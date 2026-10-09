using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PipelineReviewRegressionTests
{
    [Fact]
    public async Task UnemittedReopenUsesNewFormalApplicantForTestIndependence()
    {
        await using var f = new PipelinePromotionFixture();
        await f.InitializeAsync(4, approval: true);
        var prior = await PipelineExecutionTests.SubmittedAsync(f);
        await PipelineRecoveryTests.TimeoutAsync(f.Scenario, f.Run.Id, f.TargetStageId);
        using (var reopen = await PipelineRecoveryTests.ActionAsync(f.Scenario, f.TargetStageId, "reopen"))
            reopen.EnsureSuccessStatusCode();
        var applicant = await PipelineExecutionTests.ActorAsync(f);
        var requester = await PipelineStageEvidenceTests.DelegateAsync(f.Scenario);
        var independent = await PipelineStageEvidenceTests.DelegateAsync(f.Scenario);
        PromotionMappingRequest replacementMapping;
        await using (var db = f.Scenario.Api.Context())
        {
            var routes = await db.Set<ApiRoute>().Where(r => r.EnvironmentId == f.TargetEnvironmentId).ToArrayAsync();
            var original = f.Mapping();
            replacementMapping = original with { Routes = original.Routes.Select(r => r with
            { TargetRouteId = routes.Single(x => x.Path == f.Scenario.Source.Artifact.Content.Routes.Single(a => a.Key == r.ArtifactRouteKey).Path).Id }).ToArray() };
        }
        var current = await PipelineExecutionTests.ReadyAsync(f, applicant.Client, replacementMapping);
        Assert.NotEqual(prior.Id, current.Id);
        Assert.Equal(applicant.User.Id, current.RequestedBy);
        await f.Scenario.DeployCurrentAsync(f.Run.Id, f.Scenario.Creator);
        using (var artifact = await ApiFixture.CommandAsync(f.Scenario.Creator,
            $"/api/v1/release-pipeline-run-stages/{f.TargetStageId}/materialize-artifact"))
            artifact.EnsureSuccessStatusCode();
        var context = await PipelineStageEvidenceTests.ContextAsync(requester.Client, f.TargetStageId);
        var facts = await PipelineStageEvidenceTests.EvidenceAsync(requester.Client, f.TargetStageId, context);
        using var requested = await ApiFixture.CommandAsync(requester.Client,
            $"/api/v1/release-pipeline-run-stages/{f.TargetStageId}/acceptance-requests",
            new PipelineAcceptanceRequest(context.ContextHash, facts));
        requested.EnsureSuccessStatusCode();
        var acceptance = (await requested.Content.ReadFromJsonAsync<TestAcceptanceDto>())!;
        using var view = await applicant.Client.GetAsync($"/api/v1/test-acceptances/{acceptance.Id}");
        view.EnsureSuccessStatusCode();
        var eligibility = (await view.Content.ReadFromJsonAsync<TestAcceptanceDto>())!;
        using var denied = await PipelineStageEvidenceTests.ActAsync(applicant.Client, acceptance.Id, "accept");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.False(eligibility.CanAccept);
        await using (var db = f.Scenario.Api.Context())
            Assert.Equal("Requested", (await db.Set<ReleaseTestAcceptance>().SingleAsync(a => a.Id == acceptance.Id)).Status);
        using var allowed = await PipelineStageEvidenceTests.ActAsync(independent.Client, acceptance.Id, "accept");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Theory]
    [InlineData("Draft", false)]
    [InlineData("Draft", true)]
    [InlineData("Ready", false)]
    [InlineData("Ready", true)]
    [InlineData("WaitingApproval", false)]
    [InlineData("WaitingApproval", true)]
    public async Task CancelledUnemittedCandidateCanReopenWithoutWaitingForDeadline(string state, bool projected)
    {
        await using var f = new PipelinePromotionFixture();
        await f.InitializeAsync(approval: state != "Ready");
        var prior = state == "Draft" ? await f.PreparedAsync() : await PipelineExecutionTests.SubmittedAsync(f);
        Assert.Equal(state, prior.Status);
        using var cancel = await f.Scenario.Api.WriteAsync(HttpMethod.Post,
            $"/api/v1/release-promotions/{prior.Id}/cancel", new { }, RevisionTag.Format(prior.Revision));
        cancel.EnsureSuccessStatusCode();
        if (projected) await f.Scenario.ProjectAsync(f.Run.Id);
        using var reopen = await PipelineRecoveryTests.ActionAsync(f.Scenario, f.TargetStageId, "reopen");
        Assert.True(reopen.IsSuccessStatusCode, await reopen.Content.ReadAsStringAsync());
        await f.Scenario.ProjectAsync(f.Run.Id);
        var currentStage = await f.Scenario.Creator.GetFromJsonAsync<PipelineStageDto>($"/api/v1/release-pipeline-run-stages/{f.TargetStageId}");
        Assert.Equal("AwaitingMapping", currentStage!.Status);
        Assert.Null(currentStage.FormalPromotionId);
        Assert.False(currentStage.Eligibility!.CanReopen);
        using var prepare = await f.PrepareAsync();
        prepare.EnsureSuccessStatusCode();
        var fresh = (await prepare.Content.ReadFromJsonAsync<PromotionDto>())!;
        Assert.NotEqual(prior.Id, fresh.Id);
        Assert.Equal("Draft", fresh.Status);
        Assert.Null(fresh.Mapping);
        Assert.Null(fresh.TargetReleaseId);
    }

    [Fact]
    public async Task ThirdStageDelegateCanLoadWizardPredecessorAcceptanceWithoutFirstEnvironment()
    {
        await using var f = new PipelinePromotionFixture();
        await f.InitializeAsync();
        await PipelineExecutionTests.SubmittedAsync(f);
        await f.Scenario.DeployCurrentAsync(f.Run.Id, f.Scenario.Creator);
        var independent = await PipelineStageEvidenceTests.DelegateAsync(f.Scenario);
        await f.Scenario.PassCurrentAsync(f.Run.Id, f.Scenario.Creator, independent.Client);
        var stage = await f.Scenario.CurrentStageAsync(f.Run.Id);
        Assert.Equal(3, stage.StageOrder);
        var actor = await PipelineExecutionTests.ActorAsync(f);
        await RestrictAsync(f.Scenario, actor.User.Id,
            (f.TargetEnvironmentId, "read_only"), (stage.EnvironmentId, "read_write"));
        using var prepared = await f.PrepareAsync(stage.Id, client: actor.Client);
        prepared.EnsureSuccessStatusCode();
        var promotion = (await prepared.Content.ReadFromJsonAsync<PromotionDto>())!;
        using var read = await actor.Client.GetAsync($"/api/v1/test-acceptances/{promotion.AcceptanceId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var acceptance = (await read.Content.ReadFromJsonAsync<TestAcceptanceDto>())!;
        Assert.Equal(f.TargetEnvironmentId, acceptance.SourceEnvironmentId);
        Assert.False(acceptance.CanRevoke);
        Assert.DoesNotContain(f.Scenario.Environments[0].ToString(), await read.Content.ReadAsStringAsync());
        using var hidden = await actor.Client.GetAsync($"/api/v1/release-pipeline-run-stages/{f.Run.Stages[0].Id}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task ChainExternalEnvironmentScopeCannotClaimFullZeroRunCounts()
    {
        await using var s = new PipelineScenario();
        await s.InitializeEnvironmentsAsync();
        var run = await s.StartAsync(await PipelineStageEvidenceTests.VersionAsync(s));
        Guid extra;
        await using (var db = s.Api.Context())
        {
            var environment = new EnvironmentRecord { ProjectId = run.ProjectId, Code = "EXTRA", Name = "链外环境" };
            extra = environment.Id; db.Add(environment); await db.SaveChangesAsync();
        }
        var actor = await PipelineStageEvidenceTests.DelegateAsync(s);
        await s.GrantAsync(actor.User.Id, "project.read");
        await RestrictAsync(s, actor.User.Id, (extra, "read_only"));
        var page = await actor.Client.GetFromJsonAsync<JsonObject>($"/api/v1/release-pipeline-runs?projectId={run.ProjectId}");
        Assert.NotEqual("Full", page!["coverage"]!.GetValue<string>());
        Assert.Null(page["total"]);
        Assert.Empty(page["items"]!.AsArray());
        var overview = await actor.Client.GetFromJsonAsync<JsonObject>($"/api/v1/delivery/overview?projectId={run.ProjectId}");
        Assert.Null(overview!["pipelineCounts"]);
        Assert.NotEqual("Full", overview["pipelineVisibility"]!.GetValue<string>());
    }

    private static async Task RestrictAsync(PipelineScenario s, Guid user, params (Guid Environment, string Access)[] scopes)
    {
        await using var db = s.Api.Context();
        await db.Set<UserProjectScope>().Where(g => g.UserId == user).ExecuteDeleteAsync();
        foreach (var scope in scopes) db.Add(new UserProjectScope
        { UserId = user, OrganizationId = s.Api.Organization.Id, ProjectId = s.Api.Project.Id,
            EnvironmentId = scope.Environment, AccessMode = scope.Access });
        await db.SaveChangesAsync();
    }
}

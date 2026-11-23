using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PromotionRetainedRouteTests
{
    private const string PrivatePath = "/orders-export";

    [Fact]
    public async Task PipelineStageKeepsItsPrivateRouteOutOfTheArtifactForTheNextStage()
    {
        await using var fixture = new PipelinePromotionFixture();
        await fixture.InitializeAsync();
        var scenario = fixture.Scenario;
        var target = fixture.TargetEnvironmentId;
        var version = scenario.Api.Version.Id;
        using (var route = await scenario.Api.WriteAsync(HttpMethod.Post, $"/api/v1/environments/{target}/routes",
            new { apiVersionId = version, routeName = "中间环境专用", path = PrivatePath, methods = new[] { "GET" },
                clusterId = fixture.ClusterId, priority = 100, enabled = true, timeoutMs = 9000 }))
            route.EnsureSuccessStatusCode();
        var request = await scenario.Api.PreviewReleaseRequestAsync(0, version, target);
        using var create = await ApiFixture.CommandAsync(scenario.Creator, $"/api/v1/environments/{target}/releases", request);
        create.EnsureSuccessStatusCode();
        var baselineRelease = (await create.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
        using (var submit = await ApiFixture.CommandAsync(scenario.Creator, $"/api/v1/releases/{baselineRelease}/submit"))
            submit.EnsureSuccessStatusCode();
        await scenario.Source.Deployment.BuildAsync(baselineRelease);
        await scenario.AcknowledgeAsync(baselineRelease);

        var promotion = await PipelineExecutionTests.SubmittedAsync(fixture, mapping: fixture.Mapping(shared: true));
        await scenario.DeployCurrentAsync(fixture.Run.Id, scenario.Creator);
        var desired = await scenario.Source.Deployment.DesiredAsync(promotion.TargetReleaseId!.Value);
        var snapshot = WebApi.Domain.Runtime.SnapshotValidator.ParsePayload(desired.Payload, target);
        Assert.Contains(snapshot.Routes, route => route.Path == PrivatePath);
        using var materialized = await ApiFixture.CommandAsync(scenario.Creator,
            $"/api/v1/release-pipeline-run-stages/{fixture.TargetStageId}/materialize-artifact");
        materialized.EnsureSuccessStatusCode();
        var artifact = (await materialized.Content.ReadFromJsonAsync<ReleaseArtifactDto>())!;
        Assert.Equal(scenario.Source.Artifact.ArtifactHash, artifact.ArtifactHash);
        Assert.Equal("/orders", Assert.Single(artifact.Content.Routes).Path);
    }

    [Fact]
    public async Task PublishedPromotionKeepsTheUnmappedRouteOfTheSameVersionAndItsFrozenDependencies()
    {
        await using var fixture = new PromotionScenario();
        await fixture.InitializeAsync(authenticatedTargetReceipts: true, beforeTargetBaseline: AddPrivateRouteAsync);
        using var services = fixture.Api.Services();
        var history = services.ServiceProvider.GetRequiredService<HistoricalSnapshotService>();
        var baseline = (await history.ReadAsync(fixture.TargetEnvironmentId, 1)).Snapshot;
        var retained = Assert.Single(baseline.Routes, route => route.Path == PrivatePath);
        var mapped = Assert.Single(baseline.Routes, route => route.Id != retained.Id);
        Assert.Equal(mapped.ApiVersionId, retained.ApiVersionId);
        Assert.Equal(mapped.Path, Assert.Single(fixture.Source.Artifact.Content.Routes).Path);

        await using (var db = fixture.Api.Context())
        {
            var draft = await db.Set<ApiRoute>().SingleAsync(route => route.Id == retained.Id);
            draft.TimeoutMs = 45000;
            draft.Revision++;
            var destination = await db.Set<UpstreamDestination>().SingleAsync(row => row.ClusterId == fixture.TargetClusterId);
            destination.Address = "http://test-backend:8080/new/";
            destination.Revision++;
            await db.SaveChangesAsync();
        }
        var precheck = await PrecheckAsync(fixture, mapped.Id);
        Assert.True(precheck.CanSubmit, JsonSerializer.Serialize(precheck.Checks));
        using var submitted = await PromotionSubmissionTests.Send(fixture, "submit", etag: RevisionTag.Format(precheck.Revision));
        submitted.EnsureSuccessStatusCode();
        var releaseId = (await submitted.Content.ReadFromJsonAsync<PromotionDto>())!.TargetReleaseId!.Value;
        foreach (var role in new[] { "ApiApprover", "SecurityReviewer" })
        {
            var approver = await fixture.NewProductionApproverAsync(role);
            using var approval = await ApiFixture.CommandAsync(approver.Client, $"/api/v1/releases/{releaseId}/approve",
                new { comment = "核准目标路由映射" });
            approval.EnsureSuccessStatusCode();
        }
        await fixture.Source.Deployment.BuildAsync(releaseId);
        await fixture.TargetAckAllAsync(releaseId);
        var published = (await history.ReadAsync(fixture.TargetEnvironmentId, 2)).Snapshot;
        var actual = Assert.Single(published.Routes, route => route.Id == retained.Id);
        Assert.Equal(CanonicalJson.Serialize(retained), CanonicalJson.Serialize(actual));
        Assert.Equal(CanonicalJson.Serialize(baseline.Clusters.Single(row => row.Id == retained.ClusterId)),
            CanonicalJson.Serialize(published.Clusters.Single(row => row.Id == actual.ClusterId)));
        var policyId = Assert.Single(retained.PolicyBindings!).PolicyId;
        Assert.Equal(CanonicalJson.Serialize(baseline.Policies.Single(row => row.Id == policyId)),
            CanonicalJson.Serialize(published.Policies.Single(row => row.Id == policyId)));
        Assert.Equal(CanonicalJson.Serialize(baseline.Applications), CanonicalJson.Serialize(published.Applications));
        Assert.NotEqual(mapped.ClusterId, published.Routes.Single(route => route.Id == mapped.Id).ClusterId);
    }

    [Fact]
    public async Task MappingOptionsIdentifyTheCredentialsSharedWithAnUnmappedRouteOfTheSameApi()
    {
        await using var fixture = new PromotionScenario();
        await fixture.InitializeAsync(authenticatedTargetReceipts: true, beforeTargetBaseline: AddPrivateRouteAsync);
        var promotion = await PromotionSubmissionTests.Create(fixture);
        var options = await fixture.Api.Client.GetFromJsonAsync<PromotionMappingOptionsDto>(
            $"/api/v1/release-promotions/{promotion.Id}/mapping-options");
        var app = Assert.Single(options!.Applications!, row => row.Id == fixture.TargetApplicationId);
        Assert.Contains(fixture.Api.Api.Id, app.RetainedApiIds);
        Assert.Contains(fixture.TargetCredentialId, app.RequiredCredentialIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrecheckAllowsNewCredentialsOnlyWhenTheUnmappedRouteDoesNotUseThem(bool anonymous)
    {
        await using var fixture = new PromotionScenario();
        await fixture.InitializeAsync(authenticatedTargetReceipts: true,
            beforeTargetBaseline: target => AddPrivateRouteAsync(target, target.Api.Version.Id, anonymous));
        using var key = await fixture.Api.WriteAsync(HttpMethod.Post,
            $"/api/v1/applications/{fixture.TargetApplicationId}/credentials",
            new { validFrom = DateTimeOffset.UtcNow.AddMinutes(-1), expiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        key.EnsureSuccessStatusCode();
        var extra = (await key.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credential").GetProperty("id").GetGuid();
        await using var db = fixture.Api.Context();
        var mappedId = await db.Set<ApiRoute>().Where(route => route.EnvironmentId == fixture.TargetEnvironmentId &&
            route.Path != PrivatePath).Select(route => route.Id).SingleAsync();
        var result = await PrecheckAsync(fixture, mappedId, extra, confirmShared: !anonymous);
        if (anonymous)
        {
            Assert.True(result.CanSubmit, JsonSerializer.Serialize(result.Checks));
            using var scope = fixture.Api.Services();
            var history = scope.ServiceProvider.GetRequiredService<HistoricalSnapshotService>();
            var baseline = (await history.ReadAsync(fixture.TargetEnvironmentId, 1)).Snapshot;
            var frozen = await fixture.PrepareAsync();
            var compiled = scope.ServiceProvider.GetRequiredService<SnapshotCompiler>().Compile(frozen, baseline, 2, DateTimeOffset.UtcNow);
            var snapshot = WebApi.Domain.Runtime.SnapshotValidator.ParsePayload(compiled.Payload.Span, fixture.TargetEnvironmentId);
            Assert.False(snapshot.Routes.Single(route => route.Path == PrivatePath).RequireApiKey);
            Assert.Contains(snapshot.Applications.Single().Credentials, credential => credential.Id == extra);
            return;
        }
        Assert.False(result.CanSubmit);
        Assert.Contains(result.Checks, check => check.Blocking && check.ReasonCode == "promotion_retained_access_changed");
        Assert.Null(result.CandidateHash);
        Assert.False(await db.Set<ReleaseRecord>().AnyAsync(release => release.PromotionId == fixture.PromotionId));
    }

    [Fact]
    public async Task PrecheckBlocksReplacingTheVersionOfAnUnmappedRoute()
    {
        await using var fixture = new PromotionScenario();
        await fixture.InitializeAsync();
        await using var db = fixture.Api.Context();
        var older = new ApiVersion { ApiId = fixture.Api.Api.Id, Version = "0.9", Status = "Published",
            CreatedBy = fixture.Api.User.Id, SealedAt = DateTimeOffset.UtcNow };
        db.Add(older);
        await db.SaveChangesAsync();
        await AddPrivateRouteAsync(fixture, older.Id);
        await fixture.SeedProductionBaselineAsync(older.Id);
        var mappedId = await db.Set<ApiRoute>().Where(route => route.EnvironmentId == fixture.TargetEnvironmentId &&
            route.Path != PrivatePath).Select(route => route.Id).SingleAsync();
        var blocked = await PrecheckAsync(fixture, mappedId);
        Assert.False(blocked.CanSubmit);
        Assert.Contains(blocked.Checks, check => check.Blocking && check.ReasonCode == "promotion_retained_version_conflict");
        Assert.Null(blocked.CandidateHash);
    }

    internal static Task AddPrivateRouteAsync(PromotionScenario fixture) => AddPrivateRouteAsync(fixture, fixture.Api.Version.Id);

    private static async Task AddPrivateRouteAsync(PromotionScenario fixture, Guid versionId, bool anonymous = false)
    {
        await using var db = fixture.Api.Context();
        var route = new ApiRoute { EnvironmentId = fixture.TargetEnvironmentId, ApiVersionId = versionId, RouteName = "生产导出",
            Path = PrivatePath, NormalizedPath = PrivatePath, Methods = ["GET"], ClusterId = fixture.TargetClusterId, TimeoutMs = 9000 };
        var policy = new Policy { OrganizationId = fixture.Api.Organization.Id, ProjectId = fixture.Api.Project.Id,
            Name = "导出限流", Type = "rate_limit",
            Config = """{"algorithm":"TokenBucket","keyBy":"Route","refillTokens":1,"windowMs":1000,"burst":3,"redisFailureMode":"Reject"}""" };
        db.AddRange(route, policy, new RoutePolicyBinding { RouteId = route.Id, PolicyId = policy.Id });
        if (anonymous)
        {
            var auth = new Policy { OrganizationId = fixture.Api.Organization.Id, ProjectId = fixture.Api.Project.Id,
                Name = "公开导出", Type = "authentication", Config = """{"mode":"Anonymous"}""" };
            db.AddRange(auth, new RoutePolicyBinding { RouteId = route.Id, PolicyId = auth.Id, Priority = 1 });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<PromotionPrecheckDto> PrecheckAsync(PromotionScenario fixture, Guid mappedId, Guid? extraKey = null, bool confirmShared = true)
    {
        await PromotionSubmissionTests.Create(fixture);
        var mapping = fixture.Mapping(mappedId);
        var app = mapping.Applications.Single() with { ConfirmSharedCredentialImpact = confirmShared };
        if (extraKey is Guid id) app = app with { CredentialIds = [fixture.TargetCredentialId, id] };
        using var saved = await fixture.SaveAsync(mapping with { Applications = [app] });
        saved.EnsureSuccessStatusCode();
        using var precheck = await PromotionSubmissionTests.Send(fixture, "precheck",
            new PromotionPrecheckRequest(ConfirmUpstreamHealth: true, UpstreamHealthComment: "已核实目标上游及其依赖可用，申请独立审批。"));
        precheck.EnsureSuccessStatusCode();
        return (await precheck.Content.ReadFromJsonAsync<PromotionPrecheckDto>())!;
    }
}

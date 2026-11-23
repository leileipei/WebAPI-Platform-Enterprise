using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ReleaseLifecycleTests
{
    private static async Task<VersionDto> VersionAsync(ApiFixture api, Guid environmentId)
    {
        var detail = await api.Client.GetFromJsonAsync<ApiDetailDto>($"/api/v1/apis/{api.Api.Id}?environmentId={environmentId}");
        return Assert.Single(detail!.Versions, version => version.Id == api.Version.Id);
    }

    private static async Task ExpireAsync(ApiFixture api, Guid releaseId, bool runWorker = true)
    {
        await using var db = api.Context();
        await db.Set<ReleaseRecord>().Where(r => r.Id == releaseId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.DeadlineAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        if (runWorker)
        {
            using var services = api.Services();
            Assert.Equal(1, await services.ServiceProvider.GetRequiredService<ReleaseTimeoutService>().ExpireAsync());
        }
    }

    private static async Task<Guid> CommandAsync(HttpClient client, string path, object? body = null)
    {
        using var response = await ApiFixture.CommandAsync(client, path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("worker-timeout")]
    [InlineData("late-ack")]
    public async Task FirstFailureKeepsVersionSealedUntilRetrySucceeds(string outcome)
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var api = scenario.Api;
        var release = await scenario.PublishAsync(acknowledge: false);
        var envelope = (await scenario.DesiredAsync(release)).Envelope;
        var dispatched = await VersionAsync(api, api.Environment.Id);
        Assert.Equal("Publishing", dispatched.Status);
        Assert.NotNull(dispatched.SealedAt);
        if (outcome == "negative")
        {
            var nack = new NodeAck(scenario.Instances[0], release, envelope.ConfigVersion, envelope.DeploymentSequence,
                envelope.PayloadHash, DateTimeOffset.UtcNow, false, "invalid_snapshot");
            using var rejected = await scenario.NodeAsync(0, $"/internal/v1/nodes/{scenario.NodeIds[0]}/ack", nack);
            rejected.EnsureSuccessStatusCode();
        }
        else
        {
            await scenario.AckAsync(0, envelope);
            Assert.Equal("Publishing", (await VersionAsync(api, api.Environment.Id)).Status);
            await ExpireAsync(api, release, runWorker: outcome == "worker-timeout");
            if (outcome == "late-ack")
            {
                var ack = new NodeAck(scenario.Instances[1], release, envelope.ConfigVersion, envelope.DeploymentSequence,
                    envelope.PayloadHash, DateTimeOffset.UtcNow, true, null);
                using var late = await scenario.NodeAsync(1, $"/internal/v1/nodes/{scenario.NodeIds[1]}/ack", ack);
                Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            }
        }
        var failed = await VersionAsync(api, api.Environment.Id);
        Assert.Equal("Draft", failed.Status);
        Assert.Equal(dispatched.SealedAt, failed.SealedAt);
        using var edit = await api.WriteAsync(HttpMethod.Put, $"/api/v1/versions/{api.Version.Id}",
            new { version = "2.0.0", changeType = "breaking" }, RevisionTag.Format(failed.Revision));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Contains("version_immutable", await edit.Content.ReadAsStringAsync());

        var retry = await CommandAsync(api.Client, $"/api/v1/releases/{release}/retry");
        await scenario.BuildQueuedAsync();
        Assert.Equal("Publishing", (await VersionAsync(api, api.Environment.Id)).Status);
        await scenario.AckAllAsync(retry);
        var recovered = await VersionAsync(api, api.Environment.Id);
        Assert.Equal("Published", recovered.Status);
        Assert.Equal(dispatched.SealedAt, recovered.SealedAt);
        var original = await api.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{release}");
        Assert.Equal("Failed", original!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectVersionTracksConcurrentDispatchesInBothEnvironments(bool finishSourceFirst)
    {
        await using var s = new PromotionScenario();
        await s.InitializeAsync(authenticatedTargetReceipts: true);
        var promotion = await PromotionExecutionTests.Ready(s);
        var target = promotion.TargetReleaseId!.Value;
        Assert.Equal("Published", (await VersionAsync(s.Api, s.Api.Environment.Id)).Status);
        await s.Source.Deployment.BuildAsync(target);
        using (var first = await s.TargetAckAsync(target, 0)) first.EnsureSuccessStatusCode();
        var source = await CommandAsync(s.Api.Client, $"/api/v1/environments/{s.Api.Environment.Id}/releases",
            await s.Api.PreviewReleaseRequestAsync(1));
        await CommandAsync(s.Api.Client, $"/api/v1/releases/{source}/submit");
        await s.Source.Deployment.BuildAsync(source);
        if (finishSourceFirst) await s.Source.Deployment.AckAllAsync(source);
        else await ExpireAsync(s.Api, target);
        Assert.Equal("Publishing", (await VersionAsync(s.Api, s.Api.Environment.Id)).Status);
        Assert.Equal("Publishing", (await VersionAsync(s.Api, s.TargetEnvironmentId)).Status);
        if (finishSourceFirst)
        {
            using var failure = await s.TargetAckAsync(target, 1, false);
            failure.EnsureSuccessStatusCode();
        }
        else await s.Source.Deployment.AckAllAsync(source);
        Assert.Equal("Published", (await VersionAsync(s.Api, s.Api.Environment.Id)).Status);
        Assert.Equal("Published", (await VersionAsync(s.Api, s.TargetEnvironmentId)).Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SuccessfulRollbackRecoveryUpdatesOnlyItsOriginalSuccessfulRelease(int retries)
    {
        await using var s = new PromotionScenario();
        await s.InitializeAsync(authenticatedTargetReceipts: true);
        var promotion = await PromotionExecutionTests.Ready(s);
        var original = promotion.TargetReleaseId!.Value;
        await s.Source.Deployment.BuildAsync(original);
        await s.TargetAckAllAsync(original);
        var rollback = await CommandAsync(s.Api.Client, $"/api/v1/releases/{original}/rollback", new { targetConfigVersion = 1L });
        await CommandAsync(s.Api.Client, $"/api/v1/releases/{rollback}/submit");
        await s.Source.Deployment.ApproveAsync(rollback);
        await s.Source.Deployment.BuildAsync(rollback);

        var attempts = new List<Guid> { rollback };
        for (var n = 0; n < retries; n++)
        {
            var current = attempts[^1];
            using (var first = await s.TargetAckAsync(current, 0)) first.EnsureSuccessStatusCode();
            if (n == 0)
            {
                using var failure = await s.TargetAckAsync(current, 1, false);
                failure.EnsureSuccessStatusCode();
            }
            else await ExpireAsync(s.Api, current);
            var retry = await CommandAsync(s.Api.Client, $"/api/v1/releases/{current}/retry");
            await s.Source.Deployment.BuildQueuedAsync();
            attempts.Add(retry);
        }
        using (var partial = await s.TargetAckAsync(attempts[^1], 0)) partial.EnsureSuccessStatusCode();
        Assert.Equal("Succeeded", (await s.Api.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{original}"))!.State);
        using (var complete = await s.TargetAckAsync(attempts[^1], 1)) complete.EnsureSuccessStatusCode();
        using (var duplicate = await s.TargetAckAsync(attempts[^1], 1)) duplicate.EnsureSuccessStatusCode();
        var originalDto = (await s.Api.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{original}"))!;
        Assert.Equal("RolledBack", originalDto.State);
        Assert.Equal(2L, originalDto.DeploymentSequence);
        for (var n = 0; n < attempts.Count; n++)
        {
            var record = (await s.Api.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{attempts[n]}"))!;
            Assert.Equal(n == attempts.Count - 1 ? "Succeeded" : "Failed", record.State);
            Assert.Equal(3L + n, record.DeploymentSequence);
            Assert.Equal(1L, record.TargetConfigVersion);
            Assert.Equal(n == 0 ? original : (Guid?)null, record.RollbackOf);
            Assert.Equal(n == 0 ? null : attempts[n - 1], record.RecoveryOf);
            Assert.Equal(n == attempts.Count - 1 ? 2 : 1, record.Targets.Count(t => t.Acknowledged));
        }
        var currentPromotion = await s.Api.Client.GetFromJsonAsync<PromotionDto>($"/api/v1/release-promotions/{promotion.Id}");
        Assert.Equal("RolledBack", currentPromotion!.Status);
        Assert.Equal("Succeeded", (await s.Api.Client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{s.Source.ReleaseId}"))!.State);
    }

    private static async Task AssertEligibilityAsync(HttpClient client, Guid releaseId, Guid environmentId, bool expected)
    {
        var query = $"/api/v1/approvals?environmentId={environmentId}&view=";
        var pending = (await client.GetFromJsonAsync<ApprovalInboxPageDto>(query + "PendingMine"))!;
        var visible = (await client.GetFromJsonAsync<ApprovalInboxPageDto>(query + "AllVisible"))!;
        var detail = (await client.GetFromJsonAsync<ReleaseDto>($"/api/v1/releases/{releaseId}"))!;
        var list = (await client.GetFromJsonAsync<PageResult<ReleaseDto>>($"/api/v1/environments/{environmentId}/releases"))!;
        Assert.Equal(expected ? 1 : 0, pending.Counts.PendingMine);
        Assert.Equal(expected, pending.Page.Items.Any(r => r.Id == releaseId));
        Assert.Equal(expected, Assert.Single(visible.Page.Items, r => r.Id == releaseId).ApprovalEligibility.CanAct);
        Assert.Equal(expected, Assert.Single(list.Items, r => r.Id == releaseId).ApprovalEligibility!.CanAct);
        Assert.Equal(expected, detail.ApprovalEligibility!.CanAct);
        Assert.Equal(1, detail.ApprovalEligibility.CurrentStepOrder);
        Assert.Equal("WaitingApproval", detail.State);
    }

    [Fact]
    public async Task ApprovalInboxAndCommandsFollowPauseAndTimelyResume()
    {
        await using var f = new PipelinePromotionFixture();
        await f.InitializeAsync(2);
        var promotion = await PipelineExecutionTests.SubmittedAsync(f);
        var release = promotion.TargetReleaseId!.Value;
        var reviewer = await PipelineExecutionTests.ApproverAsync(f, "ApiApprover");
        await AssertEligibilityAsync(reviewer.Client, release, f.TargetEnvironmentId, true);
        using (var pause = await PipelineRecoveryTests.ActionAsync(f.Scenario, f.Run.Id, "pause")) pause.EnsureSuccessStatusCode();
        foreach (var action in new[] { "approve", "reject" })
        {
            using var denied = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{release}/{action}", new { comment = "暂停期间再核对" });
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            Assert.Contains("pipeline_stage_not_writable", await denied.Content.ReadAsStringAsync());
        }
        await AssertEligibilityAsync(reviewer.Client, release, f.TargetEnvironmentId, false);
        using (var resume = await PipelineRecoveryTests.ActionAsync(f.Scenario, f.Run.Id, "resume")) resume.EnsureSuccessStatusCode();
        await AssertEligibilityAsync(reviewer.Client, release, f.TargetEnvironmentId, true);
        await CommandAsync(reviewer.Client, $"/api/v1/releases/{release}/approve", new { comment = "恢复后办理" });
        var history = (await reviewer.Client.GetFromJsonAsync<ApprovalInboxPageDto>($"/api/v1/approvals?environmentId={f.TargetEnvironmentId}&view=HandledMine"))!;
        Assert.Contains(history.Page.Items, r => r.Id == release && !r.ApprovalEligibility.CanAct);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("different-stage")]
    [InlineData("finished-attempt")]
    [InlineData("missing-release-binding")]
    [InlineData("replaced-policy")]
    [InlineData("changed-approval")]
    [InlineData("changed-seats")]
    [InlineData("revoked-predecessor")]
    public async Task UnavailablePipelineAttemptCannotOfferAnApproval(string change)
    {
        await using var f = new PipelinePromotionFixture();
        await f.InitializeAsync(2);
        var promotion = await PipelineExecutionTests.SubmittedAsync(f);
        var reviewer = await PipelineExecutionTests.ApproverAsync(f, "ApiApprover");
        await using (var db = f.Scenario.Api.Context())
        {
            var attempt = await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a => a.RunStageId == f.TargetStageId);
            if (change == "expired")
            {
                attempt.ActivatedAt = DateTimeOffset.UtcNow.AddHours(-2);
                attempt.DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }
            if (change == "finished-attempt") attempt.Status = "TimedOut";
            if (change == "missing-release-binding") attempt.ActualReleaseId = null;
            if (change == "different-stage") (await db.Set<ReleasePipelineRun>().SingleAsync(r => r.Id == f.Run.Id)).CurrentStageOrder = 1;
            if (change == "replaced-policy") (await db.Set<ProjectDeliveryPolicy>().SingleAsync(p => p.ProjectId == f.Run.ProjectId)).Revision++;
            if (change == "changed-approval") (await db.Set<ApprovalFlow>().SingleAsync()).Revision++;
            if (change == "changed-seats") (await db.Set<ApprovalStep>().FirstAsync(s => s.StepOrder == 1)).RequiredCount++;
            if (change == "revoked-predecessor") (await db.Set<ReleaseTestAcceptance>().SingleAsync(a => a.Id == f.AcceptanceId)).Status = "Revoked";
            await db.SaveChangesAsync();
        }
        using var denied = await ApiFixture.CommandAsync(reviewer.Client, $"/api/v1/releases/{promotion.TargetReleaseId}/reject", new { comment = "核对当前阶段" });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        await AssertEligibilityAsync(reviewer.Client, promotion.TargetReleaseId!.Value, f.TargetEnvironmentId, false);
    }

    [Theory]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "non-utf8")]
    [InlineData(true, "bad-json")]
    [InlineData(false, "snapshot-hash")]
    [InlineData(true, "missing-ack")]
    public async Task UpgradeRepairsProvenHistoricalOutcomesAndPreservesFailedAttemptsAndSeals(bool sqlEntry, string evidence)
    {
        await using var s = new DeploymentScenario();
        await s.InitializeAsync(targetMigration: "20261119150000_FormalRotationDecision");
        var first = await s.PublishAsync();
        var second = await s.PublishAsync();
        var rollback = await CommandAsync(s.Api.Client, $"/api/v1/releases/{second}/rollback", new { targetConfigVersion = 1 });
        await CommandAsync(s.Api.Client, $"/api/v1/releases/{rollback}/submit");
        await s.ApproveAsync(rollback);
        await s.BuildAsync(rollback);
        await ExpireAsync(s.Api, rollback);
        var retry = await CommandAsync(s.Api.Client, $"/api/v1/releases/{rollback}/retry");
        await s.BuildQueuedAsync();
        await s.AckAllAsync(retry);
        var third = await s.PublishAsync(acknowledge: false);
        await ExpireAsync(s.Api, third);
        var fourth = await s.PublishAsync(acknowledge: false);

        await using var db = s.Api.Context();
        // Previous builds recorded version items only for submitted releases and left these stale projections.
        await db.Set<ReleaseItem>().Where(i => i.ReleaseId == retry).ExecuteDeleteAsync();
        await db.Set<ReleaseRecord>().Where(r => r.Id == second).ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, "Succeeded"));
        await db.Set<ApiVersion>().ExecuteUpdateAsync(set => set.SetProperty(v => v.Status, "Publishing"));
        if (evidence is "non-utf8" or "bad-json")
        {
            byte[] invalid = evidence == "non-utf8" ? [0xff, 0xfe] : "{"u8.ToArray();
            await db.Set<ReleaseRecord>().Where(r => r.Id == retry).ExecuteUpdateAsync(set => set.SetProperty(r => r.CandidateBytes, invalid));
        }
        if (evidence == "snapshot-hash")
            await db.Set<GatewayConfigVersion>().Where(v => v.EnvironmentId == s.Api.Environment.Id && v.VersionNo == 1)
                .ExecuteUpdateAsync(set => set.SetProperty(v => v.SnapshotHash, new string('0', 64)));
        if (evidence == "missing-ack")
            await db.Set<GatewayAck>().Where(a => a.ReleaseId == retry && a.NodeId == s.NodeIds[1]).ExecuteDeleteAsync();
        var seals = await db.Set<ApiVersion>().AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.SealedAt);
        var receipts = await db.Set<GatewayAck>().CountAsync();
        for (var n = 0; n < 2; n++)
        {
            if (sqlEntry)
            {
                var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "initial-core.sql"));
                await using var connection = await s.Api.Database.OpenAsync();
                await using var command = new NpgsqlCommand(script, connection);
                await command.ExecuteNonQueryAsync();
            }
            else await db.Database.MigrateAsync();
        }

        var records = await db.Set<ReleaseRecord>().AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.Equal(evidence == "valid" ? "RolledBack" : "Succeeded", records[second].Status);
        Assert.Equal("Succeeded", records[first].Status);
        Assert.Equal("Failed", records[rollback].Status);
        Assert.Equal("Succeeded", records[retry].Status);
        Assert.Equal("Failed", records[third].Status);
        Assert.Equal("Publishing", records[fourth].Status);
        Assert.Equal(second, records[rollback].RollbackOf);
        Assert.Equal(rollback, records[retry].RecoveryOf);
        var versions = await db.Set<ApiVersion>().AsNoTracking().OrderBy(v => v.Version).ToArrayAsync();
        Assert.Equal(new[] { "Published", "Published", "Draft", "Publishing" }, versions.Select(v => v.Status));
        Assert.All(versions, v => Assert.Equal(seals[v.Id], v.SealedAt));
        Assert.Equal(receipts, await db.Set<GatewayAck>().CountAsync());
        Assert.Equal(evidence is "valid" or "missing-ack" ? 1 : 0, await db.Set<ReleaseItem>().CountAsync(i => i.ReleaseId == retry));
    }
}

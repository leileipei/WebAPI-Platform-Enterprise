using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class RunningDeploymentReadinessTests
{
    private static string Address(ApiFixture api) =>
        $"/api/v1/environments/{api.Environment.Id}/apis/{api.Api.Id}/access-addresses?view=running";

    private static async Task AssertUnconfirmedAsync(params HttpResponseMessage[] responses)
    {
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        foreach (var response in responses)
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("running_state_unconfirmed", error.GetProperty("code").GetString());
        }
    }

    private static async Task HeartbeatAsync(DeploymentScenario scenario, Guid instance, SnapshotEnvelope envelope, string status)
    {
        using var response = await scenario.NodeAsync(0, $"/internal/v1/nodes/{scenario.NodeIds[0]}/heartbeat",
            new NodeHeartbeat(instance, envelope.ConfigVersion, envelope.DeploymentSequence, status));
        response.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("NotReady")]
    [InlineData("Degraded")]
    public async Task RunningAddressesAndArtifactCreationWaitForEveryEnabledNodeToRecover(string status)
    {
        await using var scenario = new DeploymentScenario(nodeCount: 3);
        await scenario.InitializeAsync();
        var api = scenario.Api;
        await using (var db = api.Context())
            await db.Set<EnvironmentRecord>().Where(e => e.Id == api.Environment.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsProduction, false)
                    .SetProperty(e => e.GatewayPublicUrl, "https://source.example"));

        var release = await scenario.PublishAsync();
        var envelope = (await scenario.DesiredAsync(release)).Envelope;
        var artifactUrl = $"/api/v1/releases/{release}/artifacts";
        await HeartbeatAsync(scenario, scenario.Instances[0], envelope, status);
        using var address = await api.Client.GetAsync(Address(api));
        using var creation = await ApiFixture.CommandAsync(api.Client, artifactUrl);
        await AssertUnconfirmedAsync(address, creation);
        await using (var db = api.Context())
        {
            Assert.False(await db.Set<ReleaseArtifact>().AnyAsync(a => a.SourceReleaseId == release));
            Assert.Equal("Succeeded", await db.Set<ReleaseRecord>().Where(r => r.Id == release).Select(r => r.Status).SingleAsync());
            Assert.Equal(3, await db.Set<GatewayAck>().CountAsync(a => a.ReleaseId == release && a.Success));
        }

        var nodeUrl = $"/api/v1/gateway-nodes/{scenario.NodeIds[0]}";
        using var node = await api.Client.GetAsync(nodeUrl);
        node.EnsureSuccessStatusCode();
        using var disabled = await api.WriteAsync(HttpMethod.Put, nodeUrl + "/enabled", new { enabled = false }, node.Headers.ETag!.Tag);
        disabled.EnsureSuccessStatusCode();
        using var otherTwoReady = await api.Client.GetAsync(Address(api));
        otherTwoReady.EnsureSuccessStatusCode();
        using var enabled = await api.WriteAsync(HttpMethod.Put, nodeUrl + "/enabled", new { enabled = true }, disabled.Headers.ETag!.Tag);
        enabled.EnsureSuccessStatusCode();
        using var backInScope = await api.Client.GetAsync(Address(api));
        await AssertUnconfirmedAsync(backInScope);

        await HeartbeatAsync(scenario, scenario.Instances[0], envelope, "Ready");
        var recovered = await api.Client.GetFromJsonAsync<EnvironmentApiAccessDto>(Address(api));
        Assert.Equal("https://source.example/orders", Assert.Single(recovered!.Routes).PublicTemplate);
        using var created = await ApiFixture.CommandAsync(api.Client, artifactUrl);
        created.EnsureSuccessStatusCode();
        Assert.Equal(envelope.PayloadHash, (await created.Content.ReadFromJsonAsync<ReleaseArtifactDto>())!.SourceSnapshotHash);
    }

    [Fact]
    public async Task FirstDeploymentRequiresAllAcknowledgementsAndRestartRequiresTheCurrentInstanceReceipt()
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var api = scenario.Api;
        using var noBaseline = await api.Client.GetAsync(Address(api));
        await AssertUnconfirmedAsync(noBaseline);
        var release = await scenario.PublishAsync(acknowledge: false);
        var envelope = (await scenario.DesiredAsync(release)).Envelope;
        await scenario.AckAsync(0, envelope);
        using var partial = await api.Client.GetAsync(Address(api));
        await AssertUnconfirmedAsync(partial);
        await scenario.AckAsync(1, envelope);
        using var allConfirmed = await api.Client.GetAsync(Address(api));
        allConfirmed.EnsureSuccessStatusCode();

        var instance = Guid.NewGuid();
        var nodeId = scenario.NodeIds[0];
        using var registration = await scenario.NodeAsync(0, "/internal/v1/nodes/register",
            new RegisterNodeRequest(api.Environment.Id, "node-0", instance, "test"));
        registration.EnsureSuccessStatusCode();
        await HeartbeatAsync(scenario, instance, envelope, "Ready");
        using var heartbeatOnly = await api.Client.GetAsync(Address(api));
        await AssertUnconfirmedAsync(heartbeatOnly);
        using var application = await scenario.NodeAsync(0, $"/internal/v1/nodes/{nodeId}/ack",
            new NodeAck(instance, release, envelope.ConfigVersion, envelope.DeploymentSequence, envelope.PayloadHash, DateTimeOffset.UtcNow, true, null));
        application.EnsureSuccessStatusCode();
        using var restarted = await api.Client.GetAsync(Address(api));
        restarted.EnsureSuccessStatusCode();
        await HeartbeatAsync(scenario, instance, envelope, "Degraded");
        using var degraded = await api.Client.GetAsync(Address(api));
        await AssertUnconfirmedAsync(degraded);
        await HeartbeatAsync(scenario, instance, envelope, "Ready");
        using var recovered = await api.Client.GetAsync(Address(api));
        recovered.EnsureSuccessStatusCode();
        await using var db = api.Context();
        var frozen = scenario.Instances[0].ToString();
        Assert.Equal(frozen, await db.Set<ReleaseTarget>().Where(t => t.ReleaseId == release && t.NodeId == nodeId).Select(t => t.InstanceId).SingleAsync());
        Assert.Equal(frozen, await db.Set<GatewayAck>().Where(a => a.ReleaseId == release && a.NodeId == nodeId).Select(a => a.InstanceId).SingleAsync());
    }

    [Fact]
    public async Task ProductionVerificationCannotCompleteUntilTheReportingNodeIsReadyAgain()
    {
        await using var scenario = new PromotionScenario();
        await scenario.InitializeAsync(authenticatedTargetReceipts: true);
        var promotion = await ProductionVerificationTests.Deployed(scenario);
        var verifier = await ProductionVerificationTests.Verifier(scenario);
        var url = $"/api/v1/release-promotions/{promotion.Id}/verification-context";
        var before = (await verifier.Client.GetFromJsonAsync<ProductionVerificationContextDto>(url))!;
        foreach (var type in ProductionVerificationTests.Types.Take(2))
        {
            using var recorded = await ProductionVerificationTests.Record(scenario, verifier.Client, type, expected: before.Hash);
            recorded.EnsureSuccessStatusCode();
        }

        var heartbeatUrl = $"/internal/v1/nodes/{scenario.TargetNodeIds[0]}/heartbeat";
        var heartbeat = new NodeHeartbeat(scenario.TargetInstances[0], before.ConfigVersion, before.DeploymentSequence, "NotReady", ["2.0", "2.1", "2.2"]);
        using var down = await scenario.TargetNodeAsync(0, heartbeatUrl, heartbeat);
        down.EnsureSuccessStatusCode();
        using var context = await verifier.Client.GetAsync(url);
        using var blocked = await ProductionVerificationTests.Record(scenario, verifier.Client, ProductionVerificationTests.Types[2], expected: before.Hash);
        await AssertUnconfirmedAsync(context, blocked);
        await using (var db = scenario.Api.Context())
        {
            var row = await db.Set<ReleasePromotion>().SingleAsync(p => p.Id == promotion.Id);
            Assert.Equal("Verifying", row.Status);
            Assert.Null(row.CompletedAt);
            Assert.Equal(2, await db.Set<ReleaseVerification>().CountAsync(v => v.PromotionId == promotion.Id));
            Assert.Equal("Succeeded", await db.Set<ReleaseRecord>().Where(r => r.Id == before.ReleaseId).Select(r => r.Status).SingleAsync());
        }

        using var up = await scenario.TargetNodeAsync(0, heartbeatUrl, heartbeat with { Status = "Ready" });
        up.EnsureSuccessStatusCode();
        Assert.Equal(before.Hash, await ProductionVerificationTests.ContextHash(scenario, verifier.Client));
        using var final = await ProductionVerificationTests.Record(scenario, verifier.Client, ProductionVerificationTests.Types[2], expected: before.Hash);
        final.EnsureSuccessStatusCode();
        Assert.Equal("Completed", (await final.Content.ReadFromJsonAsync<PromotionDto>())!.Status);
        await using var verify = scenario.Api.Context();
        Assert.Equal(3, await verify.Set<ReleaseVerification>().CountAsync(v => v.PromotionId == promotion.Id && v.CreatedBy == verifier.User.Id));
    }

    [Fact]
    public async Task PipelineProjectsCompletedProductionEvidenceOnlyWhileEveryNodeIsReady()
    {
        await using var fixture = new PipelinePromotionFixture();
        await fixture.InitializeAsync(2);
        var scenario = fixture.Scenario;
        var promotion = await PipelineExecutionTests.ReadyAsync(fixture);
        await PipelineExecutionTests.SeedSucceededAfterRealBuildAsync(fixture, promotion);
        var verifier = await PipelineExecutionTests.ApproverAsync(fixture, "PipelineCurrentVerifier");
        var url = $"/api/v1/release-promotions/{promotion.Id}";
        var context = (await verifier.Client.GetFromJsonAsync<ProductionVerificationContextDto>(url + "/verification-context"))!;
        foreach (var type in ProductionVerificationTests.Types)
        {
            var now = DateTimeOffset.UtcNow;
            using var recorded = await ApiFixture.CommandAsync(verifier.Client, url + "/verifications",
                new RecordVerificationRequest(type, "Passed", now, now, ExpectedContextHash: context.Hash));
            recorded.EnsureSuccessStatusCode();
        }

        await using var db = scenario.Api.Context();
        var node = await db.Set<GatewayNode>().AsNoTracking().Where(n => n.EnvironmentId == fixture.TargetEnvironmentId).OrderBy(n => n.Id).FirstAsync();
        async Task ReportAsync(string status)
        {
            using var services = scenario.Api.Services();
            await services.ServiceProvider.GetRequiredService<NodeRegistry>().HeartbeatAsync(new NodeIdentity(node.Id, node.EnvironmentId, node.IdentityHash),
                new NodeHeartbeat(Guid.Parse(node.InstanceId), context.ConfigVersion, context.DeploymentSequence, status));
        }

        await ReportAsync("Degraded");
        await scenario.ProjectAsync(fixture.Run.Id);
        Assert.Equal("AwaitingVerification", await db.Set<ReleasePipelineRunStage>().Where(s => s.Id == fixture.TargetStageId).Select(s => s.Status).SingleAsync());
        Assert.Equal("Active", await db.Set<ReleasePipelineRun>().Where(r => r.Id == fixture.Run.Id).Select(r => r.Status).SingleAsync());
        Assert.Equal("Completed", await db.Set<ReleasePromotion>().Where(p => p.Id == promotion.Id).Select(p => p.Status).SingleAsync());
        await ReportAsync("Ready");
        await scenario.ProjectAsync(fixture.Run.Id);
        Assert.Equal("Passed", await db.Set<ReleasePipelineRunStage>().Where(s => s.Id == fixture.TargetStageId).Select(s => s.Status).SingleAsync());
        Assert.Equal("Completed", await db.Set<ReleasePipelineRun>().Where(r => r.Id == fixture.Run.Id).Select(r => r.Status).SingleAsync());
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PromotionSnapshotCapabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneLegacyNodeBlocksTheMappedOrRetainedTwoPointOnePolicy(bool retainedRateLimit)
    {
        await using var scenario = new PromotionScenario();
        if (retainedRateLimit)
            await scenario.InitializeAsync(authenticatedTargetReceipts: true,
                beforeTargetBaseline: PromotionRetainedRouteTests.AddPrivateRouteAsync);
        else
            await InitializeWithSourceCircuitBreakerAsync(scenario);

        await PromotionSubmissionTests.Create(scenario);
        var path = Assert.Single(scenario.Source.Artifact.Content.Routes).Path;
        await using (var db = scenario.Api.Context())
        {
            var mapped = await db.Set<ApiRoute>().SingleAsync(r => r.EnvironmentId == scenario.TargetEnvironmentId && r.Path == path);
            var mapping = scenario.Mapping(mapped.Id);
            var application = Assert.Single(mapping.Applications) with { ConfirmSharedCredentialImpact = retainedRateLimit };
            using var saved = await scenario.SaveAsync(mapping with { Applications = [application] });
            saved.EnsureSuccessStatusCode();
        }
        await HeartbeatAsync(scenario, 1, "2.0");
        using var response = await PromotionSubmissionTests.Send(scenario, "precheck",
            new PromotionPrecheckRequest(ConfirmUpstreamHealth: true, UpstreamHealthComment: "已确认目标环境上游及其依赖正常运行。"));
        response.EnsureSuccessStatusCode();
        var check = (await response.Content.ReadFromJsonAsync<PromotionPrecheckDto>())!;
        Assert.False(check.CanSubmit);
        Assert.Equal("Blocked", check.Status);
        Assert.Contains(check.Checks, item => item.Blocking && item.Status == "Failed" && item.ReasonCode == "gateway_schema_unsupported");
        using var denied = await PromotionSubmissionTests.Send(scenario, "submit", etag: RevisionTag.Format(check.Revision));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        await AssertNoApprovalAsync(scenario);
    }

    [Fact]
    public async Task SubmitRechecksCapabilitiesAndStartsApprovalOnlyAfterSupportReturns()
    {
        await using var scenario = new PromotionScenario();
        await InitializeWithSourceCircuitBreakerAsync(scenario);
        var check = await PromotionSubmissionTests.Prepared(scenario);
        var etag = RevisionTag.Format(check.Revision);
        await HeartbeatAsync(scenario, 0, "2.0", "2.2");
        using var denied = await PromotionSubmissionTests.Send(scenario, "submit", etag: etag);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        await AssertNoApprovalAsync(scenario);

        await HeartbeatAsync(scenario, 0, "2.0", "2.1");
        using var accepted = await PromotionSubmissionTests.Send(scenario, "submit", etag: etag);
        accepted.EnsureSuccessStatusCode();
        var promotion = (await accepted.Content.ReadFromJsonAsync<PromotionDto>())!;
        Assert.Equal("WaitingApproval", promotion.Status);
        await using var db = scenario.Api.Context();
        Assert.Equal(2, await db.Set<ApprovalTask>().CountAsync(t => t.ReleaseId == promotion.TargetReleaseId && t.Status == "Pending"));
    }

    [Fact]
    public async Task LegacyCandidateCanEnterApprovalWithTwoLegacyNodes()
    {
        await using var scenario = new PromotionScenario();
        await scenario.InitializeAsync(authenticatedTargetReceipts: true);
        for (var i = 0; i < scenario.TargetNodeIds.Length; i++) await HeartbeatAsync(scenario, i, "2.0");
        var promotion = await PromotionSubmissionTests.Submitted(scenario);
        Assert.Equal("WaitingApproval", promotion.Status);
    }

    private static Task InitializeWithSourceCircuitBreakerAsync(PromotionScenario scenario) =>
        scenario.InitializeAsync(authenticatedTargetReceipts: true, configureSourceAuthentication: async api =>
        {
            var deployment = scenario.Source.Deployment;
            for (var i = 0; i < deployment.NodeIds.Length; i++)
            {
                using var registration = await deployment.NodeAsync(i, "/internal/v1/nodes/register",
                    new RegisterNodeRequest(api.Environment.Id, "node-" + i, deployment.Instances[i], "test", ["2.0", "2.1"]));
                registration.EnsureSuccessStatusCode();
            }
            await using var db = api.Context();
            var route = await db.Set<ApiRoute>().SingleAsync();
            var policy = new Policy
            {
                OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, Name = "来源熔断", Type = "circuit_breaker",
                Config = """{"samplingWindowMs":30000,"minimumRequests":20,"failureRatio":0.5,"openDurationMs":30000,"halfOpenMaxRequests":1,"halfOpenSuccesses":3,"failureStatusCodes":[503],"countTimeouts":true,"countConnectionFailures":true}"""
            };
            db.AddRange(policy, new RoutePolicyBinding { RouteId = route.Id, PolicyId = policy.Id });
            await db.SaveChangesAsync();
        });

    private static async Task HeartbeatAsync(PromotionScenario scenario, int index, params string[] schemas)
    {
        using var response = await scenario.TargetNodeAsync(index, $"/internal/v1/nodes/{scenario.TargetNodeIds[index]}/heartbeat",
            new NodeHeartbeat(scenario.TargetInstances[index], 1, 1, "Ready", schemas));
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssertNoApprovalAsync(PromotionScenario scenario)
    {
        await using var db = scenario.Api.Context();
        var draft = await db.Set<ReleasePromotion>().SingleAsync(p => p.Id == scenario.PromotionId);
        Assert.Equal("Draft", draft.Status);
        Assert.Null(draft.TargetReleaseId);
        Assert.False(await db.Set<ReleaseRecord>().AnyAsync(r => r.PromotionId == draft.Id));
        Assert.False(await db.Set<ApprovalTask>().AnyAsync(t => t.Status == "Pending"));
        Assert.False(await db.Set<ReleasePromotionEvent>().AnyAsync(e => e.PromotionId == draft.Id && e.ReasonCode == "promotion_submitted"));
    }
}

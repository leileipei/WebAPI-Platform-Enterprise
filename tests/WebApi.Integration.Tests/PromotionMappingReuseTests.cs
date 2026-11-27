using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PromotionMappingReuseTests
{
    [Fact]
    public async Task RepeatedSaveAndPrecheckKeepsOnePrivatePolicyAndTheSameCandidate()
    {
        await using var scenario = new PromotionScenario();
        await scenario.InitializeAsync(timeout: true);
        var first = await PromotionSubmissionTests.Prepared(scenario);
        await using var db = scenario.Api.Context();
        var route = await db.Set<ApiRoute>().AsNoTracking().SingleAsync(r => r.EnvironmentId == scenario.TargetEnvironmentId);
        var privateId = await db.Set<RoutePolicyBinding>().Where(b => b.RouteId == route.Id).Select(b => b.PolicyId).SingleAsync();
        var mapping = scenario.Mapping(route.Id);
        var revision = first.Revision;

        for (var i = 0; i < 2; i++)
        {
            using var saved = await scenario.SaveAsync(mapping, RevisionTag.Format(revision));
            saved.EnsureSuccessStatusCode();
            var unchanged = (await saved.Content.ReadFromJsonAsync<PromotionDto>())!;
            Assert.Equal(revision, unchanged.Revision);
            Assert.Equal(first.MappingRevision, unchanged.MappingRevision);
            Assert.Equal(first.CandidateHash, unchanged.CandidateHash);
            using var response = await PromotionSubmissionTests.Send(scenario, "precheck",
                new PromotionPrecheckRequest(ConfirmUpstreamHealth: true, UpstreamHealthComment: "人工确认目标后端与依赖已准备就绪，等待独立生产审批"));
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<PromotionPrecheckDto>())!;
            Assert.True(result.CanSubmit);
            Assert.Equal(first.CandidateHash, result.CandidateHash);
            revision = result.Revision;
        }

        var prefix = $"Promotion-{scenario.PromotionId:N}-";
        Assert.Equal(privateId, await db.Set<Policy>().Where(p => p.Name.StartsWith(prefix)).Select(p => p.Id).SingleAsync());
        Assert.Equal(privateId, await db.Set<RoutePolicyBinding>().Where(b => b.RouteId == route.Id).Select(b => b.PolicyId).SingleAsync());
        Assert.Equal(route.Revision, await db.Set<ApiRoute>().Where(r => r.Id == route.Id).Select(r => r.Revision).SingleAsync());
        var options = await scenario.Api.Client.GetFromJsonAsync<PromotionMappingOptionsDto>(
            $"/api/v1/release-promotions/{scenario.PromotionId}/mapping-options");
        Assert.Equal(privateId, Assert.Single(options!.Policies!, p => p.Name.StartsWith(prefix, StringComparison.Ordinal)).Id);
        Assert.Single(await db.Set<ReleaseRecord>().Where(r => r.EnvironmentId == scenario.TargetEnvironmentId).ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentIdenticalSavesAndPrechecksReuseTheExistingBinding()
    {
        await using var scenario = new PromotionScenario();
        await scenario.InitializeAsync(timeout: true);
        var first = await PromotionSubmissionTests.Prepared(scenario);
        await using var db = scenario.Api.Context();
        var routeId = await db.Set<ApiRoute>().Where(r => r.EnvironmentId == scenario.TargetEnvironmentId).Select(r => r.Id).SingleAsync();
        var mapping = scenario.Mapping(routeId);
        var etag = RevisionTag.Format(first.Revision);
        var saves = await Task.WhenAll(scenario.SaveAsync(mapping, etag), scenario.SaveAsync(mapping, etag));
        foreach (var saved in saves) using (saved) saved.EnsureSuccessStatusCode();
        var checks = await Task.WhenAll(PromotionSubmissionTests.Send(scenario, "precheck"), PromotionSubmissionTests.Send(scenario, "precheck"));
        foreach (var check in checks) using (check)
        {
            check.EnsureSuccessStatusCode();
            Assert.Equal(first.CandidateHash, (await check.Content.ReadFromJsonAsync<PromotionPrecheckDto>())!.CandidateHash);
        }
        var prefix = $"Promotion-{scenario.PromotionId:N}-";
        Assert.Equal(1, await db.Set<Policy>().CountAsync(p => p.Name.StartsWith(prefix) && p.Enabled));
    }

    [Fact]
    public async Task OldUnpreparedMappingCanRecoverItsBoundCopyWithoutTouchingOtherPolicies()
    {
        await using var scenario = new PromotionScenario();
        await scenario.InitializeAsync(timeout: true, configureSourceAuthentication: async api =>
        {
            await using var source = api.Context();
            var route = await source.Set<ApiRoute>().SingleAsync();
            var auth = new Policy { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id,
                Name = "Source authentication", Type = "authentication", Config = "{\"mode\":\"ApiKey\"}" };
            source.Add(auth);
            // A different policy type may use the same priority as the timeout policy.
            source.Add(new RoutePolicyBinding { RouteId = route.Id, PolicyId = auth.Id, Priority = 7 });
            await source.SaveChangesAsync();
        });
        var first = await PromotionSubmissionTests.Prepared(scenario);
        await using var db = scenario.Api.Context();
        var row = await db.Set<ReleasePromotionMapping>().SingleAsync(r => r.PromotionId == scenario.PromotionId && r.Kind == "Route");
        var routeId = row.TargetRouteId!.Value;
        var privateIds = await db.Set<RoutePolicyBinding>().Where(b => b.RouteId == routeId).Select(b => b.PolicyId).OrderBy(id => id).ToArrayAsync();
        Assert.Equal(2, privateIds.Length);
        var mapping = scenario.Mapping(routeId);
        // The previous save implementation retained the route ID but discarded its preparation metadata.
        row.ParametersJson = JsonSerializer.Serialize(new { mapping = mapping.Routes[0], preparedRouteId = routeId }, CanonicalJson.Options);
        var unrelated = new Policy { OrganizationId = scenario.Api.Organization.Id, ProjectId = scenario.Api.Project.Id,
            Name = $"Promotion-{scenario.PromotionId:N}-{Guid.NewGuid():N}", Type = "timeout", Config = "{\"timeoutMs\":10000}" };
        db.Add(unrelated);
        var otherRoute = new ApiRoute { EnvironmentId = scenario.TargetEnvironmentId, ApiVersionId = scenario.Api.Version.Id,
            RouteName = "Other", Path = "/other", NormalizedPath = "/other", Methods = ["GET"], ClusterId = scenario.TargetClusterId, TimeoutMs = 10000 };
        db.Add(otherRoute);
        db.Add(new RoutePolicyBinding { RouteId = otherRoute.Id, PolicyId = unrelated.Id, Priority = 99 });
        await db.SaveChangesAsync();

        using var saved = await scenario.SaveAsync(mapping, RevisionTag.Format(first.Revision));
        saved.EnsureSuccessStatusCode();
        var candidate = await scenario.PrepareAsync();
        Assert.Equal(privateIds, candidate.Policies.Select(p => p.Id).Order());
        Assert.True(await db.Set<Policy>().AnyAsync(p => p.Id == unrelated.Id && p.Enabled && p.VersionNo == 1));
        Assert.True(await db.Set<RoutePolicyBinding>().AnyAsync(b => b.PolicyId == unrelated.Id && b.RouteId == otherRoute.Id));
        Assert.True(await db.Set<Policy>().AnyAsync(p => p.Id == scenario.TargetPolicyId && p.Enabled && p.VersionNo == 1));
    }
}

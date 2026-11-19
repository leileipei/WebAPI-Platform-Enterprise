using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Routing;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Releases;

internal sealed class GovernedRollbackCandidate(HistoricalSnapshotService history, ReleaseCandidateBuilder candidates,
    SnapshotCompiler compiler)
{
    internal async Task<FrozenReleaseCandidate> BuildAsync(EnvironmentRecord environment, long target, CancellationToken ct)
    {
        if (environment.DesiredConfigVersion is not > 0 || environment.DeploymentSequence <= 0)
            throw HistoryUnavailable();
        var baselineVersion = environment.DesiredConfigVersion.Value;
        var baseline = (await history.ReadAsync(environment.Id, baselineVersion, ct)).Snapshot;
        var source = await history.ReadGovernedRollbackSourceAsync(environment.Id, target, compiler, ct);
        var historical = source.Snapshot;
        if (baseline.SchemaVersion != "2.4" || !Same(AuthorizationSurface(historical), AuthorizationSurface(baseline)))
            throw AuthorizationChanged();

        var versions = historical.Routes.Select(route => route.ApiVersionId).Distinct().Order().ToArray();
        var current = await candidates.PreviewAsync(environment.Id, new(baselineVersion, versions), ct);
        if (current.Governance is null) throw ReleaseCandidateBuilder.GovernanceRequired();
        await history.RequireSealedRollbackContractsAsync(historical, current.Versions, ct);

        var compiledCurrent = compiler.Compile(current, baseline, checked(baselineVersion + 1), baseline.GeneratedAt);
        var currentRuntime = SnapshotValidator.ParsePayload(compiledCurrent.Payload.Span, environment.Id);
        if (!Same(AuthorizationSurface(historical), AuthorizationSurface(currentRuntime)))
            throw AuthorizationChanged();

        var clusterIds = historical.Clusters.ToDictionary(cluster => cluster.Id, cluster => cluster.SourceId);
        var policyIds = historical.Policies.ToDictionary(policy => policy.Id, policy => policy.SourcePolicyId);
        var currentClusters = current.Clusters.ToDictionary(cluster => cluster.Id);
        var currentPolicies = current.Policies.ToDictionary(policy => policy.Id);
        if (clusterIds.Values.Distinct().Count() != historical.Clusters.Count ||
            policyIds.Values.Distinct().Count() != historical.Policies.Count ||
            clusterIds.Values.Any(id => id is not Guid native || !currentClusters.ContainsKey(native)) ||
            policyIds.Values.Any(id => id is not Guid native || !currentPolicies.ContainsKey(native)))
            throw HistoryUnavailable();

        var currentRoutes = current.Routes.ToDictionary(route => route.Id);
        var originalRoutes = baseline.Routes.ToDictionary(route => route.Id);
        var changedRoutes = historical.Routes.Select(route =>
        {
            var original = originalRoutes[route.Id];
            return new GovernedRollbackRouteImpact(route.Id, route.ClusterId != original.ClusterId,
                route.TimeoutMs != original.TimeoutMs, !Same(route.PolicyBindings, original.PolicyBindings));
        }).Where(route => route.ClusterChanged || route.TimeoutChanged || route.PoliciesChanged).ToArray();

        var rebuilt = current with
        {
            Routes = historical.Routes.OrderBy(route => route.Id).Select(route => currentRoutes[route.Id] with
            {
                ClusterId = clusterIds[route.ClusterId]!.Value,
                TimeoutMs = route.TimeoutMs,
                EffectiveTimeoutMs = route.TimeoutMs
            }).ToArray(),
            Clusters = historical.Clusters.Select(cluster => RebuildCluster(cluster, currentClusters[cluster.SourceId!.Value]))
                .OrderBy(cluster => cluster.Id).ToArray(),
            Policies = historical.Policies.Select(policy => new FrozenPolicy(policy.SourcePolicyId!.Value, policy.Type,
                policy.Config, true, policy.SourceRevision ?? throw HistoryUnavailable())).OrderBy(policy => policy.Id).ToArray(),
            Bindings = historical.Routes.SelectMany(route => (route.PolicyBindings ?? [])
                .Select(binding => new FrozenBinding(route.Id, policyIds[binding.PolicyId]!.Value, binding.Priority)))
                .OrderBy(binding => binding.RouteId).ThenBy(binding => binding.Priority).ThenBy(binding => binding.PolicyId).ToArray(),
            GovernedRollback = new(target, source.Hash, environment.DeploymentSequence, changedRoutes)
        };
        var compiled = compiler.Compile(rebuilt, baseline, checked(baselineVersion + 1), baseline.GeneratedAt);
        var runtime = SnapshotValidator.ParsePayload(compiled.Payload.Span, environment.Id);
        if (!Same(runtime.Routes, historical.Routes) || !Same(runtime.Clusters, historical.Clusters) ||
            !Same(runtime.Policies, historical.Policies) || !Same(runtime.Applications, currentRuntime.Applications))
            throw HistoryUnavailable();
        return rebuilt;
    }

    internal async Task RequireCurrentAsync(ReleaseRecord release, EnvironmentRecord environment, FrozenReleaseCandidate frozen,
        CancellationToken ct)
    {
        if (release.ReleaseType != "rollback" || release.RollbackOf is null || frozen.Governance is null ||
            frozen.GovernedRollback is not { } source || source.SourceConfigVersion != release.ToConfigVersion ||
            source.BaselineDeploymentSequence != environment.DeploymentSequence ||
            release.BaselineConfigVersion != environment.DesiredConfigVersion)
            throw Changed();
        var current = await BuildAsync(environment, source.SourceConfigVersion, ct);
        if (!CanonicalJson.Serialize(current).AsSpan().SequenceEqual(release.CandidateBytes)) throw Changed();
    }

    private static ClusterDto RebuildCluster(RuntimeCluster historical, ClusterDto current)
    {
        var destinations = current.Destinations.Where(destination => destination.Enabled).ToDictionary(destination => destination.Id);
        if (current.Status != "Active" || historical.Destinations.Any(destination => !destinations.ContainsKey(destination.Id)))
            throw HistoryUnavailable();
        return current with
        {
            LoadBalancingPolicy = historical.LoadBalancingPolicy,
            HealthCheckEnabled = historical.HealthCheckEnabled,
            HealthCheckPath = historical.HealthCheckPath,
            HealthCheckIntervalSec = historical.HealthCheckIntervalSec,
            Destinations = historical.Destinations.Select(destination => destinations[destination.Id] with
            {
                Address = destination.Address,
                Weight = destination.Weight
            }).ToArray()
        };
    }

    private static object AuthorizationSurface(RuntimeSnapshot snapshot)
    {
        var policies = snapshot.Policies.ToDictionary(policy => policy.Id);
        return snapshot.Routes.OrderBy(route => route.Id).Select(route => new
        {
            route.Id, route.ApiId, route.ApiVersionId, route.EnvironmentId, route.AuthorizationRevision,
            route.Path, route.Methods, route.MatchOrder, route.RequireApiKey, route.AuthenticationMode,
            Policies = (route.PolicyBindings ?? []).Where(binding => policies[binding.PolicyId].Type is
                "authentication" or "ip_filter" or "cors" or "transform").Select(binding => new
            {
                binding.Priority, policies[binding.PolicyId].Type, policies[binding.PolicyId].Config
            }).OrderBy(policy => policy.Priority).ThenBy(policy => policy.Type, StringComparer.Ordinal).ToArray()
        }).ToArray();
    }

    private static bool Same<T>(T left, T right) =>
        CanonicalJson.Serialize(left).AsSpan().SequenceEqual(CanonicalJson.Serialize(right));

    internal static ApiException HistoryUnavailable() => new(409, "governance_history_not_rebuildable",
        "历史 2.4 快照、节点确认或业务资源无法证明可等价重建。");
    private static ApiException AuthorizationChanged() => new(409, "governance_rollback_authorization_changed",
        "历史路由的授权身份、匹配次序或访问策略已变化，须按当前授权重新准备业务发布。");
    private static ApiException Changed() => new(409, "governance_rollback_preconditions_changed",
        "回滚来源、基线、当前授权或网关目标已变化，请重新预览和审批。");
}

using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
using WebApi.Domain.Policies;
using WebApi.Contracts.Policies;
using WebApi.Domain.Routing;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Delivery;
namespace WebApi.Infrastructure.Releases;
public sealed class SnapshotCompiler(UpstreamAddressPolicy? addressPolicy=null,GatewayPolicyDeploymentRules? deployment=null)
{
    private readonly GatewayPolicyDeploymentRules bodyLimits=deployment??new();
    private readonly UpstreamAddressPolicy addresses=addressPolicy??new(["http://test-backend:8080"]);
    private static ApiException Invalid(string message)=>new(422,"invalid_candidate",message);
    public CompiledSnapshot Compile(FrozenReleaseCandidate candidate,RuntimeSnapshot baseline,long targetVersion,DateTimeOffset generatedAt)
    {
        if(candidate.EnvironmentId!=baseline.EnvironmentId||candidate.BaselineConfigVersion!=baseline.ConfigVersion||targetVersion<=baseline.ConfigVersion) throw Invalid("候选环境、基准版本或目标版本不一致。");
        if(baseline.ConfigVersion>0) SnapshotValidator.Validate(baseline,candidate.EnvironmentId);
        var versions=candidate.Versions.ToDictionary(v=>v.Version.Id);if(candidate.VersionIds.Count!=versions.Count||candidate.VersionIds.Any(id=>!versions.ContainsKey(id))||candidate.Versions.Select(v=>v.Api.Id).Distinct().Count()!=versions.Count) throw Invalid("候选版本选择不完整或同一API重复。");
        foreach(var version in candidate.Versions)
        {
            if(version.Api.OrganizationId!=candidate.OrganizationId||version.Api.ProjectId!=candidate.ProjectId||version.Version.ApiId!=version.Api.Id) throw Invalid("候选版本跨范围。");
            foreach(var parameter in version.Parameters) SnapshotValidator.ValidateSchema(parameter.Schema);foreach(var schema in version.Schemas) SnapshotValidator.ValidateSchema(schema.SchemaJson);
        }
        var allPolicies=candidate.Policies.ToDictionary(p=>p.Id);foreach(var policy in allPolicies.Values) PolicyConfigurationValidator.Normalize(policy.Type,policy.Config);
        var runtimePolicies=baseline.Policies.ToDictionary(p=>p.Id);
        foreach(var p in candidate.Policies.Where(p=>p.Enabled)) {
            var config=PolicyConfigurationValidator.Normalize(p.Type,p.Config);var id=RuntimePolicyIdentity.Create(p.Id,p.Revision,p.Type,config);var runtime=new RuntimePolicy(id,p.Type,config,p.Id,p.Revision);
            if(runtimePolicies.TryGetValue(id,out var existing)&&CanonicalJson.Serialize(existing).AsSpan().SequenceEqual(CanonicalJson.Serialize(runtime))==false) throw Invalid("运行策略身份冲突。");
            runtimePolicies[id]=runtime;
        }
        var nativeClusters=candidate.Clusters.ToDictionary(c=>c.Id);var compiledClusters=new Dictionary<Guid,RuntimeCluster>();var clusterMap=new Dictionary<Guid,Guid>();
        foreach(var cluster in candidate.Clusters)
        {
            if(cluster.ProjectId!=candidate.ProjectId||cluster.EnvironmentId!=candidate.EnvironmentId) throw Invalid("候选Cluster跨环境或项目。");
            var destinations=cluster.Destinations.Where(d=>d.Enabled).OrderBy(d=>d.Id).Select(d=>new RuntimeDestination(d.Id,addresses.Validate(d.Address),d.Weight)).ToArray();
            var runtime=new RuntimeCluster(cluster.Id,candidate.EnvironmentId,cluster.LoadBalancingPolicy,cluster.HealthCheckEnabled,cluster.HealthCheckPath,cluster.HealthCheckIntervalSec,destinations,cluster.Id);
            // Runtime identity includes configuration content, so a shared native Cluster can have two frozen generations.
            var identity=new Guid(SHA256.HashData(CanonicalJson.Serialize(runtime)).AsSpan(0,16));runtime=runtime with {Id=identity};clusterMap[cluster.Id]=identity;compiledClusters[identity]=runtime;
        }
        var selectedApis = candidate.Versions.Select(v => v.Api.Id).ToHashSet();
        var retained = RetainedRoutes(baseline, selectedApis, candidate.PreserveUnmappedPromotionRoutes ? candidate.Routes.Select(r => r.Id).ToHashSet() : null);
        var retainedIds = retained.Select(route => route.Id).ToHashSet();
        var routes = retained.ToList();
        var governance = candidate.Governance;
        if (governance is null && baseline.RequiredSecurityGeneration is not null)
            throw new ApiException(409, "governance_snapshot_24_required", "治理基线必须使用新的 2.4 候选。");
        if (governance is not null)
        {
            if (baseline.SchemaVersion != "2.4" && routes.Count > 0)
                throw new ApiException(409, "governance_snapshot_full_freeze_required", "首次发布 2.4 必须将所有仍在运行的 API 纳入同一冻结范围。");
            if (!SecuritySignedEncoding.HexHash(governance.HeadHash) || governance.Routes is null || governance.Permissions is null || governance.Targets is null ||
                governance.Routes.Any(route => route is null) || governance.Permissions.Any(permission => permission is null) ||
                baseline.RequiredSecurityGeneration is Guid previousGeneration &&
                (governance.SecurityGeneration != previousGeneration || governance.Epoch < baseline.RequiredSecurityEpoch ||
                 !SnapshotSchemaRules.AtLeast(governance.MinimumSnapshotSchema, baseline.MinimumSnapshotSchema!) ||
                 baseline.RequireEnvironmentCredentials == true && !governance.RequireEnvironmentCredentials))
                throw Invalid("冻结安全账本与现有基线不一致。");
        }
        foreach(var route in candidate.Routes.Where(r=>r.Enabled))
        {
            if(route.EnvironmentId!=candidate.EnvironmentId||!versions.TryGetValue(route.ApiVersionId,out var version)||!nativeClusters.TryGetValue(route.ClusterId,out var cluster)||cluster.Status!="Active") throw Invalid("候选Route引用跨环境或不完整。");
            if(version.Api.LifecycleStatus=="Retired") continue;
            var bindings=new List<PolicyBindingConfiguration>();
            foreach(var b in candidate.Bindings.Where(b=>b.RouteId==route.Id).OrderBy(b=>b.Priority).ThenBy(b=>b.PolicyId)) {
                if(!allPolicies.TryGetValue(b.PolicyId,out var policy)) throw Invalid("Policy绑定不存在。");bindings.Add(new(policy.Id,policy.Type,policy.Config,policy.Enabled,b.Priority));
            }
            var effective=PolicyBindingRules.Validate(bindings,true,route.TimeoutMs);
            var runtimeBindings=bindings.Where(b=>b.Enabled).Select(b=>new RuntimePolicyBinding(RuntimePolicyIdentity.Create(b.PolicyId,allPolicies[b.PolicyId].Revision,b.Type,b.Config),b.Priority)).ToArray();
            routes.Add(new(route.Id,version.Api.Id,route.ApiVersionId,clusterMap[route.ClusterId],route.Path,route.Methods.OrderBy(m=>m,StringComparer.Ordinal).ToArray(),RouteNormalizer.MatchOrder(route.Path,route.Priority),effective.TimeoutMs??route.TimeoutMs,effective.RequireApiKey,runtimeBindings,effective.Authentication?.Mode==AuthenticationMode.JWT?AuthenticationMode.JWT:null));
        }
        Dictionary<Guid,FrozenGovernancePermission>? frozenPermissions = null;
        if (governance is not null)
        {
            if (governance.Routes.Select(route => route.RouteId).Distinct().Count() != governance.Routes.Count ||
                governance.Permissions.Select(permission => permission.PermissionId).Distinct().Count() != governance.Permissions.Count)
                throw Invalid("冻结路由或授权身份重复。");
            var frozenRoutes = governance.Routes.ToDictionary(route => route.RouteId);
            if (frozenRoutes.Count != routes.Count || routes.Any(route => !frozenRoutes.TryGetValue(route.Id, out var identity) ||
                retainedIds.Contains(route.Id) && route.AuthorizationRevision != identity.AuthorizationRevision))
                throw Invalid("冻结路由的授权修订缺失或与保留配置不一致。");
            routes = routes.Select(route => route with { EnvironmentId = candidate.EnvironmentId,
                AuthorizationRevision = frozenRoutes[route.Id].AuthorizationRevision }).ToList();
            frozenPermissions = governance.Permissions.ToDictionary(permission => permission.PermissionId);
        }
        var usedClusters=routes.Select(r=>r.ClusterId).ToHashSet();var clusters=baseline.Clusters.Where(c=>usedClusters.Contains(c.Id)).ToDictionary(c=>c.Id);foreach(var c in compiledClusters.Values.Where(c=>usedClusters.Contains(c.Id))) clusters[c.Id]=c;
        foreach(var c in clusters.Values) foreach(var d in c.Destinations) addresses.Validate(d.Address);
        var usedPolicyIds=routes.SelectMany(r=>r.PolicyBindings??[]).Select(b=>b.PolicyId).ToHashSet();
        foreach(var policy in runtimePolicies.Values.Where(p=>usedPolicyIds.Contains(p.Id)&&p.Type=="transform")) bodyLimits.Validate(policy.Type,policy.Config);
        var mappedApps=runtimePolicies.Values.Where(p=>usedPolicyIds.Contains(p.Id)&&p.Type=="authentication").SelectMany(p=>PolicyConfigurationValidator.ParseAuthentication(p.Config).Jwt?.ApplicationMappings??[]).Select(m=>m.ApplicationId).ToHashSet();
        var apiIds=routes.Select(r=>r.ApiId).ToHashSet();var applications=new List<RuntimeApplication>();
        foreach(var app in candidate.Applications)
        {
            if(app.Application.OrganizationId!=candidate.OrganizationId||app.Application.ProjectId is Guid project&&project!=candidate.ProjectId||app.Permissions.Any(p=>p.ApplicationId!=app.Application.Id||p.EnvironmentId!=candidate.EnvironmentId)) throw Invalid("应用或授权跨范围。");
            var grants = new List<RuntimePermission>();
            foreach (var permission in app.Permissions.Where(p => apiIds.Contains(p.ApiId)).OrderBy(p => p.ApiId))
            {
                if (frozenPermissions is null)
                    grants.Add(new(permission.ApiId, permission.ValidFrom, permission.ExpiresAt));
                else if (frozenPermissions.Remove(permission.Id, out var frozenPermission))
                    grants.Add(new(permission.ApiId, permission.ValidFrom, permission.ExpiresAt,
                        frozenPermission.Mode, frozenPermission.RouteMethods, candidate.EnvironmentId));
                else throw Invalid("冻结快照缺少当前授权模式或路由方法修订。");
            }
            if (grants.Count == 0 && !mappedApps.Contains(app.Application.Id)) continue;
            if (governance is null && app.Credentials.Any(key => key.EnvironmentId is not null || key.BindingMode is not (null or "LegacyGlobal")))
                throw Invalid("旧版快照不能使用环境绑定凭证。");
            var credentials = app.Credentials.OrderBy(c => c.Id).Select(c => new RuntimeCredential(c.Id, c.AccessKey, c.SecretHash, c.Status,
                c.ValidFrom, c.ExpiresAt, governance is null ? null : c.EnvironmentId, governance is null ? null : c.BindingMode)).ToArray();
            applications.Add(new(app.Application.Id, app.Application.Status, credentials, grants, governance is null ? null : candidate.EnvironmentId));
        }
        if (frozenPermissions is { Count: > 0 }) throw Invalid("冻结快照包含未使用的授权身份。");
        var network=candidate.PreserveUnmappedPromotionRoutes&&baseline.SchemaVersion=="2.3"||usedPolicyIds.Any(id=>runtimePolicies[id].Type is "cors" or "ip_filter" or "transform");
        var newest=candidate.PreserveUnmappedPromotionRoutes&&baseline.SchemaVersion=="2.2"||usedPolicyIds.Any(id=>runtimePolicies[id].Type is "retry" or "cache" || runtimePolicies[id].Type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(runtimePolicies[id].Config).Mode==AuthenticationMode.JWT);
        var advanced=governance is not null||network||newest||candidate.PreserveUnmappedPromotionRoutes&&baseline.SchemaVersion=="2.1"||usedPolicyIds.Any(id=>runtimePolicies[id].Type is "rate_limit" or "circuit_breaker");
        RuntimePolicy[] finalPolicies;
        if(advanced) finalPolicies=runtimePolicies.Values.Where(p=>usedPolicyIds.Contains(p.Id)).OrderBy(p=>p.Id).ToArray();
        else {
            // In 2.0 authentication and timeout are executed only through folded route values.
            var legacy=new Dictionary<Guid,RuntimePolicy>();
            foreach(var p in baseline.Policies.Where(p=>baseline.SchemaVersion=="2.0"&&retained.Length>0&&p.Type is "authentication" or "timeout")) legacy.TryAdd(p.SourcePolicyId??p.Id,new(p.SourcePolicyId??p.Id,p.Type,p.Config));
            foreach(var p in runtimePolicies.Values.Where(p=>usedPolicyIds.Contains(p.Id))) legacy.TryAdd(p.SourcePolicyId??p.Id,new(p.SourcePolicyId??p.Id,p.Type,p.Config));
            finalPolicies=legacy.Values.OrderBy(p=>p.Id).ToArray();routes=routes.Select(r=>r with {PolicyBindings=null}).ToList();
        }
        var snapshot=new RuntimeSnapshot(governance is not null?"2.4":network?"2.3":newest?"2.2":advanced?"2.1":"2.0",candidate.EnvironmentId,targetVersion,generatedAt.ToUniversalTime(),routes.OrderBy(r=>r.MatchOrder).ThenBy(r=>r.Id).ToArray(),clusters.Values.OrderBy(c=>c.Id).ToArray(),finalPolicies,applications.OrderBy(a=>a.Id).ToArray(),
            governance?.SecurityGeneration,governance?.Epoch,governance?.RequireEnvironmentCredentials,governance?.MinimumSnapshotSchema);
        if (candidate.PreserveUnmappedPromotionRoutes)
        {
            var siblings = retained.Where(route => selectedApis.Contains(route.ApiId)).ToArray();
            if (siblings.Any(route => !versions.ContainsKey(route.ApiVersionId)))
                throw new ApiException(409, "promotion_retained_version_conflict", "同一 API 的未映射运行路由仍使用其他版本，请先独立评审其版本变更。");
            new RetainedRouteAccess(baseline, siblings).RequireUnchanged(baseline, snapshot);
        }
        if (governance is null) SnapshotValidator.Validate(snapshot,candidate.EnvironmentId);
        var bytes = CanonicalJson.Serialize(snapshot);
        if (bytes.Length > 16 * 1024 * 1024) throw Invalid("快照超过 16 MiB 限制。");
        if (governance is not null) SnapshotValidator.ParsePayload(bytes, candidate.EnvironmentId);
        return new(bytes,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.LongLength);
    }

    internal static RuntimeRoute[] RetainedRoutes(RuntimeSnapshot baseline, IReadOnlySet<Guid> selectedApis, IReadOnlySet<Guid>? mappedRoutes) =>
        baseline.Routes.Where(route => !selectedApis.Contains(route.ApiId) || mappedRoutes is not null && !mappedRoutes.Contains(route.Id)).ToArray();
}

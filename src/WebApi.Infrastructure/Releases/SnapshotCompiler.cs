using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
using WebApi.Domain.Policies;
using WebApi.Contracts.Policies;
using WebApi.Domain.Routing;
using WebApi.Infrastructure.Routing;
namespace WebApi.Infrastructure.Releases;
public sealed class SnapshotCompiler(UpstreamAddressPolicy? addressPolicy=null)
{
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
        var selectedApis=candidate.Versions.Select(v=>v.Api.Id).ToHashSet();var routes=baseline.Routes.Where(r=>!selectedApis.Contains(r.ApiId)).ToList();
        foreach(var route in candidate.Routes.Where(r=>r.Enabled))
        {
            if(route.EnvironmentId!=candidate.EnvironmentId||!versions.TryGetValue(route.ApiVersionId,out var version)||!nativeClusters.TryGetValue(route.ClusterId,out var cluster)||cluster.Status!="Active") throw Invalid("候选Route引用跨环境或不完整。");
            if(version.Api.LifecycleStatus=="Retired") continue;
            var bindings=new List<PolicyBindingConfiguration>();
            foreach(var b in candidate.Bindings.Where(b=>b.RouteId==route.Id).OrderBy(b=>b.Priority).ThenBy(b=>b.PolicyId)) {
                if(!allPolicies.TryGetValue(b.PolicyId,out var policy)) throw Invalid("Policy绑定不存在。");bindings.Add(new(policy.Id,policy.Type,policy.Config,policy.Enabled,b.Priority));
            }
            var effective=PolicyBindingRules.Validate(bindings,true);
            var runtimeBindings=bindings.Where(b=>b.Enabled).Select(b=>new RuntimePolicyBinding(RuntimePolicyIdentity.Create(b.PolicyId,allPolicies[b.PolicyId].Revision,b.Type,b.Config),b.Priority)).ToArray();
            routes.Add(new(route.Id,version.Api.Id,route.ApiVersionId,clusterMap[route.ClusterId],route.Path,route.Methods.OrderBy(m=>m,StringComparer.Ordinal).ToArray(),RouteNormalizer.MatchOrder(route.Path,route.Priority),effective.TimeoutMs??route.TimeoutMs,effective.RequireApiKey,runtimeBindings));
        }
        var usedClusters=routes.Select(r=>r.ClusterId).ToHashSet();var clusters=baseline.Clusters.Where(c=>usedClusters.Contains(c.Id)).ToDictionary(c=>c.Id);foreach(var c in compiledClusters.Values.Where(c=>usedClusters.Contains(c.Id))) clusters[c.Id]=c;
        foreach(var c in clusters.Values) foreach(var d in c.Destinations) addresses.Validate(d.Address);
        var apiIds=routes.Select(r=>r.ApiId).ToHashSet();var applications=new List<RuntimeApplication>();
        foreach(var app in candidate.Applications)
        {
            if(app.Application.OrganizationId!=candidate.OrganizationId||app.Application.ProjectId is Guid project&&project!=candidate.ProjectId||app.Permissions.Any(p=>p.ApplicationId!=app.Application.Id||p.EnvironmentId!=candidate.EnvironmentId)) throw Invalid("应用或授权跨范围。");
            var grants=app.Permissions.Where(p=>apiIds.Contains(p.ApiId)).OrderBy(p=>p.ApiId).Select(p=>new RuntimePermission(p.ApiId,p.ValidFrom,p.ExpiresAt)).ToArray();
            if(grants.Length==0) continue;applications.Add(new(app.Application.Id,app.Application.Status,app.Credentials.OrderBy(c=>c.Id).Select(c=>new RuntimeCredential(c.Id,c.AccessKey,c.SecretHash,c.Status,c.ValidFrom,c.ExpiresAt)).ToArray(),grants));
        }
        var usedPolicyIds=routes.SelectMany(r=>r.PolicyBindings??[]).Select(b=>b.PolicyId).ToHashSet();
        var advanced=usedPolicyIds.Any(id=>runtimePolicies[id].Type is "rate_limit" or "circuit_breaker");
        RuntimePolicy[] finalPolicies;
        if(advanced) finalPolicies=runtimePolicies.Values.Where(p=>usedPolicyIds.Contains(p.Id)).OrderBy(p=>p.Id).ToArray();
        else {
            // In 2.0 authentication and timeout are executed only through folded route values.
            var legacy=new Dictionary<Guid,RuntimePolicy>();
            foreach(var p in baseline.Policies.Where(p=>baseline.SchemaVersion=="2.0"&&routes.Any(r=>!selectedApis.Contains(r.ApiId))&&p.Type is "authentication" or "timeout")) legacy.TryAdd(p.SourcePolicyId??p.Id,new(p.SourcePolicyId??p.Id,p.Type,p.Config));
            foreach(var p in runtimePolicies.Values.Where(p=>usedPolicyIds.Contains(p.Id))) legacy.TryAdd(p.SourcePolicyId??p.Id,new(p.SourcePolicyId??p.Id,p.Type,p.Config));
            finalPolicies=legacy.Values.OrderBy(p=>p.Id).ToArray();routes=routes.Select(r=>r with {PolicyBindings=null}).ToList();
        }
        var snapshot=new RuntimeSnapshot(advanced?"2.1":"2.0",candidate.EnvironmentId,targetVersion,generatedAt.ToUniversalTime(),routes.OrderBy(r=>r.MatchOrder).ThenBy(r=>r.Id).ToArray(),clusters.Values.OrderBy(c=>c.Id).ToArray(),finalPolicies,applications.OrderBy(a=>a.Id).ToArray());
        SnapshotValidator.Validate(snapshot,candidate.EnvironmentId);var bytes=CanonicalJson.Serialize(snapshot);return new(bytes,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.LongLength);
    }
}

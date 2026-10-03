using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
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
        var policies=candidate.Policies.Where(p=>p.Enabled).ToDictionary(p=>p.Id);foreach(var policy in policies.Values) ValidatePolicy(policy.Type,policy.Config);
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
            if(version.Api.LifecycleStatus=="Retired") continue;var timeout=route.TimeoutMs;var requireKey=route.RequireApiKey;var authenticationCount=0;
            foreach(var binding in candidate.Bindings.Where(b=>b.RouteId==route.Id).OrderBy(b=>b.Priority))
            {
                if(!candidate.Policies.Any(p=>p.Id==binding.PolicyId)) throw Invalid("Policy绑定不存在。");if(!policies.TryGetValue(binding.PolicyId,out var policy)) continue;using var config=JsonDocument.Parse(policy.Config);
                if(policy.Type=="authentication") {if(++authenticationCount>1) throw Invalid("同一路由不能同时绑定多个认证策略。");requireKey=config.RootElement.GetProperty("mode").GetString()=="ApiKey";}
                if(policy.Type=="timeout") timeout=config.RootElement.GetProperty("timeoutMs").GetInt32();
            }
            if(!requireKey&&authenticationCount==0) throw Invalid("匿名路由必须有显式认证绑定。");
            routes.Add(new(route.Id,version.Api.Id,route.ApiVersionId,clusterMap[route.ClusterId],route.Path,route.Methods.OrderBy(m=>m,StringComparer.Ordinal).ToArray(),RouteNormalizer.MatchOrder(route.Path,route.Priority),timeout,requireKey));
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
        var runtimePolicies=baseline.Policies.ToDictionary(p=>p.Id);foreach(var p in policies.Values) runtimePolicies[p.Id]=new(p.Id,p.Type,p.Config);
        var snapshot=new RuntimeSnapshot("2.0",candidate.EnvironmentId,targetVersion,generatedAt.ToUniversalTime(),routes.OrderBy(r=>r.MatchOrder).ThenBy(r=>r.Id).ToArray(),clusters.Values.OrderBy(c=>c.Id).ToArray(),runtimePolicies.Values.OrderBy(p=>p.Id).ToArray(),applications.OrderBy(a=>a.Id).ToArray());
        SnapshotValidator.Validate(snapshot,candidate.EnvironmentId);var bytes=CanonicalJson.Serialize(snapshot);return new(bytes,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.LongLength);
    }
    private static void ValidatePolicy(string type,string source)
    {
        if(type is not ("authentication" or "timeout")) throw Invalid("首期不支持该策略类型。");
        try
        {
            using var document=JsonDocument.Parse(source);var config=document.RootElement;if(config.ValueKind!=JsonValueKind.Object) throw Invalid("策略配置必须为对象。");
            if(type=="authentication") {if(config.EnumerateObject().Any(p=>p.Name!="mode")||!config.TryGetProperty("mode",out var mode)||mode.ValueKind!=JsonValueKind.String||mode.GetString() is not ("ApiKey" or "Anonymous")) throw Invalid("首期仅支持显式API Key或匿名认证。");}
            else if(config.EnumerateObject().Any(p=>p.Name!="timeoutMs")||!config.TryGetProperty("timeoutMs",out var timeout)||!timeout.TryGetInt32(out var value)||value is <1 or >300000) throw Invalid("超时策略字段不合法。");
        }
        catch(JsonException) {throw Invalid("策略JSON无法解析。");}
    }
}

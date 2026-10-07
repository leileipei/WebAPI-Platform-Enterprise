using System.Text.Json;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Routing;
using WebApi.Domain.Policies;
using WebApi.Contracts.Policies;
namespace WebApi.Domain.Runtime;
public static class SnapshotValidator
{
    private static readonly string[] methods=["GET","POST","PUT","PATCH","DELETE","HEAD","OPTIONS"];
    private static ApiException Invalid(string message)=>new(422,"invalid_snapshot",message);
    public static void Validate(RuntimeSnapshot snapshot,Guid expectedEnvironment)
    {
        if(snapshot.SchemaVersion is not ("2.0" or "2.1" or "2.2")||snapshot.EnvironmentId!=expectedEnvironment||expectedEnvironment==Guid.Empty||snapshot.ConfigVersion<=0||snapshot.GeneratedAt.Offset!=TimeSpan.Zero) throw Invalid("Snapshot协议、环境、版本或UTC时间不合法。");
        if(snapshot.Routes is null||snapshot.Clusters is null||snapshot.Policies is null||snapshot.Applications is null) throw Invalid("Snapshot集合缺失。");
        if(snapshot.Routes.Count>10000||snapshot.Routes.Select(r=>r.Id).Distinct().Count()!=snapshot.Routes.Count||snapshot.Clusters.Select(c=>c.Id).Distinct().Count()!=snapshot.Clusters.Count) throw Invalid("Snapshot路由为空、重复或超过限制。");
        var clusters=snapshot.Clusters.ToDictionary(c=>c.Id);var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var route in snapshot.Routes)
        {
            if(snapshot.SchemaVersion!="2.2"&&route.AuthenticationMode is not null) throw Invalid("旧协议不支持认证模式字段。");
            if(route.AuthenticationMode is {} mode && (!Enum.IsDefined(mode)||(mode==AuthenticationMode.ApiKey)!=route.RequireApiKey)) throw Invalid("认证模式与API Key折叠字段不一致。");
            if(route.Id==Guid.Empty||route.ApiId==Guid.Empty||route.ApiVersionId==Guid.Empty||!clusters.ContainsKey(route.ClusterId)||route.TimeoutMs is <1 or >300000||route.Methods.Count==0||route.Methods.Any(m=>!methods.Contains(m))) throw Invalid("路由身份、方法、超时或Cluster引用不合法。");var shape=RouteNormalizer.Normalize(route.Path);
            foreach(var method in route.Methods) if(!keys.Add(method+":"+shape)) throw Invalid("Runtime存在同形方法路径冲突。");
        }
        foreach(var c in snapshot.Clusters)
        {
            if(c.EnvironmentId!=expectedEnvironment||c.Destinations.Count==0||c.LoadBalancingPolicy is not ("RoundRobin" or "LeastRequests" or "PowerOfTwoChoices" or "Random" or "FirstAlphabetical")||c.HealthCheckIntervalSec is <1 or >3600||!c.HealthCheckPath.StartsWith('/')||c.HealthCheckPath.Length>256) throw Invalid("Cluster范围、后端或健康配置不合法。");
            if(c.Destinations.Select(d=>d.Id).Distinct().Count()!=c.Destinations.Count) throw Invalid("Destination身份重复。");
            foreach(var d in c.Destinations) if(d.Id==Guid.Empty||d.Weight is <1 or >1000||!Uri.TryCreate(d.Address,UriKind.Absolute,out var address)||address.Scheme is not ("http" or "https")||address.UserInfo.Length>0||address.Query.Length>0||address.Fragment.Length>0) throw Invalid("Destination地址或权重不合法。");
        }
        if(snapshot.Policies.Count>40000||snapshot.Policies.Select(p=>p.Id).Distinct().Count()!=snapshot.Policies.Count) throw Invalid("运行策略数量或身份不合法。");
        var policies=snapshot.Policies.ToDictionary(p=>p.Id);
        foreach(var p in snapshot.Policies) {
            if(p.Id==Guid.Empty) throw Invalid("运行策略身份为空。");
            if(snapshot.SchemaVersion=="2.0"&&(p.Type is not ("authentication" or "timeout")||p.SourcePolicyId is not null||p.SourceRevision is not null)) throw Invalid("2.0不支持高级策略或来源字段。");
            var normalized=PolicyConfigurationValidator.Normalize(p.Type,p.Config);
            var jwt=p.Type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(normalized).Mode==AuthenticationMode.JWT;
            if(snapshot.SchemaVersion!="2.2"&&(p.Type is "retry" or "cache"||jwt)) throw Invalid("旧协议不支持JWT、重试或缓存。");
            if(p.SourcePolicyId is not null||p.SourceRevision is not null) {
                if(p.SourcePolicyId is not Guid source||p.SourceRevision is not long revision||p.Id!=RuntimePolicyIdentity.Create(source,revision,p.Type,normalized)) throw Invalid("运行策略身份与内容不一致。");
            }else if(snapshot.SchemaVersion is "2.1" or "2.2"&&(p.Type is not ("authentication" or "timeout")||jwt)) throw Invalid("高级策略缺少来源。");
        }
        foreach(var r in snapshot.Routes) {
            if(snapshot.SchemaVersion=="2.0"&&r.PolicyBindings is not null) throw Invalid("2.0不支持运行绑定。");
            if(r.PolicyBindings is null) {if(r.AuthenticationMode==AuthenticationMode.JWT) throw Invalid("JWT路由缺少认证绑定。");continue;}
            var bindings=new List<PolicyBindingConfiguration>();foreach(var b in r.PolicyBindings) {
                if(!policies.TryGetValue(b.PolicyId,out var p)||p.SourcePolicyId is null||p.SourceRevision is null) throw Invalid("运行绑定缺少有效策略来源。");bindings.Add(new(p.Id,p.Type,p.Config,true,b.Priority));
            }
            var effective=PolicyBindingRules.Validate(bindings,true,r.TimeoutMs);
            var mode=r.AuthenticationMode??(r.RequireApiKey?AuthenticationMode.ApiKey:AuthenticationMode.Anonymous);
            if(effective.Authentication!.Mode!=mode) throw Invalid("认证绑定与有效模式不一致。");
            if(effective.RequireApiKey!=r.RequireApiKey||(effective.TimeoutMs is int timeout&&timeout!=r.TimeoutMs)) throw Invalid("策略绑定与路由折叠配置不一致。");
        }
        var apiIds=snapshot.Routes.Select(r=>r.ApiId).ToHashSet();var accessKeys=new HashSet<string>(StringComparer.Ordinal);
        if(snapshot.Applications.Select(a=>a.Id).Distinct().Count()!=snapshot.Applications.Count) throw Invalid("Application身份重复。");
        var appIds=snapshot.Applications.Select(a=>a.Id).ToHashSet();
        foreach(var policy in snapshot.Policies.Where(p=>p.Type=="authentication"))
            if(PolicyConfigurationValidator.ParseAuthentication(policy.Config).Jwt is {} jwt&&jwt.ApplicationMappings.Any(m=>!appIds.Contains(m.ApplicationId))) throw Invalid("JWT映射应用缺失。");
        foreach(var a in snapshot.Applications)
        {
            if(a.Status is not ("Active" or "Disabled")) throw Invalid("Application状态不合法。");
            foreach(var key in a.Credentials) if(string.IsNullOrWhiteSpace(key.AccessKey)||!accessKeys.Add(key.AccessKey)||!Regex.IsMatch(key.Hash,"^[0-9a-fA-F]{64}$")||key.ExpiresAt<=key.ValidFrom||key.Status is not ("Active" or "Revoked")) throw Invalid("凭证身份、摘要或窗口不合法。");
            foreach(var grant in a.Permissions) if(!apiIds.Contains(grant.ApiId)||grant.ExpiresAt<=grant.ValidFrom) throw Invalid("授权引用或窗口不合法。");
        }
    }
    public static RuntimeSnapshot ParsePayload(ReadOnlySpan<byte> payload,Guid expectedEnvironment)
    {
        if(payload.Length>16*1024*1024) throw Invalid("Snapshot超过大小限制。");
        try
        {
            using var json=JsonDocument.Parse(payload.ToArray()); var root=json.RootElement;
            var schema=root.GetProperty("schemaVersion").GetString();
            if(schema is "2.0" or "2.1") foreach(var route in root.GetProperty("routes").EnumerateArray())
                if(route.TryGetProperty("authenticationMode",out _)) throw Invalid("旧协议不支持认证模式字段。");
            foreach(var route in root.GetProperty("routes").EnumerateArray())
                if(route.TryGetProperty("authenticationMode",out var mode) && (mode.ValueKind!=JsonValueKind.String||mode.GetString() is not ("ApiKey" or "Anonymous" or "JWT"))) throw Invalid("认证模式字段不合法。");
            var snapshot=JsonSerializer.Deserialize<RuntimeSnapshot>(payload,CanonicalJson.Options)??throw Invalid("Snapshot不可读。");
            Validate(snapshot,expectedEnvironment);return snapshot;
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {throw Invalid("Snapshot JSON结构不合法。");}
    }
    public static void ValidateSchema(string? source)
    {
        if(source is null) return;
        try {using var json=JsonDocument.Parse(source);Check(json.RootElement);}
        catch(JsonException) {throw Invalid("Schema JSON不合法。");}
    }
    private static void Check(JsonElement schema)
    {
        if(schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;if(schema.ValueKind!=JsonValueKind.Object) throw Invalid("Schema必须是对象或布尔Schema。");
        if(schema.TryGetProperty("type",out var type)) {string[] allowed=["null","string","number","integer","boolean","object","array"];if(type.ValueKind==JsonValueKind.String) {if(!allowed.Contains(type.GetString())) throw Invalid("未知Schema type。");}else if(type.ValueKind==JsonValueKind.Array) {foreach(var t in type.EnumerateArray()) if(t.ValueKind!=JsonValueKind.String||!allowed.Contains(t.GetString())) throw Invalid("Schema type数组不合法。");}else throw Invalid("Schema type不合法。");}
        if(schema.TryGetProperty("properties",out var properties)) {if(properties.ValueKind!=JsonValueKind.Object) throw Invalid("Schema properties必须是对象。");foreach(var p in properties.EnumerateObject()) Check(p.Value);}
        if(schema.TryGetProperty("items",out var items)) Check(items);
        foreach(var keyword in new[]{"allOf","oneOf","anyOf","prefixItems"}) if(schema.TryGetProperty(keyword,out var union)) {if(union.ValueKind!=JsonValueKind.Array) throw Invalid("Schema组合须是数组。");foreach(var member in union.EnumerateArray()) Check(member);}
    }
}

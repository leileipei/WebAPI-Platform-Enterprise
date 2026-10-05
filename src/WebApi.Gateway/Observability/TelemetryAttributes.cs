using System.Diagnostics;
using System.Text.Json;
using WebApi.Contracts.Policies;
namespace WebApi.Gateway.Observability;
public static class TelemetryAttributes
{
    public static object[] From(RequestTelemetryContext context)=>Values(context).Select(x=>Attribute(x.Key,x.Value)).ToArray();
    public static object Attribute(string key,object? value)=>new{key,value=value switch{
        long n=>(object)new{intValue=n.ToString(System.Globalization.CultureInfo.InvariantCulture)},
        int n=>new{intValue=n.ToString(System.Globalization.CultureInfo.InvariantCulture)},
        double n=>new{doubleValue=n},
        _=>new{stringValue=value?.ToString()??""}}};
    public static Dictionary<string,object?> Values(RequestTelemetryContext c)
    {var values=new Dictionary<string,object?>{
        ["webapi.environment.id"]=c.EnvironmentId.ToString(),["webapi.node.name"]=c.NodeName,
        ["webapi.api.id"]=c.ApiId?.ToString()??"Unmatched",["webapi.application.id"]=c.ApplicationKey,
        ["webapi.destination.id"]=c.DestinationId?.ToString()??"None",["http.request.method"]=c.Method,
        ["url.template"]=c.PathTemplate,["webapi.outcome"]=c.Outcome,["webapi.success"]=c.Success?"true":"false",
        ["webapi.request.id"]=c.RequestId,["webapi.log.id"]=c.LogId.ToString(),["webapi.duration.ms"]=c.DurationSeconds*1000,
        ["webapi.client.ip_masked"]=c.MaskedIp,["webapi.client.ip_hmac"]=c.IpHmac,
        ["http.response.status_code"]=c.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"None",
        ["webapi.config.version"]=c.ConfigVersion?.ToString()??"None",["webapi.deployment.sequence"]=c.DeploymentSequence?.ToString()??"None",
        ["webapi.api.version.id"]=c.ApiVersionId?.ToString()??"None",["webapi.route.id"]=c.RouteId?.ToString()??"None",
        ["webapi.runtime.cluster.id"]=c.RuntimeClusterId?.ToString()??"None",["webapi.cluster.id"]=c.ClusterId?.ToString()??"None"};
        foreach(var p in c.PolicyDecisions.Take(2).Where(PolicyDecisionValues.Valid)) {var prefix="webapi.policy."+p.PolicyType+".";values[prefix+"id"]=p.PolicyId.ToString();values[prefix+"type"]=p.PolicyType;values[prefix+"revision"]=p.PolicyRevision;values[prefix+"decision"]=p.Decision;if(p.RejectionReason is not null) values[prefix+"rejection_reason"]=p.RejectionReason;}
        return values;
    }
    public static IReadOnlySet<string> PermittedKeys {get;}=Values(new RequestTelemetryContext()).Keys.Concat(PolicyDecisionValues.Types.SelectMany(type=>new[]{"id","type","revision","decision","rejection_reason"}.Select(field=>"webapi.policy."+type+"."+field))).ToHashSet(StringComparer.Ordinal);
    public static void Apply(Activity? activity,RequestTelemetryContext context)
    {if(activity is null)return;foreach(var pair in Values(context))activity.SetTag(pair.Key,pair.Value);}
    public static string Nano(DateTimeOffset time)=>((time.UtcTicks-DateTimeOffset.UnixEpoch.UtcTicks)*100).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static JsonElement Copy(object value)=>JsonSerializer.SerializeToElement(value);
}

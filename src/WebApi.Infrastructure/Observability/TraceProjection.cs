using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed record SourceSpan(Guid? EnvironmentId,string SpanId,string? ParentSpanId,string Name,DateTimeOffset Start,double DurationMs,string Kind,string Status,IReadOnlyDictionary<string,string> Attributes);
public static class TraceProjection
{
    private static readonly HashSet<string> Outcomes=["Completed","ClientAborted","Timeout","ProxyError"];
    public static TraceDetailDto Project(string traceId,IReadOnlyList<SourceSpan> spans,TrustedObservationScope scope)
    {
        var visible=new List<TraceSpanDto>();var partial=false;var seen=new HashSet<string>();
        foreach(var span in spans)
        {
            if(span.EnvironmentId is not Guid env||!scope.EnvironmentIds.Contains(env)){partial=true;continue;}
            var attrs=span.Attributes;var tags=new Dictionary<string,string>{{"webapi.environment.id",env.ToString()}};
            if(!Resource("webapi.api.id",scope.ApiNames.ContainsKey,["Unmatched"])||!Resource("webapi.application.id",scope.ApplicationNames.ContainsKey,["Anonymous","Unknown"])||!Resource("webapi.destination.id",id=>scope.Destinations.TryGetValue(id,out var dest)&&dest.EnvironmentId==env,["None"])) {partial=true;continue;}
            bool Resource(string key,Func<Guid,bool> exists,string[] placeholders){if(!attrs.TryGetValue(key,out var value))return true;if(Guid.TryParse(value,out var id)&&exists(id)){tags[key]=id.ToString();return true;}if(placeholders.Contains(value)){tags[key]=value;return true;}return false;}
            if(!seen.Add(span.SpanId)){partial=true;continue;}
            if(attrs.TryGetValue("http.request.method",out var method)&&method.Length<=16&&method.All(c=>c is >= 'A' and <= 'Z'))tags["http.request.method"]=method;
            if(attrs.TryGetValue("url.template",out var path)&&path.Length<=512&&path.StartsWith('/')&&!path.Any(c=>char.IsControl(c)||c is '?' or '#'))tags["url.template"]=path;
            if(attrs.TryGetValue("webapi.outcome",out var outcome)&&Outcomes.Contains(outcome))tags["webapi.outcome"]=outcome;
            foreach(var key in new[]{"webapi.config.version","webapi.api.version.id","webapi.route.id","webapi.cluster.id"})if(attrs.TryGetValue(key,out var value)&&Guid.TryParse(value,out var id))tags[key]=id.ToString();
            if(attrs.TryGetValue("webapi.deployment.sequence",out var seq)&&long.TryParse(seq,out var number)&&number>=0)tags["webapi.deployment.sequence"]=number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Names are controlled by the gateway. Never copy arbitrary backend names or raw attributes.
            var name=span.Name is "gateway.request" or "gateway.proxy"?span.Name:span.Kind=="Server"?"gateway.request":span.Kind=="Client"?"gateway.proxy":"span";
            visible.Add(new(span.SpanId,span.ParentSpanId,name,span.Start,span.DurationMs,span.Kind,span.Status,tags));
        }
        var ids=visible.Select(x=>x.SpanId).ToHashSet();
        var output=visible.Select(x=>x.ParentSpanId is string parent&&!ids.Contains(parent)?MissingParent(x):x).OrderBy(x=>x.Start).ThenBy(x=>x.SpanId,StringComparer.Ordinal).ToArray();
        TraceSpanDto MissingParent(TraceSpanDto span){partial=true;return span with{ParentSpanId=null};}
        return new(traceId,partial,output);
    }
}

using System.Diagnostics;
using OpenTelemetry;
namespace WebApi.Gateway.Observability;
public sealed class TelemetryActivityProcessor(BoundedTelemetryBuffer buffer,TelemetryDropTracker tracker,string source) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if(activity.Source.Name!=source||!activity.Recorded)return;
        if(activity.Kind==ActivityKind.Client&&!Guid.TryParse(activity.GetTagItem("webapi.destination.id")?.ToString(),out _))return;
        try
        {
            // Only our controlled source is registered. Events, links, baggage and exception descriptions are intentionally omitted.
            var permitted=TelemetryAttributes.PermittedKeys;
            var attributes=activity.TagObjects.Where(x=>permitted.Contains(x.Key)).Select(x=>TelemetryAttributes.Attribute(x.Key,x.Value)).ToArray();
            buffer.TryWrite("traces",TelemetryAttributes.Copy(new{traceId=activity.TraceId.ToHexString(),spanId=activity.SpanId.ToHexString(),parentSpanId=activity.ParentSpanId==default?"":activity.ParentSpanId.ToHexString(),name=activity.Kind==ActivityKind.Client?"gateway.proxy":"gateway.request",kind=activity.Kind==ActivityKind.Client?3:2,startTimeUnixNano=TelemetryAttributes.Nano(new DateTimeOffset(activity.StartTimeUtc)),endTimeUnixNano=TelemetryAttributes.Nano(new DateTimeOffset(activity.StartTimeUtc+activity.Duration)),attributes,status=new{code=activity.Status==ActivityStatusCode.Error?2:1}}));
        }
        catch(Exception){tracker.Record("traces",1);}
    }
}

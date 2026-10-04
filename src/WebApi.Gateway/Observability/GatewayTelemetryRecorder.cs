using System.Diagnostics;
using System.Diagnostics.Metrics;
namespace WebApi.Gateway.Observability;
public sealed class GatewayTelemetryRecorder : IDisposable
{
    private readonly BoundedTelemetryBuffer buffer;
    private readonly TelemetryDropTracker tracker;
    private readonly Meter meter;
    private readonly Counter<long> requests;
    private readonly Histogram<double> durations;
    public ActivitySource Activities {get;}
    public string MeterName {get;}
    public GatewayTelemetryRecorder(BoundedTelemetryBuffer buffer,TelemetryDropTracker tracker,GatewaySettings settings,GatewayHealthObserver health)
    {
        this.buffer=buffer;this.tracker=tracker;
        MeterName=$"WebApi.Gateway.{settings.EnvironmentId:N}.{settings.NodeName}";
        meter=new(MeterName);Activities=new(MeterName);
        requests=meter.CreateCounter<long>("webapi_gateway_requests_total");
        durations=meter.CreateHistogram<double>("webapi_gateway_request_duration_seconds","s");
        meter.CreateObservableGauge("webapi_telemetry_last_observed_timestamp_seconds",()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d);
        meter.CreateObservableCounter("webapi_telemetry_dropped_total",()=>tracker.Drops.Select(x=>new Measurement<long>(x.Value,new KeyValuePair<string,object?>("signal",x.Key))));
        meter.CreateObservableCounter("webapi_telemetry_export_failures_total",()=>tracker.Failures.Select(x=>new Measurement<long>(x.Value,new KeyValuePair<string,object?>("signal",x.Key))));
        meter.CreateObservableGauge("webapi_telemetry_last_export_success_timestamp_seconds",()=>tracker.LastSuccess.Select(x=>new Measurement<double>(x.Value,new KeyValuePair<string,object?>("signal",x.Key))));
        meter.CreateObservableGauge("webapi_destination_health",()=>health.Read().Select(x=>new Measurement<long>(x.Health switch{"Healthy"=>1,"Unhealthy"=>0,"Disabled"=>-2,_=>-1},new[]{new KeyValuePair<string,object?>("webapi.cluster.id",x.ClusterId.ToString()),new("webapi.destination.id",x.DestinationId.ToString())})));
    }
    public void Record(RequestTelemetryContext context)
    {
        try
        {
            var dimensions=new TagList{{"webapi.api.id",context.ApiId?.ToString()??"Unmatched"},{"webapi.application.id",context.ApplicationKey},{"webapi.destination.id",context.DestinationId?.ToString()??"None"}};
            durations.Record(context.DurationSeconds,dimensions);
            dimensions.Add("http.response.status_code",context.Status?.ToString()??"None");dimensions.Add("webapi.outcome",context.Outcome);dimensions.Add("webapi.success",context.Success?"true":"false");requests.Add(1,dimensions);
            buffer.TryWrite("logs",TelemetryAttributes.Copy(new{timeUnixNano=TelemetryAttributes.Nano(context.Time),observedTimeUnixNano=TelemetryAttributes.Nano(DateTimeOffset.UtcNow),severityNumber=9,severityText="INFO",body=new{stringValue="gateway.request"},traceId=context.TraceId,spanId=context.SpanId,attributes=TelemetryAttributes.From(context)}));
        }
        catch(Exception){tracker.Record("logs",1);}
    }
    public void Dispose(){Activities.Dispose();meter.Dispose();}
}

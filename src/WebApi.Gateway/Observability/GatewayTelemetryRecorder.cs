using System.Diagnostics;
using System.Diagnostics.Metrics;
using WebApi.Contracts.Policies;
namespace WebApi.Gateway.Observability;
public sealed class GatewayTelemetryRecorder : IDisposable
{
    private readonly BoundedTelemetryBuffer buffer;
    private readonly TelemetryDropTracker tracker;
    private readonly Meter meter;
    private readonly Counter<long> requests;
    private readonly Histogram<double> durations;
    private readonly Counter<long> policyDecisions;private readonly Counter<long> attempts;private readonly GatewaySettings gateway;
    public ActivitySource Activities {get;}
    public string MeterName {get;}
    public GatewayTelemetryRecorder(BoundedTelemetryBuffer buffer,TelemetryDropTracker tracker,GatewaySettings settings,GatewayHealthObserver health)
    {
        this.buffer=buffer;this.tracker=tracker;gateway=settings;
        MeterName=$"WebApi.Gateway.{settings.EnvironmentId:N}.{settings.NodeName}";
        meter=new(MeterName);Activities=new(MeterName);
        requests=meter.CreateCounter<long>("webapi_gateway_requests_total");
        policyDecisions=meter.CreateCounter<long>("webapi_gateway_policy_decisions_total");
        attempts=meter.CreateCounter<long>("webapi_gateway_forward_attempts_total");
        durations=meter.CreateHistogram<double>("webapi_gateway_request_duration_seconds","s");
        meter.CreateObservableGauge("webapi_telemetry_last_observed_timestamp_seconds",()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d);
        meter.CreateObservableCounter("webapi_telemetry_dropped_total",()=>tracker.Drops.Select(x=>new Measurement<long>(x.Value,new KeyValuePair<string,object?>("signal",x.Key))));
        meter.CreateObservableCounter("webapi_telemetry_export_failures_total",()=>tracker.Failures.Select(x=>new Measurement<long>(x.Value,new KeyValuePair<string,object?>("signal",x.Key))));
        meter.CreateObservableGauge("webapi_telemetry_last_loss_timestamp_seconds",()=>tracker.LastLoss.Select(x=>new Measurement<double>(x.Value,new KeyValuePair<string,object?>("signal",x.Key))));
        meter.CreateObservableGauge("webapi_telemetry_last_export_success_timestamp_seconds",()=>tracker.LastSuccess.Select(x=>new Measurement<double>(x.Value,new KeyValuePair<string,object?>("signal",x.Key))));
        meter.CreateObservableGauge("webapi_destination_health",()=>health.Read().Select(x=>new Measurement<long>(x.Health switch{"Healthy"=>1,"Unhealthy"=>0,"Disabled"=>-2,_=>-1},new[]{new KeyValuePair<string,object?>("webapi.cluster.id",x.ClusterId.ToString()),new("webapi.destination.id",x.DestinationId.ToString())})));
    }
    public void Record(RequestTelemetryContext context)
    {
        try
        {
            foreach(var p in context.PolicyDecisions.Take(5).Where(PolicyDecisionValues.Valid)) RecordPolicy(p.PolicyId,p.PolicyType,p.Decision);
            var dimensions=new TagList{{"webapi.api.id",context.ApiId?.ToString()??"Unmatched"},{"webapi.application.id",context.ApplicationKey},{"webapi.destination.id",context.DestinationId?.ToString()??"None"}};
            durations.Record(context.DurationSeconds,dimensions);
            dimensions.Add("http.response.status_code",context.Status?.ToString()??"None");dimensions.Add("webapi.outcome",context.Outcome);dimensions.Add("webapi.success",context.Success?"true":"false");requests.Add(1,dimensions);
            buffer.TryWrite("logs",TelemetryAttributes.Copy(new{timeUnixNano=TelemetryAttributes.Nano(context.Time),observedTimeUnixNano=TelemetryAttributes.Nano(DateTimeOffset.UtcNow),severityNumber=9,severityText="INFO",body=new{stringValue="gateway.request"},traceId=context.TraceId,spanId=context.SpanId,attributes=TelemetryAttributes.From(context)}));
        }
        catch(Exception){tracker.Record("logs",1);}
    }
    public void RecordPolicy(Guid sourcePolicyId,string type,string decision)
    {policyDecisions.Add(1,new TagList{{"webapi.environment.id",gateway.EnvironmentId.ToString()},{"webapi.node.name",gateway.NodeName},{"webapi.policy.id",sourcePolicyId.ToString()},{"webapi.policy.type",type},{"webapi.policy.decision",decision}});}
    public void RecordAttempt(Guid? retryPolicyId,string outcome)
    {try{attempts.Add(1,new TagList{{"webapi.environment.id",gateway.EnvironmentId.ToString()},{"webapi.node.name",gateway.NodeName},{"webapi.policy.id",retryPolicyId?.ToString()??"None"},{"webapi.policy.type",retryPolicyId is null?"None":"retry"},{"webapi.policy.decision",outcome}});}catch(Exception){tracker.Record("metrics",1);}}
    public void Dispose(){Activities.Dispose();meter.Dispose();}
}

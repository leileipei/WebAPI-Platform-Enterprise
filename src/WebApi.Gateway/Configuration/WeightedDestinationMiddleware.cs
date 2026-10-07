using System.Diagnostics;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Security;
using WebApi.Gateway.Policies;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Configuration;
public sealed class WeightedDestinationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var feature=context.GetReverseProxyFeature();var options=feature.AvailableDestinations;
        var execution=TrafficExecutionContext.From(context)!;
        if(options.Count>1)
        {
            var generation=execution.Generation;var cluster=generation.Snapshot.Clusters.Single(c=>c.Id==execution.Route.ClusterId);
            var selected=context.RequestServices.GetRequiredService<WebApi.Gateway.Forwarding.WeightedDestinationSelector>().Select(generation,cluster,options,new HashSet<string>(StringComparer.Ordinal));if(selected is not null)feature.AvailableDestinations=selected;
        }
        var telemetry=RequestTelemetryState.From(context);var recorder=telemetry is null?null:context.RequestServices.GetRequiredService<GatewayTelemetryRecorder>();
        var started=Stopwatch.GetTimestamp();using var activity=feature.AvailableDestinations.Count>0?recorder?.Activities.StartActivity("gateway.proxy",ActivityKind.Client):null;
        if(activity?.Id is { } id)context.Request.Headers["traceparent"]=id;
        try{await next(context);}
        finally
        {
            if(feature.ProxiedDestination is { } actual)
            {
                var destination=Guid.TryParse(actual.DestinationId,out var actualId)?actualId:(Guid?)null;var failed=context.Features.Get<Yarp.ReverseProxy.Forwarder.IForwarderErrorFeature>() is not null;
                var outcome=context.RequestAborted.IsCancellationRequested?"ClientAborted":context.Features.Get<Microsoft.AspNetCore.Http.Timeouts.IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested==true?"Timeout":failed?"ProxyError":"Completed";
                var attempt=new ForwardAttemptObservation(1,destination,context.Response.StatusCode,outcome,Stopwatch.GetElapsedTime(started).TotalSeconds);execution.AddAttempt(attempt);
                if(telemetry is not null)
                {
                    telemetry.Context=telemetry.Context with{DestinationId=destination};var client=telemetry.Context with{Status=attempt.Status,DurationSeconds=attempt.DurationSeconds,Outcome=outcome,PolicyDecisions=execution.Decisions.Select(p=>p.ToDto()).ToArray(),AttemptNumber=1,AttemptCount=1,ForwardAttempts=execution.Attempts};
                    TelemetryAttributes.Apply(activity,client);activity?.SetStatus(client.Success?ActivityStatusCode.Ok:ActivityStatusCode.Error);
                    var retry=(execution.Route.PolicyBindings??[]).Select(b=>execution.Generation.PoliciesById[b.PolicyId]).SingleOrDefault(p=>p.Type=="retry");recorder!.RecordAttempt(retry?.SourcePolicyId,outcome);
                }
            }
            else if(telemetry is not null)telemetry.Context=telemetry.Context with{DestinationId=null};
        }
    }
}

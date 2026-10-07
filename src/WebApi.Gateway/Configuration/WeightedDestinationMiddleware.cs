using System.Diagnostics;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Security;
using WebApi.Gateway.Policies;
using WebApi.Contracts.Policies;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Configuration;
public sealed class WeightedDestinationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var feature=ctx.GetReverseProxyFeature();var options=feature.AvailableDestinations;if(options.Count>1)
        {
            var generation=(RuntimeGeneration)ctx.Items[ApiKeyMiddleware.GenerationItem]!;var cluster=generation.Snapshot.Clusters.Single(c=>c.Id==Guid.Parse(feature.Route.Config.Metadata!["runtimeClusterId"]));
            var selected=ctx.RequestServices.GetRequiredService<WebApi.Gateway.Forwarding.WeightedDestinationSelector>().Select(generation,cluster,options,new HashSet<string>(StringComparer.Ordinal));
            if(selected is not null)feature.AvailableDestinations=selected;
        }
        var telemetry=RequestTelemetryState.From(ctx);
        if(telemetry is null){await next(ctx);return;}
        if(feature.AvailableDestinations.Count==1&&Guid.TryParse(feature.AvailableDestinations[0].DestinationId,out var selectedId))
            telemetry.Context=telemetry.Context with{DestinationId=selectedId};
        telemetry.Context=telemetry.Context with {PolicyDecisions=TrafficExecutionContext.From(ctx)?.Decisions.Select(p=>new PolicyDecisionDto(p.PolicyId,p.PolicyType,p.PolicyRevision,p.Decision,p.RejectionReason)).ToArray()??[]};
        var recorder=ctx.RequestServices.GetRequiredService<GatewayTelemetryRecorder>();
        var started=Stopwatch.GetTimestamp();using var activity=recorder.Activities.StartActivity("gateway.proxy",ActivityKind.Client);
        TelemetryAttributes.Apply(activity,telemetry.Context);
        if(activity?.Id is { } id)ctx.Request.Headers["traceparent"]=id;
        try{await next(ctx);}
        finally
        {
            if(feature.ProxiedDestination is { } actual&&Guid.TryParse(actual.DestinationId,out var actualId))telemetry.Context=telemetry.Context with{DestinationId=actualId};
            else telemetry.Context=telemetry.Context with{DestinationId=null};
            var failed=ctx.Features.Get<Yarp.ReverseProxy.Forwarder.IForwarderErrorFeature>() is not null;
            var client=telemetry.Context with{Status=ctx.Response.StatusCode,DurationSeconds=Stopwatch.GetElapsedTime(started).TotalSeconds,Outcome=ctx.RequestAborted.IsCancellationRequested?"ClientAborted":failed?"ProxyError":"Completed"};
            TelemetryAttributes.Apply(activity,client);activity?.SetStatus(client.Success?ActivityStatusCode.Ok:ActivityStatusCode.Error);
        }
    }
}

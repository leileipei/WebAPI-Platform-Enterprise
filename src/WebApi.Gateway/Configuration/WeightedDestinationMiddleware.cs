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
            var generation=(RuntimeGeneration)ctx.Items[ApiKeyMiddleware.GenerationItem]!;var cluster=generation.Snapshot.Clusters.Single(c=>c.Id==Guid.Parse(feature.Route.Config.Metadata!["runtimeClusterId"]));var weights=cluster.Destinations.ToDictionary(d=>d.Id.ToString(),d=>(long)d.Weight);long Weight(DestinationState d)=>weights[d.DestinationId];var total=options.Sum(Weight);
            DestinationState At(long value) {foreach(var d in options) {value-=Weight(d);if(value<0) return d;}return options[^1];}
            DestinationState PickRandom()=>At(Random.Shared.NextInt64(total));DestinationState selected;
            switch(cluster.LoadBalancingPolicy)
            {
                case "RoundRobin":var counter=generation.Counters.GetOrAdd(cluster.Id,_=>new());var index=(Interlocked.Increment(ref counter.Value)-1)%total;if(index<0) index+=total;selected=At(index);break;
                case "Random":selected=PickRandom();break;
                case "LeastRequests":selected=options.MinBy(d=>(d.ConcurrentRequestCount+1)/(double)Weight(d))!;break;
                case "PowerOfTwoChoices":var a=PickRandom();var b=PickRandom();selected=(a.ConcurrentRequestCount+1)/(double)Weight(a)<=(b.ConcurrentRequestCount+1)/(double)Weight(b)?a:b;break;
                default:selected=options.OrderBy(d=>d.DestinationId,StringComparer.Ordinal).First();break;
            }
            feature.AvailableDestinations=selected;
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

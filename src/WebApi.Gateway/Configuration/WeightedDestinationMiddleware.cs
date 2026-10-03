using WebApi.Gateway.Security;
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
        await next(ctx);
    }
}

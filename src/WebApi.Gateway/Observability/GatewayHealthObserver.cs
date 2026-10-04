using WebApi.Gateway.Configuration;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Observability;
public sealed record DestinationObservation(Guid EnvironmentId,string NodeName,Guid ClusterId,Guid DestinationId,DateTimeOffset ObservedAt,string Health);
public sealed class GatewayHealthObserver(RuntimeGenerationStore generations,IProxyStateLookup lookup,GatewaySettings settings)
{
    public IReadOnlyList<DestinationObservation> Read()
    {
        var generation=generations.Current;if(generation is null)return[];var rows=new List<DestinationObservation>();var now=DateTimeOffset.UtcNow;
        foreach(var cluster in generation.Snapshot.Clusters)
        {
            lookup.TryGetCluster(generation.Envelope.DeploymentSequence+":"+cluster.Id,out var state);
            foreach(var destination in cluster.Destinations)
            {
                var health="Unknown";
                if(state is not null&&state.Destinations.TryGetValue(destination.Id.ToString(),out var actual))
                {
                    if(actual.Health.Active==DestinationHealth.Unhealthy||actual.Health.Passive==DestinationHealth.Unhealthy)health="Unhealthy";
                    else if(actual.Health.Active==DestinationHealth.Healthy||actual.Health.Passive==DestinationHealth.Healthy)health="Healthy";
                }
                rows.Add(new(settings.EnvironmentId,settings.NodeName,cluster.SourceId??cluster.Id,destination.Id,now,health));
            }
        }
        return rows;
    }
}

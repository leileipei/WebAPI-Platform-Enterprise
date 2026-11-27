using WebApi.Gateway.Configuration;
using WebApi.Contracts.Gateway;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Observability;
public sealed record DestinationObservation(Guid EnvironmentId,string NodeName,Guid ClusterId,Guid DestinationId,DateTimeOffset ObservedAt,string Health,string ActiveHealth,string PassiveHealth,Guid RuntimeClusterId,string TargetFingerprint,long Sequence);
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
                var active="Unknown";var passive="Unknown";
                if(state is not null&&state.Destinations.TryGetValue(destination.Id.ToString(),out var actual))
                {
                    active=actual.Health.Active==DestinationHealth.Unknown&&state.Model.Config.HealthCheck?.Active?.Enabled!=true?"Disabled":actual.Health.Active.ToString();
                    passive=actual.Health.Passive==DestinationHealth.Unknown&&state.Model.Config.HealthCheck?.Passive?.Enabled!=true?"Disabled":actual.Health.Passive.ToString();
                    if(actual.Health.Active==DestinationHealth.Unhealthy||actual.Health.Passive==DestinationHealth.Unhealthy)health="Unhealthy";
                    else if(actual.Health.Active==DestinationHealth.Healthy||actual.Health.Passive==DestinationHealth.Healthy)health="Healthy";
                }
                rows.Add(new(settings.EnvironmentId,settings.NodeName,cluster.SourceId??cluster.Id,destination.Id,now,health,active,passive,cluster.Id,GatewayProbeTarget.Fingerprint(destination.Address,cluster.HealthCheckPath),generation.Envelope.DeploymentSequence));
            }
        }
        return rows;
    }
}

using WebApi.Contracts.Runtime;
using WebApi.Gateway.Configuration;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Forwarding;
public sealed class WeightedDestinationSelector(ForwardAttemptRegistry attempts)
{
    public DestinationState? Select(RuntimeGeneration generation, RuntimeCluster cluster, IReadOnlyList<DestinationState> available, IReadOnlySet<string> attempted)
    {
        var weights = cluster.Destinations.ToDictionary(d => d.Id.ToString(), d => (long)d.Weight);
        var options = available.Where(d => weights.ContainsKey(d.DestinationId)).ToArray();
        if (options.Length == 0) return null;
        var untried = options.Where(d => !attempted.Contains(d.DestinationId)).ToArray();
        if (untried.Length > 0) options = untried;
        if (options.Length == 1) return options[0];
        long Weight(DestinationState destination) => weights[destination.DestinationId];
        var total = options.Sum(Weight);
        DestinationState At(long value) { foreach (var destination in options) { value -= Weight(destination); if (value < 0) return destination; } return options[^1]; }
        DestinationState Random() => At(System.Random.Shared.NextInt64(total));
        double Load(DestinationState destination) => (destination.ConcurrentRequestCount + attempts.Count(generation, destination) + 1) / (double)Weight(destination);
        switch (cluster.LoadBalancingPolicy)
        {
            case "RoundRobin": var counter = generation.Counters.GetOrAdd(cluster.Id, _ => new()); var index = (Interlocked.Increment(ref counter.Value) - 1) % total; if (index < 0) index += total; return At(index);
            case "Random": return Random();
            case "LeastRequests": return options.MinBy(Load)!;
            case "PowerOfTwoChoices": var a = Random(); var b = Random(); return Load(a) <= Load(b) ? a : b;
            default: return options.OrderBy(d => d.DestinationId, StringComparer.Ordinal).First();
        }
    }
}

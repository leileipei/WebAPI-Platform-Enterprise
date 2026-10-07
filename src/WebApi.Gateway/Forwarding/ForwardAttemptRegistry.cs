using WebApi.Gateway.Configuration;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Forwarding;
public sealed class ForwardAttemptRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<(RuntimeGeneration Generation, string Destination), int> counts = [];
    public int ActiveEntryCount { get { lock (sync) return counts.Count; } }
    public IDisposable Acquire(RuntimeGeneration generation, DestinationState destination)
    {
        var key = (generation, destination.DestinationId);
        lock (sync) counts[key] = counts.GetValueOrDefault(key) + 1;
        return new Lease(() => { lock (sync) { var remaining = counts[key] - 1; if (remaining == 0) counts.Remove(key); else counts[key] = remaining; } });
    }
    public int Count(RuntimeGeneration generation, DestinationState destination)
    { lock (sync) return counts.GetValueOrDefault((generation, destination.DestinationId)); }
    private sealed class Lease(Action release) : IDisposable
    { private int disposed; public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) release(); } }
}

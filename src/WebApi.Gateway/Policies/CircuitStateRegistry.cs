using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
namespace WebApi.Gateway.Policies;
public sealed record CircuitKey(Guid InstanceId,Guid RuntimePolicyId,Guid RouteId,Guid RuntimeClusterId);
public sealed class CircuitStateRegistry(TimeProvider? clock=null)
{
    private sealed class Entry(CircuitBreakerState state,byte[] config) {public CircuitBreakerState State {get;}=state;public byte[] Config {get;}=config;public int References;}
    private readonly object sync=new();private readonly Dictionary<CircuitKey,Entry> states=[];
    public event Action<CircuitKey,RuntimePolicy,CircuitStatus,CircuitStatus>? Transitioned;
    public int Count {get {lock(sync) return states.Count;}}
    public CircuitStateLease Acquire(CircuitKey key,CircuitBreakerConfiguration configuration,RuntimePolicy? policy=null)
    {
        lock(sync) {
            var config=CanonicalJson.Serialize(configuration);
            if(!states.TryGetValue(key,out var entry)) {entry=new(new(configuration,clock??TimeProvider.System),config);states.Add(key,entry);if(policy is not null) entry.State.Transitioned+=(prior,next)=>Transitioned?.Invoke(key,policy,prior,next);}
            else if(!entry.Config.AsSpan().SequenceEqual(config)) throw new InvalidOperationException("Circuit identity has conflicting configuration.");
            entry.References++;return new(entry.State,()=>{lock(sync) {entry.References--;if(entry.References==0) states.Remove(key);}});
        }
    }
}
public sealed class CircuitStateLease(CircuitBreakerState state,Action release) : IDisposable
{public CircuitBreakerState State {get;}=state;private int disposed;public void Dispose() {if(Interlocked.Exchange(ref disposed,1)==0) release();}}

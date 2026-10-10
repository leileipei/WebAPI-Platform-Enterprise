using System.Collections.Concurrent;
using WebApi.Contracts.Runtime;
using System.Collections.ObjectModel;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Gateway.Policies;
namespace WebApi.Gateway.Configuration;
public sealed class RuntimeGeneration
{
    public RuntimeGeneration(SnapshotEnvelope envelope,byte[] payload,RuntimeSnapshot snapshot)
    {
        Envelope=envelope;Payload=payload.ToArray();Snapshot=Freeze(snapshot);
        PoliciesById=new ReadOnlyDictionary<Guid,RuntimePolicy>(Snapshot.Policies.ToDictionary(p=>p.Id));
        RateLimits=new ReadOnlyDictionary<Guid,RateLimitConfiguration>(Snapshot.Policies.Where(p=>p.Type=="rate_limit").ToDictionary(p=>p.Id,p=>PolicyConfigurationValidator.ParseRate(p.Config)));
        CircuitConfigurations=new ReadOnlyDictionary<Guid,CircuitBreakerConfiguration>(Snapshot.Policies.Where(p=>p.Type=="circuit_breaker").ToDictionary(p=>p.Id,p=>PolicyConfigurationValidator.ParseCircuit(p.Config)));
        Authentication=new ReadOnlyDictionary<Guid,AuthenticationConfiguration>(Snapshot.Policies.Where(p=>p.Type=="authentication").ToDictionary(p=>p.Id,p=>PolicyConfigurationValidator.ParseAuthentication(p.Config)));
        Retry=new ReadOnlyDictionary<Guid,RetryConfiguration>(Snapshot.Policies.Where(p=>p.Type=="retry").ToDictionary(p=>p.Id,p=>PolicyConfigurationValidator.ParseRetry(p.Config)));
        Cache=new ReadOnlyDictionary<Guid,CacheConfiguration>(Snapshot.Policies.Where(p=>p.Type=="cache").ToDictionary(p=>p.Id,p=>PolicyConfigurationValidator.ParseCache(p.Config)));
        Indexes=RuntimeLookupIndex.Build(Snapshot,Authentication);
    }
    public RuntimeLookupIndex Indexes {get;}
    public SnapshotEnvelope Envelope {get;}public ReadOnlyMemory<byte> Payload {get;}public RuntimeSnapshot Snapshot {get;}
    public IReadOnlyDictionary<Guid,RuntimePolicy> PoliciesById {get;}
    public IReadOnlyDictionary<Guid,RateLimitConfiguration> RateLimits {get;}
    public IReadOnlyDictionary<Guid,CircuitBreakerConfiguration> CircuitConfigurations {get;}
    public IReadOnlyDictionary<Guid,AuthenticationConfiguration> Authentication {get;}
    public IReadOnlyDictionary<Guid,RetryConfiguration> Retry {get;}
    public IReadOnlyDictionary<Guid,CacheConfiguration> Cache {get;}
    internal Dictionary<Guid,CircuitStateLease> CircuitLeases {get;}=[];
    private static RuntimeSnapshot Freeze(RuntimeSnapshot snapshot)=>snapshot with
    {
        Routes=Array.AsReadOnly(snapshot.Routes.Select(r=>r with {Methods=Array.AsReadOnly(r.Methods.ToArray()),PolicyBindings=r.PolicyBindings is null?null:Array.AsReadOnly(r.PolicyBindings.ToArray())}).ToArray()),
        Clusters=Array.AsReadOnly(snapshot.Clusters.Select(c=>c with {Destinations=Array.AsReadOnly(c.Destinations.ToArray())}).ToArray()),
        Policies=Array.AsReadOnly(snapshot.Policies.ToArray()),
        Applications=Array.AsReadOnly(snapshot.Applications.Select(a=>a with {Credentials=Array.AsReadOnly(a.Credentials.ToArray()),Permissions=Array.AsReadOnly(a.Permissions.ToArray())}).ToArray())
    };
    internal int Leases;internal bool Retired;internal ConcurrentDictionary<Guid,Counter> Counters {get;}=new();internal sealed class Counter {public long Value;}
}
public sealed class RuntimeGenerationStore(CircuitStateRegistry? circuits=null,GatewaySettings? settings=null) : IDisposable
{
    private readonly object sync=new();private readonly Dictionary<long,RuntimeGeneration> generations=[];private RuntimeGeneration? current;
    public RuntimeGeneration? Current {get {lock(sync) return current;}}
    public void Register(RuntimeGeneration generation)
    {
        lock(sync) {
            if(generations.ContainsKey(generation.Envelope.DeploymentSequence)) throw new InvalidOperationException("Generation sequence already registered.");
            try {
                foreach(var route in generation.Snapshot.Routes) foreach(var b in route.PolicyBindings??[]) if(generation.CircuitConfigurations.TryGetValue(b.PolicyId,out var config)) {
                    if(circuits is null||settings is null) throw new InvalidOperationException("Circuit registry and node instance are required.");
                    generation.CircuitLeases.Add(route.Id,circuits.Acquire(new(settings.InstanceId,b.PolicyId,route.Id,route.ClusterId),config,generation.PoliciesById[b.PolicyId]));
                }
                generations.Add(generation.Envelope.DeploymentSequence,generation);
            }catch {foreach(var lease in generation.CircuitLeases.Values) lease.Dispose();generation.CircuitLeases.Clear();throw;}
        }
    }
    public void Activate(long sequence)
    {lock(sync) {var next=generations[sequence];var prior=current;current=next;if(prior is not null&&prior!=next) {prior.Retired=true;Collect(prior);}}}
    public void Discard(long sequence) {lock(sync) {if(generations.TryGetValue(sequence,out var value)&&value!=current) {value.Retired=true;Collect(value);}}}
    private void Collect(RuntimeGeneration generation) {if(generation.Retired&&generation.Leases==0) {generations.Remove(generation.Envelope.DeploymentSequence);foreach(var lease in generation.CircuitLeases.Values) lease.Dispose();generation.CircuitLeases.Clear();}}
    public void Dispose() {lock(sync) {current=null;foreach(var generation in generations.Values.ToArray()) {generation.Retired=true;Collect(generation);}}}
    public GenerationLease Acquire(long sequence)
    {lock(sync) {if(!generations.TryGetValue(sequence,out var generation)) throw new InvalidOperationException("Routing generation is unavailable.");generation.Leases++;return new(generation,()=>{lock(sync) {generation.Leases--;Collect(generation);}});}}
    public sealed class GenerationLease(RuntimeGeneration generation,Action release) : IDisposable {public RuntimeGeneration Generation {get;}=generation;private int disposed;public void Dispose() {if(Interlocked.Exchange(ref disposed,1)==0) release();}}
}

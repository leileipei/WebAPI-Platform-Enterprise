using System.Collections.Concurrent;
using WebApi.Contracts.Runtime;
namespace WebApi.Gateway.Configuration;
public sealed class RuntimeGeneration(SnapshotEnvelope envelope,byte[] payload,RuntimeSnapshot snapshot)
{
    public SnapshotEnvelope Envelope {get;}=envelope;public ReadOnlyMemory<byte> Payload {get;}=payload.ToArray();public RuntimeSnapshot Snapshot {get;}=Freeze(snapshot);
    private static RuntimeSnapshot Freeze(RuntimeSnapshot snapshot)=>snapshot with
    {
        Routes=Array.AsReadOnly(snapshot.Routes.Select(r=>r with {Methods=Array.AsReadOnly(r.Methods.ToArray())}).ToArray()),
        Clusters=Array.AsReadOnly(snapshot.Clusters.Select(c=>c with {Destinations=Array.AsReadOnly(c.Destinations.ToArray())}).ToArray()),
        Policies=Array.AsReadOnly(snapshot.Policies.ToArray()),
        Applications=Array.AsReadOnly(snapshot.Applications.Select(a=>a with {Credentials=Array.AsReadOnly(a.Credentials.ToArray()),Permissions=Array.AsReadOnly(a.Permissions.ToArray())}).ToArray())
    };
    internal int Leases;internal bool Retired;internal ConcurrentDictionary<Guid,Counter> Counters {get;}=new();internal sealed class Counter {public long Value;}
}
public sealed class RuntimeGenerationStore
{
    private readonly object sync=new();private readonly Dictionary<long,RuntimeGeneration> generations=[];private RuntimeGeneration? current;
    public RuntimeGeneration? Current {get {lock(sync) return current;}}
    public void Register(RuntimeGeneration generation) {lock(sync) generations.Add(generation.Envelope.DeploymentSequence,generation);}
    public void Activate(long sequence)
    {lock(sync) {var next=generations[sequence];var prior=current;current=next;if(prior is not null&&prior!=next) {prior.Retired=true;Collect(prior);}}}
    public void Discard(long sequence) {lock(sync) {if(generations.TryGetValue(sequence,out var value)&&value!=current) {value.Retired=true;Collect(value);}}}
    private void Collect(RuntimeGeneration generation) {if(generation.Retired&&generation.Leases==0) generations.Remove(generation.Envelope.DeploymentSequence);}
    public GenerationLease Acquire(long sequence)
    {lock(sync) {if(!generations.TryGetValue(sequence,out var generation)) throw new InvalidOperationException("Routing generation is unavailable.");generation.Leases++;return new(generation,()=>{lock(sync) {generation.Leases--;Collect(generation);}});}}
    public sealed class GenerationLease(RuntimeGeneration generation,Action release) : IDisposable {public RuntimeGeneration Generation {get;}=generation;private int disposed;public void Dispose() {if(Interlocked.Exchange(ref disposed,1)==0) release();}}
}

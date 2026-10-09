using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Gateway.Configuration;
namespace WebApi.PerformanceHost;
public sealed record GenerationMeasurement(TimeSpan Elapsed,long AllocatedBytes,long HeapBytes);
public sealed record GenerationLifecycleMeasurement(long EmptyHeapBytes,long SingleHeapBytes,long HeldDualHeapBytes,long ReleasedHeapBytes,bool OldLeaseUsable,bool RetiredRemoved,bool RetiredCollected,GenerationMeasurement NewBuild);
public static class GenerationProbe
{
    public static GenerationLifecycleMeasurement MeasureLifecycle(RuntimeSnapshot snapshot,byte[] payload)
    {
        using var store=new RuntimeGenerationStore();var empty=GC.GetTotalMemory(true);
        var (measurement,retired)=BuildHeldPair(store,snapshot,payload,empty);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        var released=GC.GetTotalMemory(true);var collected=!retired.IsAlive;GC.KeepAlive(store);
        return measurement with {ReleasedHeapBytes=released,RetiredCollected=collected};
    }
    // No caller retains the old generation or its disposed lease when collection is measured.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (GenerationLifecycleMeasurement Measurement,WeakReference Retired) BuildHeldPair(RuntimeGenerationStore store,RuntimeSnapshot snapshot,byte[] payload,long empty)
    {
        var old=new RuntimeGeneration(new(Guid.NewGuid(),1,snapshot.ConfigVersion,Convert.ToHexStringLower(SHA256.HashData(payload)),payload.Length),payload,snapshot);
        var retired=new WeakReference(old);store.Register(old);store.Activate(1);var single=GC.GetTotalMemory(true);
        using var lease=store.Acquire(1);
        var secondSnapshot=snapshot with {ConfigVersion=snapshot.ConfigVersion+1};var secondPayload=CanonicalJson.Serialize(secondSnapshot);
        var envelope=new SnapshotEnvelope(Guid.NewGuid(),2,secondSnapshot.ConfigVersion,Convert.ToHexStringLower(SHA256.HashData(secondPayload)),secondPayload.Length);
        var before=GC.GetAllocatedBytesForCurrentThread();var start=Stopwatch.GetTimestamp();
        var next=new RuntimeGeneration(envelope,secondPayload,secondSnapshot);
        var elapsed=Stopwatch.GetElapsedTime(start);var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        store.Register(next);store.Activate(2);var held=GC.GetTotalMemory(true);
        var usable=lease.Generation.Envelope.DeploymentSequence==1&&lease.Generation.Payload.Span.SequenceEqual(payload);
        lease.Dispose();var removed=false;
        try{using var unexpected=store.Acquire(1);}catch(InvalidOperationException){removed=true;}
        return (new(empty,single,held,0,usable,removed,false,new(elapsed,allocated,held)),retired);
    }
    public static GenerationMeasurement MeasureBuild(RuntimeSnapshot snapshot,SnapshotEnvelope envelope,byte[] payload)
    {
        var before=GC.GetAllocatedBytesForCurrentThread();var start=Stopwatch.GetTimestamp();
        var generation=new RuntimeGeneration(envelope,payload,snapshot);
        var elapsed=Stopwatch.GetElapsedTime(start);var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        var heap=GC.GetTotalMemory(false);GC.KeepAlive(generation);
        return new(elapsed,allocated,heap);
    }
}

using WebApi.Contracts.CredentialUsage;
namespace WebApi.Gateway.CredentialUsage;

// All request work is bounded in-memory state; only the single reporter drains it to durable storage.
public sealed class CredentialUsageAccumulator(CredentialUsageOptions options):ICredentialUsageRecorder
{
    private readonly object sync=new();
    private Dictionary<Key,UsageObservation> active=[];
    private readonly Dictionary<string,UsageInterval> losses=new(StringComparer.Ordinal);
    private long dropped;
    public long DroppedCount=>Volatile.Read(ref dropped);
    private sealed record Key(Guid EnvironmentId,Guid ApplicationId,Guid CredentialId,Guid ApiId,DateTimeOffset From,DateTimeOffset? To,UsageSource Source,DateTimeOffset Hour);
    public bool TryRecord(UsageObservation observation)
    {
        if(!options.Enabled)return false;
        var item=observation.Item;
        lock(sync)
        {
            var key=new Key(observation.EnvironmentId,item.ApplicationId,item.CredentialId,item.ApiId,item.GrantValidFrom,item.GrantExpiresAt,item.Source,item.HourStart);
            if(active.TryGetValue(key,out var previous))
            {
                if(previous.Item.Count==long.MaxValue){Drop(item.LastObservedAt,"AccumulatorOverflow");return false;}
                active[key]=observation with{Item=item with{Count=previous.Item.Count+1,FirstObservedAt=previous.Item.FirstObservedAt<item.FirstObservedAt?previous.Item.FirstObservedAt:item.FirstObservedAt,LastObservedAt=previous.Item.LastObservedAt>item.LastObservedAt?previous.Item.LastObservedAt:item.LastObservedAt}};return true;
            }
            if(active.Count>=options.MaxInMemoryKeys){Drop(item.LastObservedAt,"AccumulatorFull");return false;}
            active.Add(key,observation with{Item=item with{Count=1}});return true;
        }
    }
    private void Drop(DateTimeOffset at,string reason)
    {if(dropped<long.MaxValue)dropped++;MarkGapCore(at,reason);}
    public void MarkGap(DateTimeOffset at,string reason)
    {
        if(!options.Enabled)return;
        lock(sync)MarkGapCore(at,reason is "AccumulatorFull" or "AccumulatorOverflow"?reason:"RecorderFailure");
    }
    private void MarkGapCore(DateTimeOffset at,string reason)
    {
        at=at.ToUniversalTime();var end=at==DateTimeOffset.MaxValue?at:at.AddTicks(1);if(at==end)at=at.AddTicks(-1);
        if(losses.TryGetValue(reason,out var before))losses[reason]=new(before.From<at?before.From:at,before.To>end?before.To:end);
        else losses[reason]=new(at,end);
    }
    public IReadOnlyList<UsageObservation> Drain(DateTimeOffset through)
    {
        Dictionary<Key,UsageObservation> handedOff;
        lock(sync){handedOff=active;active=[];}
        // Handoff is atomic, including records racing the caller's clock read. The writer seals through
        // at least the maximum observed timestamp, never advertising coverage ahead of unsealed counts.
        return handedOff.Values.ToArray();
    }
    public IReadOnlyList<UsageGap> DrainGaps(DateTimeOffset through)
    {
        lock(sync)
        {
            var result=losses.Select(x=>new UsageGap(x.Value.From,x.Value.To,x.Key)).ToArray();losses.Clear();return result;
        }
    }
}

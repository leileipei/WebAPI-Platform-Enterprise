using System.Text.Json;
using WebApi.Contracts.CredentialUsage;
namespace WebApi.Gateway.CredentialUsage;
public sealed class UsageBatchSealer(CredentialUsageOptions options)
{
    private sealed class Block {public readonly List<UsageItem> Items=[];public readonly List<UsageGap> Gaps=[];public readonly List<UsageInterval> Intervals=[];}
    public UsageCheckpoint Seal(UsageCheckpoint checkpoint,Guid producerInstanceId,DateTimeOffset now)
    {
        if(producerInstanceId==Guid.Empty||checkpoint.CollectorId==Guid.Empty||checkpoint.NextSequence<1)throw new InvalidDataException("Invalid usage checkpoint identity.");
        now=now.ToUniversalTime();if(checkpoint.Active.Count>options.MaxInMemoryKeys)throw new InvalidDataException("Usage active key budget exceeded.");
        var ledger=new UsageGapLedger();foreach(var gap in checkpoint.Gaps)ledger.Add(gap);var oldest=now.AddHours(-options.MaxPendingAgeHours);
        var expired=checkpoint.Pending.Any(b=>b.SealedAt<oldest);var capacity=Bytes(checkpoint.Pending)>options.MaxPendingBytes;
        if(expired||capacity)
        {
            // Unacknowledged old batches might already have applied. Never reseal them under another ID.
            var start=checkpoint.DurableThrough<now?checkpoint.DurableThrough:now.AddTicks(-1);
            ledger.Add(new(start,now,expired?"PendingExpired":"PendingCapacity"));
            checkpoint=checkpoint with{CollectorId=Guid.NewGuid(),NextSequence=1,Pending=[]};
        }
        var active=checkpoint.Active.Where(x=>x.Item.LastObservedAt>=oldest).ToArray();
        if(active.Length!=checkpoint.Active.Count)ledger.Add(new(oldest,now,"PendingExpired"));
        if(active.Any(x=>x.EnvironmentId!=checkpoint.EnvironmentId||x.Item.Count<1))throw new InvalidDataException("Invalid active credential usage.");
        if(active.Length>0&&active.Max(x=>x.Item.LastObservedAt)>now)now=active.Max(x=>x.Item.LastObservedAt);
        var blocks=new List<Block>();var block=new Block();
        UsageBatchCore Core(Block value,long sequence)=>new("1.0",producerInstanceId,checkpoint.CollectorId,sequence,now,now,Array.AsReadOnly(value.Intervals.ToArray()),Array.AsReadOnly(value.Gaps.ToArray()),Array.AsReadOnly(value.Items.ToArray()));
        int Size(Block value)=>JsonSerializer.SerializeToUtf8Bytes(new UsageBatchRequest(producerInstanceId,Core(value,long.MaxValue-1)),UsageProtocolJson.Options).Length;
        void Flush(){if(block.Items.Count>0||block.Gaps.Count>0||block.Intervals.Count>0){blocks.Add(block);block=new();}}
        foreach(var observation in active)
        {
            var remaining=observation.Item.Count;
            while(remaining>0)
            {
                var item=observation.Item with{Count=Math.Min(1000000,remaining)};
                var duplicate=block.Items.Any(x=>SameKey(x,item));
                if(block.Items.Count>=options.MaxBatchEntries||duplicate||block.Items.Select(x=>x.Source).Append(item.Source).Distinct().Count()>2)Flush();
                block.Items.Add(item);
                if(Size(block)>options.MaxBatchBytes){block.Items.RemoveAt(block.Items.Count-1);Flush();block.Items.Add(item);if(Size(block)>options.MaxBatchBytes)throw new InvalidDataException("Usage item exceeds batch budget.");}
                remaining-=item.Count;
                if(blocks.Count>options.MaxPendingBytes/256)throw new InvalidDataException("Usage delta exceeds bounded sealing budget.");
            }
        }
        var boundedGaps=ledger.Snapshot();
        foreach(var gap in boundedGaps)
        {
            var start=gap.From<oldest?oldest:gap.From;var end=gap.To>now?now:gap.To;if(start>=end)continue;
            var clipped=gap with{From=start,To=end};block.Gaps.Add(clipped);
            if(Size(block)>options.MaxBatchBytes){block.Gaps.RemoveAt(block.Gaps.Count-1);Flush();block.Gaps.Add(clipped);}
        }
        var coverageStart=checkpoint.DurableThrough<oldest?oldest:checkpoint.DurableThrough;
        if(coverageStart<now)
        {
            var interval=new UsageInterval(coverageStart,now);block.Intervals.Add(interval);
            if(Size(block)>options.MaxBatchBytes){block.Intervals.Clear();Flush();block.Intervals.Add(interval);}
        }
        Flush();
        var pending=checkpoint.Pending.ToList();var next=checkpoint.NextSequence;
        foreach(var part in blocks){if(next==long.MaxValue)throw new InvalidDataException("Usage sequence exhausted.");pending.Add(Core(part,next++));}
        if(Bytes(pending)>options.MaxPendingBytes)
        {
            // This seal has not been published or returned. Preserve original pending identities and
            // record loss before starting a fresh epoch; do not rewrite any already frozen core.
            ledger.Add(new(coverageStart<now?coverageStart:now.AddTicks(-1),now,"PendingCapacity"));
            var fresh=checkpoint with{CollectorId=Guid.NewGuid(),NextSequence=1,Pending=[],Active=[],Gaps=ledger.Snapshot()};
            return Seal(fresh,producerInstanceId,now);
        }
        return checkpoint with{NextSequence=next,DurableThrough=now,Active=[],Pending=Array.AsReadOnly(pending.ToArray()),Gaps=boundedGaps};
    }
    private static long Bytes(IReadOnlyList<UsageBatchCore> batches)
    {long bytes=0;foreach(var batch in batches)bytes=checked(bytes+JsonSerializer.SerializeToUtf8Bytes(batch,UsageProtocolJson.Options).LongLength);return bytes;}
    private static bool SameKey(UsageItem a,UsageItem b)=>a.ApplicationId==b.ApplicationId&&a.CredentialId==b.CredentialId&&a.ApiId==b.ApiId&&a.GrantValidFrom==b.GrantValidFrom&&a.GrantExpiresAt==b.GrantExpiresAt&&a.Source==b.Source&&a.HourStart==b.HourStart;
}

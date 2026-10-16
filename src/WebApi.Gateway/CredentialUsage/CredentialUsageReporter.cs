using WebApi.Contracts.CredentialUsage;
namespace WebApi.Gateway.CredentialUsage;

// The only journal writer and sender. Authentication records never await this service.
public sealed class CredentialUsageReporter(GatewaySettings settings,CredentialUsageOptions options,CredentialUsageAccumulator accumulator,UsageJournal journal,UsageBatchSealer sealer,UsageNodeTransport transport,TimeProvider clock,ILogger<CredentialUsageReporter> logger):BackgroundService
{
    private UsageCheckpoint? state;private bool bound;private bool blocked;private DateTimeOffset nextSend;private DateTimeOffset nextSeal;private int failures;
    public string Status {get;private set;}="Starting";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!options.Enabled){Status="Disabled";return;}
        try
        {
            try{state=await journal.LoadAsync(stoppingToken);}
            catch(InvalidDataException) when(!File.Exists(journal.FilePath)){state=Fresh(clock.GetUtcNow(),"JournalCorrupt");}
            state??=Fresh(clock.GetUtcNow(),journal.RecoveredOrphanedWrite?"OrphanedCheckpoint":null);
            // Old active data must retain its producing instance before current requests join it.
            if(journal.LastWriterInstanceId is Guid producer&&producer!=settings.InstanceId&&state.Active.Count>0)state=sealer.Seal(state,producer,clock.GetUtcNow());
            await journal.SaveAsync(state,stoppingToken);nextSeal=clock.GetUtcNow().AddSeconds(options.FlushIntervalSeconds);
            while(!stoppingToken.IsCancellationRequested)
            {
                try{await TickAsync(stoppingToken);}
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
                catch(Exception error) when(error is not OutOfMemoryException)
                {Status="Unavailable";logger.LogWarning("Credential usage checkpoint unavailable ({ErrorType}).",error.GetType().Name);DelayRetry();}
                // A maintenance timer must not share a request retry test/virtual-timer barrier.
                // Observation timestamps still use the injected UTC clock.
                await Task.Delay(TimeSpan.FromMilliseconds(options.CheckpointIntervalMs),stoppingToken);
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){}
        catch(Exception error) when(error is not OutOfMemoryException){Status="Unavailable";logger.LogWarning("Credential usage recovery unavailable ({ErrorType}).",error.GetType().Name);}
        finally
        {
            // A bounded local flush, independent of an unreachable/old control plane.
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if(state is not null)try{Capture();state=sealer.Seal(state,settings.InstanceId,clock.GetUtcNow());await journal.SaveAsync(state,stop.Token);await journal.MarkCleanStopAsync(stop.Token);}catch(Exception error) when(error is not OutOfMemoryException){Status="Unavailable";logger.LogWarning("Credential usage stop checkpoint unavailable ({ErrorType}).",error.GetType().Name);}
        }
    }
    private UsageCheckpoint Fresh(DateTimeOffset now,string? reason=null)=>new(settings.EnvironmentId,settings.NodeName,Guid.NewGuid(),1,now,[],[],reason is null?[]:[new(now.AddTicks(-1),now,reason)]);
    private void Capture()
    {
        var now=clock.GetUtcNow();var active=state!.Active.ToList();var gaps=new UsageGapLedger();foreach(var gap in state.Gaps.Concat(accumulator.DrainGaps(now)))gaps.Add(gap);
        foreach(var observation in accumulator.Drain(now))
        {
            var index=active.FindIndex(x=>x.EnvironmentId==observation.EnvironmentId&&Same(x.Item,observation.Item));
            if(index>=0)
            {
                var before=active[index].Item;try{active[index]=observation with{Item=observation.Item with{Count=checked(before.Count+observation.Item.Count),FirstObservedAt=before.FirstObservedAt<observation.Item.FirstObservedAt?before.FirstObservedAt:observation.Item.FirstObservedAt,LastObservedAt=before.LastObservedAt>observation.Item.LastObservedAt?before.LastObservedAt:observation.Item.LastObservedAt}};}
                catch(OverflowException){gaps.Add(new(observation.Item.FirstObservedAt,observation.Item.LastObservedAt.AddTicks(1),"AccumulatorOverflow"));}
            }
            else if(active.Count<options.MaxInMemoryKeys)active.Add(observation);
            else gaps.Add(new(observation.Item.FirstObservedAt,observation.Item.LastObservedAt.AddTicks(1),"AccumulatorFull"));
        }
        state=state with{Active=active.ToArray(),Gaps=gaps.Snapshot()};
    }
    private async Task TickAsync(CancellationToken ct)
    {
        Capture();var now=clock.GetUtcNow();var previous=state!.CollectorId;
        if(now>=nextSeal){state=sealer.Seal(state,settings.InstanceId,now);nextSeal=now.AddSeconds(options.FlushIntervalSeconds);}
        if(previous!=state.CollectorId)bound=false;
        await journal.SaveAsync(state,ct); // Never send an increment that has not reached durable storage.
        if(blocked||now<nextSend)return;
        try
        {
            if(!bound)
            {
                var receipt=await transport.BindAsync(new("1.0",settings.InstanceId,state.CollectorId),ct);
                var first=state.Pending.Count>0?state.Pending[0].Sequence:state.NextSequence;
                if(receipt.NextExpectedSequence>state.NextSequence||receipt.NextExpectedSequence<first){await RewindAsync(ct);return;}
                bound=true;
            }
            if(state.Pending.Count>0)
            {
                var core=state.Pending[0];var receipt=await transport.SendAsync(new(settings.InstanceId,core),ct);
                // AlreadyApplied proves this exact core hash even when later retained batches also applied.
                // Replay each remaining core for its own hash proof; never skip them on cursor alone.
                if(receipt.NextExpectedSequence<core.Sequence+1||receipt.NextExpectedSequence>state.NextSequence||receipt.Result=="Applied"&&receipt.NextExpectedSequence!=core.Sequence+1){await RewindAsync(ct);return;}
                state=state with{Pending=state.Pending.Skip(1).ToArray()};await journal.SaveAsync(state,ct);
            }
            failures=0;nextSend=now;Status="Available";
        }
        catch(UsageTransportException error) when(error.Status is 401 or 403){blocked=true;Status="Unavailable";}
        catch(UsageTransportException error) when(error.Status==410||error.Status==409&&error.Code is "usage_sequence_mismatch" or "usage_batch_hash_conflict" or "usage_collector_closed" or "usage_sequence_exhausted"){await RewindAsync(ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception error) when(error is HttpRequestException or OperationCanceledException or UsageTransportException or InvalidDataException or System.Text.Json.JsonException)
        {Status=error is UsageTransportException {Status:404}?"Unsupported":"Unavailable";DelayRetry();}
    }
    private void DelayRetry(){failures=Math.Min(failures+1,6);nextSend=clock.GetUtcNow().AddSeconds(Math.Min(30,Math.Pow(2,failures-1))*(0.8+Random.Shared.NextDouble()*0.2));}
    private async Task RewindAsync(CancellationToken ct)
    {
        var now=clock.GetUtcNow();var from=state!.Pending.SelectMany(x=>x.Intervals.Select(y=>y.From).Concat(x.Items.Select(y=>y.FirstObservedAt))).Append(state.DurableThrough).Min();
        await journal.QuarantineAsync("CheckpointRewind",ct);
        // Pending may already have applied; neither replay it under a new identity nor relabel active uncertainty.
        state=Fresh(now) with{Gaps=[new(from<now?from:now.AddTicks(-1),now,"CheckpointRewind")]};bound=false;Status="Partial";
        await journal.SaveAsync(state,ct);DelayRetry();
    }
    private static bool Same(UsageItem a,UsageItem b)=>a.ApplicationId==b.ApplicationId&&a.CredentialId==b.CredentialId&&a.ApiId==b.ApiId&&a.GrantValidFrom==b.GrantValidFrom&&a.GrantExpiresAt==b.GrantExpiresAt&&a.Source==b.Source&&a.HourStart==b.HourStart;
}

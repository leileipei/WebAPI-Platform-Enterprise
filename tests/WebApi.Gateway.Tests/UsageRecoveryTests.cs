using System.Text.Json;
using WebApi.Contracts.CredentialUsage;
using WebApi.Domain.CredentialUsage;
using WebApi.Gateway.CredentialUsage;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class UsageRecoveryTests
{
    internal static readonly DateTimeOffset Now=DateTimeOffset.UtcNow;
    internal static CredentialUsageOptions Options(int entries=1000,int bytes=256*1024,int pending=8*1024*1024)=>new(true,15,1000,10000,pending,entries,bytes,24);
    internal static UsageObservation Observation(long count=1)
    {
        var observed=Now.AddMinutes(-5);var hour=new DateTimeOffset(observed.Year,observed.Month,observed.Day,observed.Hour,0,0,TimeSpan.Zero);
        return new(Guid.NewGuid(),new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Now.AddDays(-1),Now.AddDays(1),new(Guid.NewGuid(),1,1,new string('a',64)),hour,count,observed,observed));
    }
    internal static UsageCheckpoint Checkpoint(params UsageObservation[] active)
    {
        var observation=active.FirstOrDefault()??Observation();return new(observation.EnvironmentId,"test-node",Guid.NewGuid(),1,Now.AddMinutes(-10),active,[],[]);
    }
    private static Func<UsageCheckpoint,Guid,DateTimeOffset,UsageCheckpoint> Sealer(CredentialUsageOptions? options=null)
    {
        var type=typeof(UsageCheckpoint).Assembly.GetType("WebApi.Gateway.CredentialUsage.UsageBatchSealer");Assert.NotNull(type);var instance=Activator.CreateInstance(type,options??Options());Assert.NotNull(instance);var method=type.GetMethod("Seal");Assert.NotNull(method);return method.CreateDelegate<Func<UsageCheckpoint,Guid,DateTimeOffset,UsageCheckpoint>>(instance);
    }
    private static void Valid(UsageBatchCore core)
    {
        var request=new UsageBatchRequest(Guid.NewGuid(),core);var bytes=JsonSerializer.SerializeToUtf8Bytes(request,UsageProtocolJson.Options);UsageBatchRules.Parse(bytes,Now,24);
    }
    [Fact]
    public void Active_delta_transfers_once_and_existing_core_hash_never_changes()
    {
        var seal=Sealer();var input=Checkpoint(Observation(7));var before=JsonSerializer.Serialize(input,UsageProtocolJson.Options);var producer=Guid.NewGuid();var first=seal(input,producer,Now);var original=Assert.Single(first.Pending);var hash=UsageBatchRules.CoreHash(original);
        Assert.Empty(first.Active);Assert.Equal(2,first.NextSequence);Assert.Equal(7,original.Items.Sum(x=>x.Count));Assert.Equal(producer,original.ProducerInstanceId);Valid(original);
        var second=seal(first,producer,Now.AddSeconds(15));Assert.Equal(hash,UsageBatchRules.CoreHash(second.Pending[0]));Assert.Equal(7,second.Pending.Sum(b=>b.Items.Sum(i=>i.Count)));Assert.Equal(before,JsonSerializer.Serialize(input,UsageProtocolJson.Options));
    }
    [Fact]
    public void Idle_pulse_proves_coverage_without_creating_a_fake_count()
    {
        var input=Checkpoint();var output=Sealer()(input,Guid.NewGuid(),Now);var batch=Assert.Single(output.Pending);Assert.Empty(batch.Items);Assert.NotEmpty(batch.Intervals);Assert.Equal(input.DurableThrough,batch.Intervals[0].From);Assert.Equal(Now,batch.DurableThrough);Valid(batch);
    }
    [Fact]
    public void Million_and_one_splits_across_batches_without_duplicate_item_keys()
    {
        var output=Sealer()(Checkpoint(Observation(1000001)),Guid.NewGuid(),Now);Assert.Equal(2,output.Pending.Count);Assert.Equal(1000001,output.Pending.Sum(b=>b.Items.Sum(i=>i.Count)));Assert.All(output.Pending,b=>{Assert.Single(b.Items);Valid(b);});
        Assert.Empty(output.Pending[0].Intervals);Assert.NotEmpty(output.Pending[1].Intervals);
    }
    [Fact]
    public void Item_byte_and_two_source_limits_bound_every_batch()
    {
        var a=Observation();var active=Enumerable.Range(0,120).Select(i=>a with{Item=a.Item with{CredentialId=Guid.NewGuid(),Source=a.Item.Source with{ReleaseId=Guid.NewGuid()}}}).ToArray();
        var output=Sealer(Options(entries:3,bytes:16*1024))(Checkpoint(active),Guid.NewGuid(),Now);
        Assert.Equal(120,output.Pending.Sum(b=>b.Items.Count));Assert.All(output.Pending,b=>{Assert.InRange(b.Items.Count,1,3);Assert.InRange(b.Items.Select(i=>i.Source).Distinct().Count(),1,2);Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(new UsageBatchRequest(Guid.NewGuid(),b),UsageProtocolJson.Options).Length,1,16*1024);Valid(b);});
        Assert.All(output.Pending.SkipLast(1),b=>Assert.Empty(b.Intervals));Assert.NotEmpty(output.Pending.Last().Intervals);
    }
    [Fact]
    public void Payload_byte_limit_splits_even_before_entry_limit()
    {
        var a=Observation();var active=Enumerable.Range(0,100).Select(_=>a with{Item=a.Item with{CredentialId=Guid.NewGuid()}}).ToArray();var output=Sealer(Options(bytes:16*1024))(Checkpoint(active),Guid.NewGuid(),Now);
        Assert.True(output.Pending.Count>1);Assert.Equal(100,output.Pending.Sum(b=>b.Items.Count));Assert.All(output.Pending,b=>Assert.True(JsonSerializer.SerializeToUtf8Bytes(new UsageBatchRequest(Guid.NewGuid(),b),UsageProtocolJson.Options).Length<=16*1024));
    }
    [Fact]
    public void Sequence_digit_growth_cannot_exceed_exact_wire_byte_budget()
    {
        var a=Observation();var active=Enumerable.Range(0,61).Select(_=>a with{Item=a.Item with{CredentialId=Guid.NewGuid()}}).ToArray();var input=Checkpoint(active) with{NextSequence=9};var producer=Guid.NewGuid();
        var wire=new UsageBatchRequest(producer,new("1.0",producer,input.CollectorId,9,Now,Now,[],[],active.Take(30).Select(x=>x.Item).ToArray()));var limit=JsonSerializer.SerializeToUtf8Bytes(wire,UsageProtocolJson.Options).Length;Assert.InRange(limit,16*1024,256*1024);
        var output=Sealer(Options(bytes:limit))(input,producer,Now);Assert.Contains(output.Pending,b=>b.Sequence>=10);Assert.Equal(61,output.Pending.Sum(b=>b.Items.Count));Assert.All(output.Pending,b=>Assert.True(JsonSerializer.SerializeToUtf8Bytes(new UsageBatchRequest(producer,b),UsageProtocolJson.Options).Length<=limit));
    }
    [Fact]
    public void Expired_pending_never_reuses_or_mutates_an_old_sequence()
    {
        var seal=Sealer();var a=Observation(7);var input=Checkpoint(a);
        var oldItem=a.Item with{HourStart=a.Item.HourStart.AddHours(-26),GrantValidFrom=a.Item.GrantValidFrom.AddHours(-26),GrantExpiresAt=a.Item.GrantExpiresAt?.AddHours(-26),FirstObservedAt=a.Item.FirstObservedAt.AddHours(-26),LastObservedAt=a.Item.LastObservedAt.AddHours(-26)};
        var old=new UsageBatchCore("1.0",Guid.NewGuid(),input.CollectorId,1,Now.AddHours(-25),Now.AddHours(-25),[],[],[oldItem]);input=input with{Active=[],Pending=[old],NextSequence=2,DurableThrough=Now.AddHours(-25)};var hash=UsageBatchRules.CoreHash(old);
        var output=seal(input,Guid.NewGuid(),Now);Assert.NotEqual(input.CollectorId,output.CollectorId);Assert.Contains(output.Gaps,g=>g.Reason=="PendingExpired");Assert.DoesNotContain(output.Pending,b=>b.CollectorId==input.CollectorId);Assert.Equal(hash,UsageBatchRules.CoreHash(old));
    }
    [Fact]
    public void Pending_capacity_creates_explicit_loss_epoch_without_reassigning_old_batches()
    {
        var a=Observation();var initial=Checkpoint();var producer=Guid.NewGuid();var items=Enumerable.Range(0,100).Select(_=>a.Item with{CredentialId=Guid.NewGuid()}).ToArray();
        var pending=Enumerable.Range(1,200).Select(i=>new UsageBatchCore("1.0",producer,initial.CollectorId,i,Now,Now,[],[],items)).ToArray();var input=initial with{Pending=pending,NextSequence=201};
        var output=Sealer()(input,producer,Now);Assert.NotEqual(input.CollectorId,output.CollectorId);Assert.Contains(output.Gaps,g=>g.Reason=="PendingCapacity");Assert.True(JsonSerializer.SerializeToUtf8Bytes(output.Pending,UsageProtocolJson.Options).Length<=8*1024*1024);Assert.All(pending,b=>Assert.Equal(input.CollectorId,b.CollectorId));
    }
    [Fact]
    public void Gap_control_is_bounded_and_never_silently_erases_uncertainty()
    {
        var type=typeof(UsageCheckpoint).Assembly.GetType("WebApi.Gateway.CredentialUsage.UsageGapLedger");Assert.NotNull(type);var ledger=Activator.CreateInstance(type,2048);Assert.NotNull(ledger);var add=type.GetMethod("Add")!.CreateDelegate<Action<UsageGap>>(ledger);var snapshot=type.GetMethod("Snapshot")!.CreateDelegate<Func<IReadOnlyList<UsageGap>>>(ledger);
        for(var i=0;i<200;i++)add(new(Now.AddSeconds(-200+i),Now.AddSeconds(-199+i),"Loss_"+i));var gaps=snapshot();Assert.Contains(gaps,g=>g.Reason=="GapSummaryLimit");Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(gaps,UsageProtocolJson.Options).Length,1,2048);
    }
}

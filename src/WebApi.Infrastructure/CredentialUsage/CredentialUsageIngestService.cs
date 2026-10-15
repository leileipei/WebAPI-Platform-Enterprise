using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.CredentialUsage;
using WebApi.Domain.CredentialUsage;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.CredentialUsage;

public sealed class UsageIngestBudget(CredentialUsageRetentionOptions options):IDisposable
{
    private readonly SemaphoreSlim slots=new(options.IngestConcurrency,options.IngestConcurrency);
    public IDisposable Enter()
    {
        if(!slots.Wait(0))throw new ApiException(503,"usage_ingest_busy","统计接收暂忙，请稍后重试。");return new Lease(slots);
    }
    public CancellationTokenSource Timeout(CancellationToken ct){var result=CancellationTokenSource.CreateLinkedTokenSource(ct);result.CancelAfter(TimeSpan.FromSeconds(options.IngestTimeoutSeconds));return result;}
    private sealed class Lease(SemaphoreSlim slots):IDisposable{private int disposed;public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)slots.Release();}}
    public void Dispose()=>slots.Dispose();
}
public sealed class UsageSequenceException(int status,string code,long nextExpectedSequence):Exception("统计批次顺序或内容身份不匹配。")
{
    public int Status {get;}=status;public string Code {get;}=code;public long NextExpectedSequence {get;}=nextExpectedSequence;
}

public sealed class CredentialUsageIngestService(WebApiDbContext db,UsageSnapshotProofReader proof,UsageIngestBudget budget,CredentialUsageRetentionOptions options,TimeProvider clock,ILogger<CredentialUsageIngestService> logger)
{
    public Task<UsageReceipt> ApplyAsync(NodeIdentity identity,UsageBatchRequest request,CancellationToken ct=default)
        =>ReceiveAsync(identity,_=>Task.FromResult(request),ct);
    public async Task<UsageReceipt> ReceiveAsync(NodeIdentity identity,Func<CancellationToken,Task<UsageBatchRequest>> read,CancellationToken ct=default)
    {
        var callerCancellation=ct;using var lease=budget.Enter();using var timeout=budget.Timeout(ct);ct=timeout.Token;UsageBatchRequest? request=null;
        try
        {
            request=await read(ct);RequireIdentity(request);
            await using var transaction=await db.Database.BeginTransactionAsync(ct);
            await UsageNodeLock.AcquireAsync(db,identity,request.SenderInstanceId,ct);
            var core=request.Core;
            var collector=await db.Set<CredentialUsageCollector>().FromSqlInterpolated($"SELECT * FROM credential_usage_collectors WHERE node_id={identity.NodeId} AND collector_id={core.CollectorId} FOR UPDATE").SingleOrDefaultAsync(ct)
                ??throw new ApiException(409,"usage_collector_required","请先绑定统计来源。");
            if(collector.EnvironmentId!=identity.EnvironmentId||collector.ClosedAt is not null||collector.Protocol!="1.0")throw new ApiException(409,"usage_collector_closed","统计来源已关闭或身份不匹配。");
            var expected=UsageNodeLock.Next(collector.AcceptedSequence);
            if(!await db.Set<CredentialUsageCollectorInstance>().AnyAsync(x=>x.NodeId==identity.NodeId&&x.CollectorId==core.CollectorId&&x.InstanceId==request.SenderInstanceId,ct))throw new ApiException(409,"usage_sender_unbound","当前节点实例尚未绑定统计来源。");
            var receipt=core.Sequence<=collector.AcceptedSequence?await db.Set<CredentialUsageBatch>().AsNoTracking().SingleOrDefaultAsync(x=>x.NodeId==identity.NodeId&&x.CollectorId==core.CollectorId&&x.Sequence==core.Sequence,ct):null;
            // A retired cursor makes no assertion about the incoming content hash and accepts no old counts.
            if(core.Sequence<=collector.AcceptedSequence&&receipt is null)throw new UsageSequenceException(410,"usage_batch_retired",expected);
            var now=clock.GetUtcNow();var bytes=JsonSerializer.SerializeToUtf8Bytes(request,UsageProtocolJson.Options);
            UsageBatchRules.Parse(bytes,now,options.MaxPendingAgeHours);var hash=UsageBatchRules.CoreHash(core);
            if(receipt is not null)
            {
                if(receipt.CoreHash!=hash)throw new UsageSequenceException(409,"usage_batch_hash_conflict",expected);
                await transaction.CommitAsync(ct);return new("AlreadyApplied",expected,receipt.AcceptedThrough);
            }
            if(core.Sequence!=expected)throw new UsageSequenceException(409,"usage_sequence_mismatch",expected);
            if(core.Sequence==long.MaxValue)throw new ApiException(409,"usage_sequence_exhausted","统计来源序列已耗尽，需要建立新的采集来源。");
            await RequireProducerAsync(identity,core,now,ct);
            await proof.ValidateAsync(identity.EnvironmentId,core.Items,ct);
            var grouped=core.Items.GroupBy(x=>new{x.CredentialId,x.HourStart}).Select(group=>
            {
                var apps=group.Select(x=>x.ApplicationId).Distinct().ToArray();if(apps.Length!=1)throw new ApiException(422,"usage_application_mismatch","凭证的发布应用归属不一致。");
                return new HourlyDelta(apps[0],group.Key.CredentialId,group.Key.HourStart,group.Sum(x=>x.Count),group.Max(x=>x.LastObservedAt));
            }).OrderBy(x=>x.CredentialId).ThenBy(x=>x.HourStart).ToArray();
            await AddHourlyAsync(identity.EnvironmentId,grouped,ct);
            await MergeCoverageAsync(identity,core.Intervals,core.Gaps,core.DurableThrough,now,ct);
            collector.AcceptedSequence=core.Sequence;collector.AcceptedThrough=Latest(collector.AcceptedThrough,core.DurableThrough);
            db.Add(new CredentialUsageBatch{NodeId=identity.NodeId,CollectorId=core.CollectorId,Sequence=core.Sequence,CoreHash=hash,AppliedAt=now,AcceptedThrough=collector.AcceptedThrough});
            await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
            return new("Applied",UsageNodeLock.Next(core.Sequence),collector.AcceptedThrough);
        }
        catch(PostgresException error) when(error.SqlState==PostgresErrorCodes.NumericValueOutOfRange&&request is not null)
        {
            db.ChangeTracker.Clear();await TryRecordFaultAsync(identity,request.SenderInstanceId,"CountOverflow",request.Core.Items.Select(x=>new UsageInterval(x.FirstObservedAt,x.LastObservedAt.AddTicks(1))).ToArray(),ct);
            throw new ApiException(409,"usage_count_overflow","凭证统计计数溢出，本批未应用，来源已标记为不完整。");
        }
        catch(ApiException error) when(error.Code=="usage_clock_skew"&&request is not null)
        {
            db.ChangeTracker.Clear();await TryRecordFaultAsync(identity,request.SenderInstanceId,"ClockSkew",[],ct);throw;
        }
        catch(OperationCanceledException) when(!callerCancellation.IsCancellationRequested)
        {throw new ApiException(503,"usage_ingest_timeout","统计接收超过处理预算，请稍后重试。");}
    }
    private static void RequireIdentity(UsageBatchRequest request)
    {
        if(request is null||request.SenderInstanceId==Guid.Empty||request.Core is not {} core||core.Protocol!="1.0"||core.ProducerInstanceId==Guid.Empty||core.CollectorId==Guid.Empty||core.Sequence<1||core.Items is null||core.Intervals is null||core.Gaps is null||core.Items.Count>1000)
            throw new ApiException(422,"invalid_usage_batch","统计批次身份不合法。");
    }
    private async Task RequireProducerAsync(NodeIdentity identity,UsageBatchCore core,DateTimeOffset now,CancellationToken ct)
    {
        if(await db.Set<CredentialUsageCollectorInstance>().AnyAsync(x=>x.NodeId==identity.NodeId&&x.CollectorId==core.CollectorId&&x.InstanceId==core.ProducerInstanceId,ct))return;
        var instance=core.ProducerInstanceId.ToString();
        var registration=await db.Set<GatewayNodeEvent>().FromSqlInterpolated($"SELECT * FROM gateway_node_events WHERE gateway_node_id={identity.NodeId} AND event_type='registered' AND detail->>'instanceId'={instance} ORDER BY id LIMIT 1").AsNoTracking().FirstOrDefaultAsync(ct);
        if(registration is null)throw new ApiException(422,"usage_producer_unproven","旧生产实例没有同节点的持久身份凭据。");
        db.Add(new CredentialUsageCollectorInstance{NodeId=identity.NodeId,CollectorId=core.CollectorId,InstanceId=core.ProducerInstanceId,RegisteredAt=registration.CreatedAt,EndedAt=now,IdentityProof="registered"});
    }
    private sealed record HourlyDelta(Guid ApplicationId,Guid CredentialId,DateTimeOffset HourStart,long Count,DateTimeOffset LastUsedAt);
    private async Task AddHourlyAsync(Guid environmentId,HourlyDelta[] rows,CancellationToken ct)
    {
        if(rows.Length==0)return;
        var json=JsonSerializer.Serialize(rows.Select(x=>new{application_id=x.ApplicationId,credential_id=x.CredentialId,hour_start=x.HourStart,count=x.Count,last_used_at=x.LastUsedAt}),UsageProtocolJson.Options);
        var affected=await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO credential_usage_hourly(environment_id,credential_id,hour_start,application_id,count,last_used_at) SELECT {environmentId},v.credential_id,v.hour_start,v.application_id,v.count,v.last_used_at FROM jsonb_to_recordset({json}::jsonb) AS v(application_id uuid,credential_id uuid,hour_start timestamptz,count bigint,last_used_at timestamptz) ORDER BY v.credential_id,v.hour_start ON CONFLICT(environment_id,credential_id,hour_start) DO UPDATE SET count=credential_usage_hourly.count+EXCLUDED.count,last_used_at=GREATEST(credential_usage_hourly.last_used_at,EXCLUDED.last_used_at) WHERE credential_usage_hourly.application_id=EXCLUDED.application_id",ct);
        if(affected!=rows.Length)throw new ApiException(409,"usage_application_mismatch","已采集凭证的应用归属不一致，本批未应用。");
        var ids=rows.Select(x=>x.CredentialId).Distinct().Order().ToArray();
        // Sorted global credential locks after all hour locks avoid cross-environment update deadlocks.
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM application_credentials WHERE id=ANY({ids}) ORDER BY id FOR UPDATE").ToArrayAsync(ct);
        var latest=JsonSerializer.Serialize(rows.GroupBy(x=>new{x.ApplicationId,x.CredentialId}).Select(g=>new{id=g.Key.CredentialId,application_id=g.Key.ApplicationId,last_used_at=g.Max(x=>x.LastUsedAt)}),UsageProtocolJson.Options);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE application_credentials AS c SET last_used_at=GREATEST(c.last_used_at,v.last_used_at) FROM jsonb_to_recordset({latest}::jsonb) AS v(id uuid,application_id uuid,last_used_at timestamptz) WHERE c.id=v.id AND c.application_id=v.application_id",ct);
    }
    private async Task TryRecordFaultAsync(NodeIdentity identity,Guid sender,string reason,IReadOnlyList<UsageInterval> affected,CancellationToken ct)
    {
        try
        {
            await using var transaction=await db.Database.BeginTransactionAsync(ct);await UsageNodeLock.AcquireAsync(db,identity,sender,ct);var now=clock.GetUtcNow();
            var gaps=affected.Count==0?[new UsageGap(now.AddSeconds(-1),now,reason)]:affected.Select(x=>new UsageGap(x.From,x.To,reason)).ToArray();
            await MergeCoverageAsync(identity,[],gaps,null,now,ct);
            db.Add(new GatewayNodeEvent{GatewayNodeId=identity.NodeId,EventType="usage.source.fault",Message="凭证统计来源不完整",CreatedAt=now,Detail=JsonSerializer.Serialize(new{reason})});
            await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
        }
        catch(Exception error){logger.LogWarning("Credential usage source fault persistence failed: {Type}",error.GetType().Name);}
    }
    private static DateTimeOffset Latest(DateTimeOffset? before,DateTimeOffset value)=>before is {} previous&&previous>value?previous:value;
    private static DateTimeOffset Hour(DateTimeOffset value){var utc=value.ToUniversalTime();return new(utc.Year,utc.Month,utc.Day,utc.Hour,0,0,TimeSpan.Zero);}
    private async Task MergeCoverageAsync(NodeIdentity identity,IReadOnlyList<UsageInterval> intervals,IReadOnlyList<UsageGap> gaps,DateTimeOffset? through,DateTimeOffset now,CancellationToken ct)
    {
        var hours=new SortedSet<DateTimeOffset>();
        foreach(var (from,to) in intervals.Select(x=>(x.From,x.To)).Concat(gaps.Select(x=>(x.From,x.To))))
            for(var hour=Hour(from);hour<to;hour=hour.AddHours(1))hours.Add(hour);
        foreach(var hour in hours)
        {
            var end=hour.AddHours(1);
            var row=await db.Set<CredentialUsageCoverage>().FromSqlInterpolated($"SELECT * FROM credential_usage_coverage WHERE environment_id={identity.EnvironmentId} AND node_id={identity.NodeId} AND hour_start={hour} FOR UPDATE").SingleOrDefaultAsync(ct);
            if(row is null){row=new(){EnvironmentId=identity.EnvironmentId,NodeId=identity.NodeId,HourStart=hour};db.Add(row);}
            var existingIntervals=JsonSerializer.Deserialize<UsageInterval[]>(row.Intervals,UsageProtocolJson.Options)??[];
            var existingGaps=JsonSerializer.Deserialize<UsageGap[]>(row.Gaps,UsageProtocolJson.Options)??[];
            static DateTimeOffset Max(DateTimeOffset a,DateTimeOffset b)=>a>b?a:b;
            static DateTimeOffset Min(DateTimeOffset a,DateTimeOffset b)=>a<b?a:b;
            var mergedIntervals=MergeIntervals(existingIntervals.Concat(intervals.Where(x=>x.From<end&&x.To>hour).Select(x=>new UsageInterval(Max(hour,x.From),Min(end,x.To)))));
            var mergedGaps=existingGaps.Concat(gaps.Where(x=>x.From<end&&x.To>hour).Select(x=>new UsageGap(Max(hour,x.From),Min(end,x.To),x.Reason)))
                .GroupBy(x=>x.Reason,StringComparer.Ordinal).SelectMany(g=>MergeIntervals(g.Select(x=>new UsageInterval(x.From,x.To))).Select(x=>new UsageGap(x.From,x.To,g.Key))).OrderBy(x=>x.From).ThenBy(x=>x.Reason,StringComparer.Ordinal).ToArray();
            var intervalJson=JsonSerializer.Serialize(mergedIntervals,UsageProtocolJson.Options);var gapJson=JsonSerializer.Serialize(mergedGaps,UsageProtocolJson.Options);
            // Leave margin for PostgreSQL jsonb whitespace; never drop a loss marker to make the control record fit.
            if(Encoding.UTF8.GetByteCount(intervalJson)+Encoding.UTF8.GetByteCount(gapJson)>512*1024)
            {intervalJson="[]";mergedGaps=[new(hour,end,"GapSummaryLimit")];gapJson=JsonSerializer.Serialize(mergedGaps,UsageProtocolJson.Options);}
            row.Intervals=intervalJson;row.Gaps=gapJson;row.State=mergedGaps.Any(x=>x.Reason=="GapSummaryLimit")?"Unknown":"Partial";
            if(through is {} value){row.DurableThrough=Latest(row.DurableThrough,value);row.AcceptedThrough=Latest(row.AcceptedThrough,value);}row.UpdatedAt=now;
        }
    }
    private static UsageInterval[] MergeIntervals(IEnumerable<UsageInterval> values)
    {
        var merged=new List<UsageInterval>();foreach(var value in values.OrderBy(x=>x.From).ThenBy(x=>x.To))
        {
            if(value.From>=value.To)continue;
            if(merged.Count>0&&value.From<=merged[^1].To){if(value.To>merged[^1].To)merged[^1]=merged[^1] with{To=value.To};}
            else merged.Add(value);
        }
        return merged.ToArray();
    }
}

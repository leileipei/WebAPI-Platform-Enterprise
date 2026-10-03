using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Runtime;
namespace WebApi.Infrastructure.Messaging;
public sealed record DeploymentEvent(Guid EventId,Guid EnvironmentId,long DeploymentSequence,Guid ReleaseId,long TargetVersion);
public sealed class OutboxDispatcher(WebApiDbContext db,RedisSnapshotStore store,Func<Task>? afterWrite=null)
{
    public async Task<bool> DispatchNextAsync(CancellationToken ct=default)
    {
        Guid id;int attempt;DeploymentEvent deployment;
        await using(var tx=await db.Database.BeginTransactionAsync(ct))
        {
            var now=DateTimeOffset.UtcNow;var message=await db.Set<OutboxMessage>().FromSqlInterpolated($"SELECT * FROM outbox_messages WHERE processed_at IS NULL AND (lease_until IS NULL OR lease_until < {now}) ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(ct);
            if(message is null) {await tx.CommitAsync(ct);return false;}
            message.Attempts++;message.LeaseUntil=now.AddSeconds(30);id=message.Id;attempt=message.Attempts;deployment=JsonSerializer.Deserialize<DeploymentEvent>(message.Payload,CanonicalJson.Options)!;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        try
        {
            var desired=await ReadCommittedAsync(deployment,ct);await store.PutAsync(deployment.EnvironmentId,desired.Envelope,desired.Payload,ct);
            if(afterWrite is not null) await afterWrite();
            await db.Set<OutboxMessage>().Where(m=>m.Id==id&&m.Attempts==attempt&&m.ProcessedAt==null).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.ProcessedAt,DateTimeOffset.UtcNow).SetProperty(m=>m.LeaseUntil,(DateTimeOffset?)null),ct);return true;
        }
        catch(Exception e) when(e is RedisException or TimeoutException)
        {
            await db.Set<OutboxMessage>().Where(m=>m.Id==id&&m.Attempts==attempt&&m.ProcessedAt==null).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.LeaseUntil,(DateTimeOffset?)null),ct);return false;
        }
    }
    private async Task<DesiredConfigResponse> ReadCommittedAsync(DeploymentEvent e,CancellationToken ct)
    {
        var row=await (from r in db.Set<ReleaseRecord>().AsNoTracking() join v in db.Set<GatewayConfigVersion>().AsNoTracking() on new {r.EnvironmentId,VersionNo=r.ToConfigVersion} equals new {v.EnvironmentId,v.VersionNo} join s in db.Set<GatewayConfigSnapshot>().AsNoTracking() on v.Id equals s.ConfigVersionId where r.Id==e.ReleaseId&&r.EnvironmentId==e.EnvironmentId&&r.DeploymentSequence==e.DeploymentSequence&&v.VersionNo==e.TargetVersion select new {v.SnapshotHash,s.PayloadBytes,s.SizeBytes}).SingleAsync(ct);
        var envelope=new SnapshotEnvelope(e.ReleaseId,e.DeploymentSequence,e.TargetVersion,row.SnapshotHash!,row.SizeBytes);RedisSnapshotStore.Validate(e.EnvironmentId,envelope,row.PayloadBytes);return new(envelope,row.PayloadBytes);
    }
    public async Task ReconcileDesiredAsync(CancellationToken ct=default)
    {
        var environments=await db.Set<EnvironmentRecord>().AsNoTracking().Where(e=>e.DesiredConfigVersion!=null&&e.DeploymentSequence>0).ToArrayAsync(ct);
        foreach(var env in environments)
        {var release=await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.EnvironmentId==env.Id&&r.DeploymentSequence==env.DeploymentSequence&&r.ToConfigVersion==env.DesiredConfigVersion,ct);var e=new DeploymentEvent(Guid.Empty,env.Id,env.DeploymentSequence,release.Id,release.ToConfigVersion);var desired=await ReadCommittedAsync(e,ct);await store.PutAsync(env.Id,desired.Envelope,desired.Payload,ct);}
    }
}

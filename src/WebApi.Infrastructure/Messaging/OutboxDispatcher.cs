using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Runtime;
namespace WebApi.Infrastructure.Messaging;
public sealed record DeploymentEvent(Guid EventId,Guid EnvironmentId,long DeploymentSequence,Guid ReleaseId,long TargetVersion);
public sealed class OutboxDispatcher(WebApiDbContext db,RedisSnapshotStore store,Func<Task>? afterWrite=null,ILogger<OutboxDispatcher>? log=null)
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
            var published = await PublishCurrentAsync(deployment.EnvironmentId, deployment, ct);
            if(published && afterWrite is not null) await afterWrite();
            await db.Set<OutboxMessage>().Where(m=>m.Id==id&&m.Attempts==attempt&&m.ProcessedAt==null).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.ProcessedAt,DateTimeOffset.UtcNow).SetProperty(m=>m.LeaseUntil,(DateTimeOffset?)null),ct);return true;
        }
        catch(Exception e) when(e is RedisException or TimeoutException)
        {
            await db.Set<OutboxMessage>().Where(m=>m.Id==id&&m.Attempts==attempt&&m.ProcessedAt==null).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.LeaseUntil,(DateTimeOffset?)null),ct);return false;
        }
    }

    private async Task<bool> PublishCurrentAsync(Guid environmentId, DeploymentEvent? queued, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock_shared(8901202)", ct);
        var pointer = await RuntimeEnvironmentAccess.ActiveEnvironments(db).Where(env => env.Id == environmentId)
            .Select(env => new { env.DesiredConfigVersion, env.DeploymentSequence }).SingleOrDefaultAsync(ct);
        if (pointer is null || pointer.DesiredConfigVersion is not long version || pointer.DeploymentSequence <= 0 ||
            queued is not null && (queued.TargetVersion != version || queued.DeploymentSequence != pointer.DeploymentSequence))
            return false;

        if (queued is null)
        {
            var releaseId = await db.Set<ReleaseRecord>().AsNoTracking()
                .Where(release => release.EnvironmentId == environmentId && release.DeploymentSequence == pointer.DeploymentSequence &&
                    release.ToConfigVersion == version).Select(release => release.Id).SingleAsync(ct);
            queued = new(Guid.Empty, environmentId, pointer.DeploymentSequence, releaseId, version);
        }

        var desired = await ReadCommittedAsync(queued, ct);
        ct.ThrowIfCancellationRequested();
        // Keep the archive transition ordered behind the Redis attempt through the client response or timeout.
        await store.PutAsync(environmentId, desired.Envelope, desired.Payload, CancellationToken.None);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task<DesiredConfigResponse> ReadCommittedAsync(DeploymentEvent e,CancellationToken ct)
    {
        var row=await (from r in db.Set<ReleaseRecord>().AsNoTracking() join v in db.Set<GatewayConfigVersion>().AsNoTracking() on new {r.EnvironmentId,VersionNo=r.ToConfigVersion} equals new {v.EnvironmentId,v.VersionNo} join s in db.Set<GatewayConfigSnapshot>().AsNoTracking() on v.Id equals s.ConfigVersionId where r.Id==e.ReleaseId&&r.EnvironmentId==e.EnvironmentId&&r.DeploymentSequence==e.DeploymentSequence&&v.VersionNo==e.TargetVersion select new {v.SnapshotHash,s.PayloadBytes,s.SizeBytes}).SingleAsync(ct);
        var envelope=new SnapshotEnvelope(e.ReleaseId,e.DeploymentSequence,e.TargetVersion,row.SnapshotHash!,row.SizeBytes);RedisSnapshotStore.Validate(e.EnvironmentId,envelope,row.PayloadBytes);return new(envelope,row.PayloadBytes);
    }
    public async Task ReconcileDesiredAsync(CancellationToken ct=default)
    {
        var environmentIds=await db.Set<EnvironmentRecord>().AsNoTracking().Where(e=>e.Status=="Active"&&e.DesiredConfigVersion!=null&&e.DeploymentSequence>0)
            .Select(env=>env.Id).ToArrayAsync(ct);
        foreach(var environmentId in environmentIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PublishCurrentAsync(environmentId, null, ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
            catch(Exception error)
            {
                // Keep invalid snapshots rejected without starving unrelated environments.
                log?.LogError("Desired reconciliation failed for {EnvironmentId}: {ErrorType}",environmentId,error.GetType().Name);
            }
        }
    }
}

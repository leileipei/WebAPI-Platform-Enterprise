using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed record SecurityAckRevalidationTicket(Guid Nonce, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

// Not yet exposed by HTTP. The caller must authenticate the node first, and
// the future issuer must include this new nonce in an independently signed lease.
public sealed class SecurityAckRevalidationChallengeService(WebApiDbContext db, NodeIdentityService identities, SecurityLedgerReader reader)
{
    public async Task<SecurityAckRevalidationTicket> CreateAsync(NodeIdentity identity, Guid instance, Guid operation, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Security revalidation requires its own clean transaction.");
        db.ChangeTracker.Clear();
        if (instance == Guid.Empty || operation == Guid.Empty) throw Conflict();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)", ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={identity.EnvironmentId} FOR NO KEY UPDATE", ct);
        var node = await identities.RevalidateAsync(identity, instance, ct);
        var authority = await reader.ReadCompleteLockedAsync(identity.EnvironmentId, ct);
        if (!SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(node, authority.MinimumSnapshotSchema)) throw Conflict();
        var target = await db.Set<SecurityOperationTarget>().AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == operation &&
            x.EnvironmentId == identity.EnvironmentId && x.NodeId == identity.NodeId && x.InstanceGuid == instance, ct);
        if (target is null || !target.ProtocolSupportedAtFreeze || !authority.Events.Any(x => x.OperationId == operation) ||
            !await db.Set<SecurityAckRecord>().AsNoTracking().AnyAsync(x => x.OperationId == operation && x.EnvironmentId == identity.EnvironmentId && x.NodeId == identity.NodeId && x.InstanceGuid == instance, ct) ||
            await db.Set<SecurityAckVerification>().AsNoTracking().AnyAsync(x => x.OperationId == operation && x.EnvironmentId == identity.EnvironmentId && x.NodeId == identity.NodeId && x.InstanceGuid == instance, ct)) throw Conflict();
        var now = await SecurityDatabaseClock.ObserveAsync(db,ct);
        if (await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db, now, ct) ||
            await db.Set<SecurityAckRevalidationChallenge>().AsNoTracking().AnyAsync(x => x.OperationId == operation && x.EnvironmentId == identity.EnvironmentId &&
                x.NodeId == identity.NodeId && x.InstanceGuid == instance && x.CreatedAt > now, ct)) throw Conflict();
        var existing = await db.Set<SecurityAckRevalidationChallenge>().AsNoTracking().Where(x => x.OperationId == operation && x.EnvironmentId == identity.EnvironmentId &&
            x.NodeId == identity.NodeId && x.InstanceGuid == instance && x.CreatedAt <= now && x.ExpiresAt > now).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var beforeWrite = await SecurityDatabaseClock.ObserveAsync(db, ct);
        if (beforeWrite < now || await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db, beforeWrite, ct)) throw Conflict();
        if (existing is not null && existing.ExpiresAt <= beforeWrite)
        {
            using var clockBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tx.CommitAsync(clockBudget.Token);
            throw Conflict();
        }
        if (existing is null)
        {
            existing = new SecurityAckRevalidationChallenge { Nonce = Guid.NewGuid(), OperationId = operation, EnvironmentId = identity.EnvironmentId,
                NodeId = identity.NodeId, InstanceGuid = instance, CreatedAt = beforeWrite, ExpiresAt = beforeWrite.AddSeconds(30) };
            db.Add(existing);
            await db.SaveChangesAsync(ct);
        }
        var beforeCommit = await SecurityDatabaseClock.ObserveAsync(db, ct);
        if (beforeCommit < beforeWrite || await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db, beforeCommit, ct)) throw Conflict();
        // The historical observation query may itself take long enough for
        // the challenge to expire. Sample again after that query.
        var finalSample = await SecurityDatabaseClock.ObserveAsync(db, ct);
        if (finalSample < beforeCommit) throw Conflict();
        if (existing.ExpiresAt <= finalSample)
        {
            using var clockBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tx.CommitAsync(clockBudget.Token);
            throw Conflict();
        }
        await tx.CommitAsync(ct);
        // Freshness is established at finalSample; callers must still verify
        // the deadline when using the returned ticket.
        return new(existing.Nonce, existing.CreatedAt, existing.ExpiresAt);
    }

    private static ApiException Conflict() => new(409, "security_revalidation_conflict", "当前节点或确认记录不允许重新验证。");
}

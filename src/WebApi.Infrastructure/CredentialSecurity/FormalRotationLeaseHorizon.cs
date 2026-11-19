using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Domain.Applications;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed record FormalRotationLeaseScope(GovernanceJournalId Journal, GovernanceJournalPosition Position,
    Guid OrganizationId, Guid ProjectId, Guid EnvironmentId, Guid SecurityGeneration, long Epoch, string HeadHash);

public sealed record FormalRotationLeaseHorizonEvidence([property: JsonRequired] FormalRotationLeaseScope Scope,
    [property: JsonRequired] string ProofHash, [property: JsonRequired] bool Complete, [property: JsonRequired] bool HasPendingWrite,
    [property: JsonRequired] DateTimeOffset Lower, [property: JsonRequired] DateTimeOffset Upper,
    [property: JsonRequired] DateTimeOffset ValidUntil, [property: JsonRequired] DateTimeOffset? LatestAllowExpiry);

public interface IFormalRotationLeaseHorizonWitness
{
    // Return only independently authenticated and current evidence, without I/O under the local writer fence.
    // The ordered reserve/prepare authority must attest all potentially live Allow reservations, including lost
    // responses and replaced instances. Null expiry is reserved for an independently verified empty horizon.
    // Any future prepare must recheck this horizon atomically at that authority with the journal position.
    FormalRotationLeaseHorizonEvidence? GetVerifiedHorizon(FormalRotationLeaseScope scope);
}

internal static class FormalRotationLeaseHorizon
{
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumTimeWidth = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaximumTimeValidity = TimeSpan.FromSeconds(30);

    // The caller obtains the exact journal and confirmed position from the commit store's locked read,
    // requiring no local pending operation. The environment ledger is verified below at that same fence.
    internal static async Task<FormalRotationLeaseHorizonEvidence> RequireAsync(WebApiDbContext db, FormalRotationLeaseScope scope,
        IFormalRotationLeaseHorizonWitness? witness, DateTimeOffset databaseNow, DateTimeOffset hardDeadline, CancellationToken ct)
    {
        if (witness is null || !ValidScope(scope) || !UtcInstant(databaseNow) || db.Database.CurrentTransaction is null ||
            !await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_locks
                    WHERE pid = pg_catalog.pg_backend_pid() AND locktype = 'advisory' AND mode = 'ExclusiveLock'
                        AND classid = 0 AND objid = 8901202 AND objsubid = 1 AND granted
                ) AS "Value"
                """).SingleAsync(ct)) throw EvidenceUnavailable();

        SecurityLedgerMaterial ledger;
        try { ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(scope.EnvironmentId, ct); }
        catch (InvalidOperationException) { throw EvidenceUnavailable(); }
        if (ledger.OrganizationId != scope.OrganizationId || ledger.ProjectId != scope.ProjectId ||
            ledger.SecurityGeneration != scope.SecurityGeneration || ledger.Epoch != scope.Epoch || ledger.HeadHash != scope.HeadHash)
            throw EvidenceUnavailable();

        var known = await ReadKnownHorizonAsync(db, scope, databaseNow, ct);
        ct.ThrowIfCancellationRequested();
        var evidence = witness.GetVerifiedHorizon(scope);
        var prepareBudget = BoundedWitnessHttpClient.RequestTimeout;
        if (evidence is null || evidence.Scope != scope || !evidence.Complete || evidence.HasPendingWrite ||
            !SecuritySignedEncoding.HexHash(evidence.ProofHash) || !UtcInstant(evidence.Lower) || !UtcInstant(evidence.Upper) ||
            !UtcInstant(evidence.ValidUntil) || evidence.Lower > databaseNow || databaseNow > evidence.Upper ||
            evidence.Upper - evidence.Lower > MaximumTimeWidth || evidence.ValidUntil - evidence.Lower > MaximumTimeValidity ||
            evidence.ValidUntil - evidence.Upper <= prepareBudget ||
            (evidence.LatestAllowExpiry is DateTimeOffset latest && !UtcInstant(latest))) throw EvidenceUnavailable();

        if (known is DateTimeOffset expiry && (!UtcInstant(expiry) || evidence.Lower - expiry < MaximumClockSkew &&
            (evidence.LatestAllowExpiry is not DateTimeOffset reported || reported < expiry))) throw EvidenceUnavailable();

        if (!RotationLeaseDeadlinePolicy.CanPrepare(hardDeadline, evidence.Upper, MaximumClockSkew, prepareBudget,
            evidence.LatestAllowExpiry is DateTimeOffset allowExpiry ? [allowExpiry] : []))
            throw new ApiException(409, "formal_rotation_deadline_unavailable", "拟定截止时间无法覆盖现有允许租约或准备时限。");
        return evidence;
    }

    private static async Task<DateTimeOffset?> ReadKnownHorizonAsync(WebApiDbContext db, FormalRotationLeaseScope scope,
        DateTimeOffset databaseNow, CancellationToken ct)
    {
        // Read all generations and instances for this environment, including disabled and replaced instances.
        // A mutable horizon only vetoes a shorter witness report, while durable issuance is the authoritative local history.
        var issuances = db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().Where(row => row.EnvironmentId == scope.EnvironmentId);
        var tracked = db.Set<GatewaySecurityLease>().AsNoTracking().Where(row => row.EnvironmentId == scope.EnvironmentId);
        if (await issuances.AnyAsync(row => row.OrganizationId != scope.OrganizationId || row.ProjectId != scope.ProjectId ||
                row.IssuedAt > databaseNow || row.SecurityGeneration == scope.SecurityGeneration && row.Epoch > scope.Epoch, ct) ||
            await tracked.AnyAsync(row => row.OrganizationId != scope.OrganizationId || row.ProjectId != scope.ProjectId ||
                row.IssuedAt > databaseNow || row.SecurityGeneration == scope.SecurityGeneration && row.Epoch > scope.Epoch, ct))
            throw EvidenceUnavailable();

        var issued = await issuances.MaxAsync(row => (DateTimeOffset?)row.FreshUntil, ct);
        var horizon = await tracked.MaxAsync(row => (DateTimeOffset?)row.LastIssuedLeaseUntil, ct);
        return issued is null || horizon > issued ? horizon : issued;
    }

    private static bool ValidScope(FormalRotationLeaseScope? scope) => scope?.Journal is { } journal &&
        journal.AuthorityId != Guid.Empty && journal.Generation != Guid.Empty && GovernanceJournalProtocol.ValidPosition(scope.Position) &&
        scope.OrganizationId != Guid.Empty && scope.ProjectId != Guid.Empty && scope.EnvironmentId != Guid.Empty &&
        scope.SecurityGeneration != Guid.Empty && scope.Epoch >= 0 && SecuritySignedEncoding.HexHash(scope.HeadHash);

    private static bool UtcInstant(DateTimeOffset time) => time.Offset == TimeSpan.Zero &&
        time > DateTimeOffset.MinValue && time < DateTimeOffset.MaxValue;

    private static ApiException EvidenceUnavailable() => new(503, "formal_rotation_evidence_unavailable", "完整的独立租约期限证明尚不可用。");
}

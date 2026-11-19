using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.Applications;
using WebApi.Domain.CredentialSecurity;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed class SecurityLeaseIssuer(WebApiDbContext db, NodeIdentityService identities, SecurityLedgerReader reader,
    SecuritySigningSettings signing, IGovernanceRecoveryContinuity? continuity, IGovernanceLeaseReservationWitness? reservations,
    SecurityLeaseEvidenceSources? evidence, TimeProvider? clock)
{
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IssuanceBudget = TimeSpan.FromSeconds(15);
    private static readonly IndependentEvidenceLimits EvidenceLimits = new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30),
        BoundedWitnessHttpClient.RequestTimeout);
    private const int MaximumChallenges = 4096;

    public SecurityLeaseIssuer(WebApiDbContext db, NodeIdentityService identities, SecurityLedgerReader reader, SecuritySigningSettings signing)
        : this(db, identities, reader, signing, null, null, null, null) { }

    public async Task<SecurityNodeMaterial> IssueAsync(NodeIdentity identity, Guid instanceId, CancellationToken ct = default)
    {
        if (continuity is null || reservations is null || evidence is null || clock is null ||
            evidence.Trust is null || evidence.Trust.TimePublicKey == evidence.Trust.HistoryPublicKey) throw Unavailable();
        if (instanceId == Guid.Empty) throw NodeIdentityService.Denied();
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Security lease issuance requires its own clean transaction.");

        db.ChangeTracker.Clear();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(IssuanceBudget);
        var token = budget.Token;
        try
        {
            var (signer, keys, checkpoint, lease, time) = await PersistAsync(identity, instanceId, continuity, reservations, evidence, clock, token);
            // Cryptographic delivery starts only after durable local commit and
            // the exact reservation ordered before any competing journal prepare.
            token.ThrowIfCancellationRequested();
            time.RequireFresh(lease.FreshUntil, DeliveryBudget);
            var material = new SecurityNodeMaterial(signer.SignCheckpoint(checkpoint), signer.SignNodeLease(lease));
            SecurityCheckpointVerifier.VerifyCheckpoint(new(material.Checkpoint), keys, identity.EnvironmentId);
            SecurityCheckpointVerifier.VerifyNodeLease(new(material.Lease), keys, identity.EnvironmentId, identity.NodeId, instanceId);
            token.ThrowIfCancellationRequested();
            time.RequireFresh(lease.FreshUntil, DeliveryBudget);
            return material;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unavailable(); }
        catch (Exception error) when (error is InvalidDataException or IOException or CryptographicException or ArgumentException or
            InvalidOperationException or NpgsqlException or DbUpdateException or TimeoutException or OverflowException)
        {
            throw Unavailable();
        }
        finally { db.ChangeTracker.Clear(); }
    }

    private async Task<(SecurityCheckpointSigner, TrustedSigningKeys, SecurityCheckpointPayload, SecurityNodeLeasePayload, IssuanceTime)> PersistAsync(
        NodeIdentity identity, Guid instanceId, IGovernanceRecoveryContinuity continuity, IGovernanceLeaseReservationWitness reservations,
        SecurityLeaseEvidenceSources evidence, TimeProvider clock, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)", token);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL search_path = pg_catalog, public, pg_temp", token);
        var environment = await db.Set<EnvironmentRecord>().FromSqlInterpolated(
            $"SELECT * FROM public.environments WHERE id={identity.EnvironmentId} FOR NO KEY UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(token) ?? throw NodeIdentityService.Denied();
        var node = await identities.RevalidateAsync(identity, instanceId, token);
        var authority = await reader.ReadCompleteLockedAsync(environment.Id, token);
        var confirmed = await continuity.RequireAsync(db, environment.Id, token);
        if (!ReferenceEquals(db.Database.CurrentTransaction, tx) || db.ChangeTracker.HasChanges()) throw Unavailable();
        RequireExactLedger(confirmed, authority, environment);

        var leaseSettings = await db.Set<EnvironmentSecurityState>().AsNoTracking().SingleAsync(s => s.EnvironmentId == environment.Id, token);
        if (leaseSettings.LeaseSeconds is < 5 or > 30 || leaseSettings.MaximumClockSkewSeconds is < 0 or > 2)
            throw Unavailable();
        var requires24 = await GlobalCredentialPublicationGuard.RequiresGovernance24Async(db, environment.Id, token);
        if (!SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(node, requires24 ? "2.4" : authority.MinimumSnapshotSchema))
            throw new ApiException(409, "gateway_security_capability_required", "当前节点实例未声明所需的快照及安全协议能力。");
        await RequireLiveConfigurationAsync(environment, node, authority, requires24, token);

        var hardDeadline = await ReadOutstandingDeadlineAsync(authority, confirmed, token);
        var challenges = await ReadChallengesAsync(authority, node.Id, instanceId, token);
        var signer = signing.LoadSigner();
        var keys = signing.LoadTrustedKeys();
        var publicKey = signer.ExportPublicKey();
        if (!SecuritySignedEncoding.KeyId(signing.KeyId) || !keys.SubjectPublicKeys.TryGetValue(signing.KeyId!, out var pinnedKey) ||
            publicKey != pinnedKey || publicKey == evidence.Trust.TimePublicKey || publicKey == evidence.Trust.HistoryPublicKey)
            throw Unavailable();

        var scope = new EvidenceScope("control-plane-issuance", environment.Id, node.Id, instanceId, authority.SecurityGeneration, authority.Epoch, authority.HeadHash);
        var frequency = clock.TimestampFrequency;
        var started = clock.GetTimestamp();
        var inspection = await IndependentEvidenceInspector.InspectAsync(scope, clock, evidence.Time, evidence.History, evidence.Trust, EvidenceLimits, token);
        var time = new IssuanceTime(clock, frequency, started, inspection);
        var databaseNow = await SecurityDatabaseClock.ObserveAsync(db, token);
        var nowUpper = time.Observe(databaseNow);
        if (await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db, databaseNow, token)) throw Unavailable();

        // The external sample bounds issuedAt. PostgreSQL time and the expiry
        // watermark can only impose an earlier value or reject issuance.
        var issuedAt = FloorToMicroseconds(Earlier(databaseNow, inspection.LowerAtSource.AddSeconds(leaseSettings.MaximumClockSkewSeconds)));
        var cap = RotationLeaseDeadlinePolicy.CapExpiry(hardDeadline ?? DateTimeOffset.MaxValue, nowUpper,
            issuedAt.AddSeconds(leaseSettings.LeaseSeconds), inspection.ValidUntil, MaximumClockSkew, DeliveryBudget) ?? throw Unavailable();
        var freshUntil = FloorToMicroseconds(cap);
        var checkpoint = CreateCheckpoint(authority, issuedAt, freshUntil, signing.KeyId!);
        var lease = new SecurityNodeLeasePayload(Guid.NewGuid(), SecuritySignedEncoding.Protocol, environment.Id, authority.SecurityGeneration,
            authority.Epoch, SecuritySignedEncoding.CheckpointHash(checkpoint), node.Id, instanceId, SecuritySignedEncoding.Audience,
            issuedAt, freshUntil, signing.KeyId!, leaseSettings.LeaseSeconds, leaseSettings.MaximumClockSkewSeconds, true, challenges);
        var horizon = await ReadPriorHorizonAsync(authority, lease, token);
        var fact = SecurityLeaseIssuanceEvidence.Create(lease, authority.OrganizationId, authority.ProjectId);
        var requested = new GovernanceLeaseReservation(fact.LeaseId, fact.OrganizationId, fact.ProjectId, fact.EnvironmentId, fact.NodeId,
            fact.InstanceId, fact.SecurityGeneration, fact.Epoch, fact.SigningKeyId, fact.CheckpointHash, fact.PayloadHash, fact.IssuedAt, fact.FreshUntil);

        var reserveBudget = time.RequireFresh(freshUntil, DeliveryBudget);
        using (var reservationBudget = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            // Short leases can use a shorter witness call while retaining
            // the required delivery time and the existing transport limit.
            reservationBudget.CancelAfter(reserveBudget < BoundedWitnessHttpClient.RequestTimeout
                ? reserveBudget : BoundedWitnessHttpClient.RequestTimeout);
            var reserved = await reservations.ReserveAllowLeaseAsync(confirmed.Journal, confirmed.Position, requested, reservationBudget.Token)
                .WaitAsync(reservationBudget.Token);
            if (reserved is null || reserved.Journal != confirmed.Journal || reserved.Confirmed != confirmed.Position ||
                reserved.Reservation != requested) throw Unavailable();
        }
        // No ambiguous reservation reaches this point. A lost acknowledgment
        // or local failure leaves the independently ordered expiry in the
        // witness horizon even if nothing has been committed or signed here.
        time.RequireFresh(freshUntil, DeliveryBudget);
        StageIssuance(horizon, fact, lease.LeaseSeconds);
        await db.SaveChangesAsync(token);
        await identities.RevalidateAsync(identity, instanceId, token);
        var beforeCommit = await SecurityDatabaseClock.ObserveAsync(db, token);
        if (beforeCommit < databaseNow || freshUntil - time.Observe(beforeCommit) <= DeliveryBudget ||
            await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db, beforeCommit, token)) throw Unavailable();
        time.RequireFresh(freshUntil, DeliveryBudget);
        await tx.CommitAsync(token);
        return (signer, keys, checkpoint, lease, time);
    }

    private static void RequireExactLedger(GovernanceContinuity? confirmed, SecurityLedgerMaterial local, EnvironmentRecord environment)
    {
        if (confirmed is not { Ledger: { } verified, Journal: { } journal } || journal.AuthorityId == Guid.Empty || journal.Generation == Guid.Empty ||
            !GovernanceJournalProtocol.ValidPosition(confirmed.Position) || local.ProjectId != environment.ProjectId ||
            local.EnvironmentId != environment.Id || environment.RequireEnvironmentCredential != local.RequireEnvironmentCredentials ||
            verified.Events is null || verified.Revocations is null ||
            (local.EnvironmentId, local.OrganizationId, local.ProjectId, local.SecurityGeneration, local.Epoch, local.HeadHash,
                local.RequireEnvironmentCredentials, local.MinimumSnapshotSchema, local.InitialRequireEnvironmentCredentials) !=
            (verified.EnvironmentId, verified.OrganizationId, verified.ProjectId, verified.SecurityGeneration, verified.Epoch, verified.HeadHash,
                verified.RequireEnvironmentCredentials, verified.MinimumSnapshotSchema, verified.InitialRequireEnvironmentCredentials))
            throw Unavailable();
        SecurityLedgerReader.Validate(verified);
        if (!local.Events.Select(item => item.EventHash).SequenceEqual(verified.Events.Select(item => item.EventHash)) ||
            !local.Revocations.Select(RevocationIdentity).SequenceEqual(verified.Revocations.Select(RevocationIdentity)))
            throw Unavailable();
    }

    private static (Guid Credential, Guid Application, Guid Operation, long Epoch, DateTimeOffset At) RevocationIdentity(CredentialRevocation item) =>
        (item.CredentialId, item.ApplicationId, item.OperationId, item.Epoch, item.RevokedAt);

    private async Task RequireLiveConfigurationAsync(EnvironmentRecord environment, GatewayNode node, SecurityLedgerMaterial ledger,
        bool requires24, CancellationToken ct)
    {
        if (environment.DesiredConfigVersion is not long version)
        {
            // An enrolled instance can obtain the checkpoint needed to activate
            // its first snapshot. An orphaned running version is not bootstrap.
            if (node.CurrentConfigVersion != 0 || node.CurrentDeploymentSequence != 0) throw Unavailable();
            return;
        }

        if (version <= 0 || environment.DeploymentSequence <= 0 ||
            !await db.Set<ReleaseRecord>().AsNoTracking().AnyAsync(item => item.EnvironmentId == environment.Id &&
                item.ToConfigVersion == version && item.DeploymentSequence == environment.DeploymentSequence, ct))
            throw Unavailable();
        try
        {
            var artifact = await new HistoricalSnapshotService(db, new ScopeResolver(db)).ReadAsync(environment.Id, version, ct);
            var snapshot = artifact.Snapshot;
            if (!SnapshotSchemaCapabilities.Read(node).Contains(snapshot.SchemaVersion) ||
                !SnapshotSchemaRules.AtLeast(snapshot.SchemaVersion, ledger.MinimumSnapshotSchema) ||
                snapshot.SchemaVersion != "2.4" && (requires24 || ledger.Revocations.Count > 0) ||
                (snapshot.RequiredSecurityGeneration is null && ledger.MinimumSnapshotSchema is ("2.3" or "2.4")) ||
                snapshot.RequiredSecurityGeneration is not null &&
                    (snapshot.RequiredSecurityGeneration != ledger.SecurityGeneration || snapshot.RequiredSecurityEpoch > ledger.Epoch ||
                        snapshot.MinimumSnapshotSchema != ledger.MinimumSnapshotSchema || snapshot.RequireEnvironmentCredentials != ledger.RequireEnvironmentCredentials))
                throw Unavailable();
        }
        catch (ApiException error) when (error.Status != 503) { throw Unavailable(); }
    }

    private async Task<DateTimeOffset?> ReadOutstandingDeadlineAsync(SecurityLedgerMaterial authority, GovernanceContinuity confirmed,
        CancellationToken ct)
    {
        var revoked = authority.Revocations.Select(item => item.CredentialId).ToArray();
        var aborted = await FormalRotationDecisionMutation.ReadResolvedAbortsAsync(db, confirmed.Journal, confirmed.Position,
            authority.EnvironmentId, ct);
        return await db.Set<FormalCredentialRotation>().AsNoTracking()
            .Where(item => item.EnvironmentId == authority.EnvironmentId && !revoked.Contains(item.CredentialId) && !aborted.Contains(item.Id))
            .MinAsync(item => (DateTimeOffset?)item.HardDeadline, ct);
    }

    private async Task<SecurityAckChallenge[]> ReadChallengesAsync(SecurityLedgerMaterial authority, Guid nodeId, Guid instanceId, CancellationToken ct)
    {
        var targets = await db.Set<SecurityOperationTarget>().AsNoTracking()
            .Where(item => item.EnvironmentId == authority.EnvironmentId && item.NodeId == nodeId && item.InstanceGuid == instanceId &&
                item.SupersededByInstanceGuid == null && item.ProtocolSupportedAtFreeze && item.TargetState == "Pending")
            .OrderBy(item => item.OperationId).Take(MaximumChallenges + 1).ToArrayAsync(ct);
        if (targets.Length > MaximumChallenges) throw Unavailable();
        var byOperation = authority.Events.ToDictionary(item => item.OperationId);
        return targets.Select(target =>
        {
            if (target.ChallengeNonce == Guid.Empty || !byOperation.TryGetValue(target.OperationId, out var item)) throw Unavailable();
            return new SecurityAckChallenge(item.OperationId, target.ChallengeNonce, item.Epoch, item.CredentialId,
                item.EventType == "EnvironmentPolicyChanged" ? item.EventType : null);
        }).ToArray();
    }

    private static SecurityCheckpointPayload CreateCheckpoint(SecurityLedgerMaterial authority,
        DateTimeOffset issuedAt, DateTimeOffset freshUntil, string keyId)
    {
        var byEpoch = authority.Events.ToDictionary(item => item.Epoch);
        var proofs = authority.Revocations.Select(item => new SecurityRevocationProof(item.CredentialId, item.OperationId,
            item.Epoch, item.RevokedAt, byEpoch[item.Epoch].EventHash)).ToArray();
        if (issuedAt <= DateTimeOffset.MinValue || freshUntil <= issuedAt ||
            proofs.Any(item => item.RevokedAt.Offset != TimeSpan.Zero || item.RevokedAt == DateTimeOffset.MinValue ||
                item.RevokedAt - issuedAt > MaximumClockSkew))
            throw Unavailable();
        return new(SecuritySignedEncoding.Protocol, authority.EnvironmentId, authority.SecurityGeneration, authority.Epoch, authority.HeadHash,
            proofs, authority.RequireEnvironmentCredentials, authority.MinimumSnapshotSchema, issuedAt, freshUntil, keyId);
    }

    private async Task<GatewaySecurityLease?> ReadPriorHorizonAsync(SecurityLedgerMaterial authority, SecurityNodeLeasePayload lease, CancellationToken ct)
    {
        var history = db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().Where(item => item.NodeId == lease.NodeId);
        if (await history.AnyAsync(item => item.EnvironmentId != authority.EnvironmentId || item.ProjectId != authority.ProjectId ||
                item.OrganizationId != authority.OrganizationId || item.SecurityGeneration != authority.SecurityGeneration ||
                item.Epoch > authority.Epoch || item.IssuedAt >= lease.IssuedAt, ct)) throw Unavailable();
        var horizon = await db.Set<GatewaySecurityLease>().SingleOrDefaultAsync(
            item => item.NodeId == lease.NodeId && item.InstanceId == lease.InstanceId, ct);
        if (horizon is null) return null;
        if (horizon.EnvironmentId != authority.EnvironmentId || horizon.ProjectId != authority.ProjectId ||
            horizon.OrganizationId != authority.OrganizationId || horizon.SecurityGeneration != authority.SecurityGeneration ||
            horizon.Epoch > authority.Epoch || horizon.IssuedAt >= lease.IssuedAt || horizon.LastIssuedLeaseUntil < horizon.FreshUntil ||
            !await history.AnyAsync(item => item.LeaseId == horizon.LeaseId && item.InstanceId == horizon.InstanceId &&
                item.Epoch == horizon.Epoch && item.CheckpointHash == horizon.CheckpointHash &&
                item.IssuedAt == horizon.IssuedAt && item.FreshUntil == horizon.FreshUntil && item.SigningKeyId == horizon.SigningKeyId, ct))
            throw Unavailable();
        return horizon;
    }

    private void StageIssuance(GatewaySecurityLease? horizon, GatewaySecurityLeaseIssuance fact, int leaseSeconds)
    {
        if (horizon is null)
        {
            horizon = new GatewaySecurityLease { NodeId = fact.NodeId, InstanceId = fact.InstanceId, EnvironmentId = fact.EnvironmentId,
                OrganizationId = fact.OrganizationId, ProjectId = fact.ProjectId, SecurityGeneration = fact.SecurityGeneration };
            db.Add(horizon);
        }
        else horizon.Revision = checked(horizon.Revision + 1);
        horizon.Epoch = fact.Epoch;
        horizon.CheckpointHash = fact.CheckpointHash;
        horizon.LeaseId = fact.LeaseId;
        horizon.IssuedAt = fact.IssuedAt;
        horizon.FreshUntil = fact.FreshUntil;
        if (horizon.LastIssuedLeaseUntil < fact.FreshUntil) horizon.LastIssuedLeaseUntil = fact.FreshUntil;
        horizon.SigningKeyId = fact.SigningKeyId;
        horizon.LeaseSeconds = leaseSeconds;
        db.Add(fact);
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset FloorToMicroseconds(DateTimeOffset utc) => utc.AddTicks(-(utc.Ticks % 10));

    public static ApiException Unavailable() => new(503, "security_evidence_unavailable", "独立安全证明尚不可用。");

    private sealed class IssuanceTime(TimeProvider clock, long frequency, long started, IndependentEvidenceInspection inspection)
    {
        private readonly long start = started;
        private long last = started;

        internal DateTimeOffset Observe(DateTimeOffset? databaseTime = null)
        {
            var wall = clock.GetUtcNow();
            var stamp = clock.GetTimestamp();
            if (clock.TimestampFrequency != frequency || frequency <= 0 || stamp < last) throw Unavailable();
            last = stamp;
            var elapsed = clock.GetElapsedTime(start, stamp);
            if (elapsed < inspection.ProcessingElapsed || elapsed > IssuanceBudget) throw Unavailable();
            var upper = inspection.UpperAtSource.Add(elapsed);
            if (wall.Offset != TimeSpan.Zero || wall - inspection.LowerAtSource < -MaximumClockSkew || wall - upper > MaximumClockSkew)
                throw Unavailable();
            if (databaseTime is DateTimeOffset observed)
            {
                if (observed.Offset != TimeSpan.Zero || observed - inspection.LowerAtSource < -MaximumClockSkew ||
                    observed - upper > MaximumClockSkew) throw Unavailable();
                if (observed > upper) upper = observed;
            }
            return wall > upper ? wall : upper;
        }

        internal TimeSpan RequireFresh(DateTimeOffset until, TimeSpan reserveForDelivery)
        {
            var available = until - Observe() - reserveForDelivery;
            if (until > inspection.ValidUntil || available <= TimeSpan.Zero) throw Unavailable();
            return available;
        }
    }
}

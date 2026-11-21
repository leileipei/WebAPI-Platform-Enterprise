using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

/// <summary>Binds independent observations to local facts read on both sides of the witness requests.</summary>
public sealed class PostgresRevocationReceiptEvidence
{
    private readonly Func<Sources> sources;

    public PostgresRevocationReceiptEvidence(GovernanceJournalId journal, IGovernanceJournalWitness journalWitness,
        IGovernanceEnvironmentHistoryWitness historyWitness, ICredentialRunningIngressWitness ingressWitness,
        ICredentialArchivedIngressWitness? archiveWitness = null, ICredentialRetiredTargetWitness? retiredWitness = null)
        : this(() => new(journal, journalWitness, historyWitness, ingressWitness, archiveWitness, retiredWitness)) { }

    internal PostgresRevocationReceiptEvidence(Func<Sources> sources) => this.sources = sources;

    internal sealed record Sources(GovernanceJournalId Journal, IGovernanceJournalWitness JournalWitness,
        IGovernanceEnvironmentHistoryWitness HistoryWitness, ICredentialRunningIngressWitness IngressWitness,
        ICredentialArchivedIngressWitness? ArchiveWitness, ICredentialRetiredTargetWitness? RetiredWitness = null);

    internal sealed record RetiredCoverage(string ScopeHash, string ProofHash, string InventoryHash, DateTimeOffset ValidUntil);
    internal sealed record Coverage(string ScopeHash, DateTimeOffset ValidUntil, IReadOnlySet<(Guid Node, Guid Instance)> CurrentInstances,
        IReadOnlyDictionary<(Guid Node, Guid Instance), RetiredCoverage> RetiredInstances);
    internal sealed record Observation(IReadOnlyDictionary<Guid, Coverage> Environments);
    internal sealed record EnvironmentEvidence(GovernanceEnvironmentHistoryCheckpoint Checkpoint, Coverage? Ingress);
    internal sealed record LocalEvidence(Sources Witnesses, GovernanceJournalPosition Position, GovernanceOperationConfirmation Confirmation,
        IReadOnlyDictionary<Guid, EnvironmentEvidence> Environments);
    internal sealed record IndependentEvidence(IReadOnlySet<Guid> Environments, long Started);

    internal async Task<LocalEvidence?> ReadLocalAsync(WebApiDbContext db, RevocationOperationDto receipt, Sources? pinned,
        CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction ?? throw new InvalidOperationException("Receipt evidence requires the governance fence.");
        const string savepoint = "revocation_receipt_evidence";
        await tx.CreateSavepointAsync(savepoint, ct);
        try
        {
            var local = await CaptureAsync(db, receipt, pinned ?? sources(), ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
            return local;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && (EvidenceUnavailable(error) || error is NpgsqlException))
        {
            // An unavailable evidence relation or clock can abort a PostgreSQL
            // statement. Restore this transaction before reading diagnostic status.
            await tx.RollbackToSavepointAsync(savepoint, ct);
            await tx.ReleaseSavepointAsync(savepoint, ct);
            return null;
        }
    }

    private static async Task<LocalEvidence?> CaptureAsync(WebApiDbContext db, RevocationOperationDto receipt, Sources witnesses,
        CancellationToken ct)
    {
        var (journal, journalWitness, history, ingress, archive, retired) = witnesses;
        var continuity = new PostgresGovernanceRecoveryContinuity(journal, journalWitness, history);
        var (transaction, position, entries) = await continuity.ReadConfirmedJournalAsync(db, ct);
        var intent = entries.SingleOrDefault(entry => entry.OperationId == receipt.OperationId);
        if (intent is null || ReadEvents(intent, entries) is not { } recorded ||
            recorded.Any(fact => fact.CredentialId != receipt.CredentialId || fact.OccurredAt != receipt.CommittedAt) ||
            !recorded.Select(fact => new RevocationEnvironmentDto(fact.EnvironmentId, fact.SecurityGeneration, fact.Epoch,
                fact.EventHash, CredentialRevocationEvents.CoverageState(fact))).SequenceEqual(receipt.Environments) ||
            !await FormalRotationMutation.VerifyCommittedAsync(db, journal, intent, ct)) return null;

        var confirmation = new GovernanceOperationConfirmation(intent.OperationId, GovernanceJournalProtocol.NextPosition(journal, intent));
        var environments = new Dictionary<Guid, EnvironmentEvidence>();
        foreach (var affected in receipt.Environments)
        {
            var (ledger, checkpoint) = await PostgresGovernanceRecoveryContinuity.ReadEnvironmentCheckpointAsync(db, affected.EnvironmentId, ct);
            Coverage? proof;
            try
            {
                proof = await ObserveIngressAsync(db, affected, ledger, ingress, archive, ct);
                if (proof is not null && retired is not null && affected.CoverageState != "SealedNoIngressAtCommit")
                    proof = proof with { RetiredInstances = await ObserveRetiredAsync(db, receipt, affected, ledger, journal,
                        position, confirmation, FrozenTargets(intent, affected.EnvironmentId), proof, retired, ct) };
            }
            catch (Exception error) when (EvidenceUnavailable(error)) { proof = null; }
            environments.Add(affected.EnvironmentId, new(checkpoint, proof));
        }
        await PostgresGovernanceRecoveryContinuity.RequireFenceAsync(db, transaction, ct);
        return new(witnesses, position, confirmation, environments);
    }

    private static IReadOnlyList<FormalRotationDeadlineTarget> FrozenTargets(GovernanceWriteIntent intent, Guid environment)
    {
        // Read the original scope from the independently confirmed intent. Later
        // local successor targets and mutable horizons cannot rewrite that scope.
        switch (FormalRotationJournalIntent.Kind(intent))
        {
            case null:
                var direct = CredentialRevocationIntent.Read(intent).Environments().Single(x => x.EnvironmentId == environment);
                return (direct.Coverage?.Nodes ?? []).Zip(direct.Challenges).Select(pair => new FormalRotationDeadlineTarget(
                    pair.First.NodeId, pair.First.InstanceId, pair.Second, true, pair.First.LastLeaseUntil)).ToArray();
            case FormalRotationJournalIntent.DeadlineRevocation:
                var deadline = FormalRotationDeadlineIntent.Read(intent);
                return deadline.EnvironmentId == environment ? deadline.Targets : [];
            case FormalRotationJournalIntent.Decision:
                var decision = FormalRotationDecisionIntent.Read(intent);
                return decision.EnvironmentId == environment ? decision.RevocationTargets : [];
            default: return [];
        }
    }

    private static async Task<IReadOnlyDictionary<(Guid Node, Guid Instance), RetiredCoverage>> ObserveRetiredAsync(
        WebApiDbContext db, RevocationOperationDto receipt, RevocationEnvironmentDto affected, SecurityLedgerMaterial ledger,
        GovernanceJournalId journal, GovernanceJournalPosition position, GovernanceOperationConfirmation confirmation,
        IReadOnlyList<FormalRotationDeadlineTarget> frozen, Coverage ingress, ICredentialRetiredTargetWitness witness, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, Guid), RetiredCoverage>();
        if (affected.SecurityGeneration != ledger.SecurityGeneration) return result;
        var targets = await db.Set<SecurityOperationTarget>().AsNoTracking().Where(row =>
            row.OperationId == receipt.OperationId && row.EnvironmentId == affected.EnvironmentId).ToArrayAsync(ct);
        foreach (var original in frozen.Where(target => target.ProtocolSupported && target.InstanceId != Guid.Empty &&
                     !ingress.CurrentInstances.Contains((target.NodeId, target.InstanceId))))
        {
            var target = targets.SingleOrDefault(row => row.NodeId == original.NodeId && row.InstanceGuid == original.InstanceId);
            if (target is null || !target.ProtocolSupportedAtFreeze || target.ChallengeNonce != original.ChallengeNonce ||
                target.CreatedAt != receipt.CommittedAt || original.LastLeaseUntil is DateTimeOffset frozenUntil &&
                    (target.LastLeaseUntil is not DateTimeOffset observedUntil || observedUntil < frozenUntil)) continue;
            var scope = new GovernanceRetiredTargetScope(journal, position, confirmation, receipt.CredentialId,
                ledger.OrganizationId, ledger.ProjectId, affected.EnvironmentId, ledger.SecurityGeneration, ledger.Epoch,
                ledger.HeadHash, affected.Epoch, affected.HeadHash, original.NodeId, original.InstanceId, original.ChallengeNonce,
                original.LastLeaseUntil, ingress.ScopeHash);
            var proof = witness.GetVerifiedCoverage(scope);
            var now = await SecurityDatabaseClock.ObserveAsync(db, ct);
            if (proof is null || !await CredentialRetiredTargetCoverage.CoversKnownLeasesAsync(db, scope, proof, target.LastLeaseUntil, now, ct)) continue;
            result.Add((original.NodeId, original.InstanceId), new(
                GovernanceDeploymentEvidenceProtocol.ScopeHash(GovernanceRetiredTargetProtocol.Purpose, scope), proof.ProofHash,
                CredentialRetiredTargetCoverage.InventoryHash(proof), proof.ValidUntil));
        }
        return result;
    }

    private static SecurityEvent[]? ReadEvents(GovernanceWriteIntent intent, IReadOnlyList<GovernanceWriteIntent> entries)
    {
        switch (FormalRotationJournalIntent.Kind(intent))
        {
            case null:
                var revocation = CredentialRevocationIntent.Read(intent);
                return revocation.Environments().Select(environment => revocation.Event(intent.OperationId, environment)).ToArray();
            case FormalRotationJournalIntent.DeadlineRevocation:
                return [FormalRotationDeadlineIntent.Read(intent).Event(intent.OperationId)];
            case FormalRotationJournalIntent.Decision:
                var decision = FormalRotationDecisionIntent.Read(intent);
                if (decision.Decision is not (FormalRotationDecisionIntent.Complete or FormalRotationDecisionIntent.AbortRequested)) return null;
                var schedule = entries.SingleOrDefault(entry => entry.OperationId == decision.ScheduleId &&
                    entry.Previous.Sequence < intent.Previous.Sequence);
                return schedule is not null && FormalRotationJournalIntent.Hash(schedule) == decision.ScheduleHash
                    ? [decision.Event(intent.OperationId, FormalRotationScheduleIntent.Read(schedule))]
                    : null;
            default:
                return null;
        }
    }

    internal async Task<IndependentEvidence?> ObserveAsync(LocalEvidence local, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(BoundedWitnessHttpClient.RequestTimeout);
        var token = budget.Token;
        try
        {
            var (journal, journalWitness, history, _, _, _) = local.Witnesses;
            var environments = new HashSet<Guid>();
            foreach (var (id, evidence) in local.Environments)
            {
                try
                {
                    var proof = await history.ObserveAsync(journal, local.Position, id, token).WaitAsync(token);
                    if (proof == new GovernanceEnvironmentHistory(journal, local.Position, id, "Confirmed", evidence.Checkpoint))
                        environments.Add(id);
                }
                catch (Exception error) when (EvidenceUnavailable(error))
                {
                    // Environments are authorized before this lookup. An unavailable source only
                    // withholds confirmation for its affected environment.
                }
            }

            // Confirm the local sample after the environment checks. A remote prepare cannot
            // disappear at the same confirmed position, even after a failed local commit.
            var state = await journalWitness.ObserveAsync(journal, local.Confirmation.OperationId, token).WaitAsync(token);
            if (state is null || state.Pending is not null || state.Confirmed != local.Position ||
                state.OperationConfirmation != local.Confirmation) return null;
            return new(environments, started);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception error) when (EvidenceUnavailable(error)) { return null; }
    }

    internal static Observation? Reconcile(LocalEvidence first, IndependentEvidence? independent, LocalEvidence? latest)
    {
        if (latest is null || independent is null || latest.Position != first.Position || latest.Confirmation != first.Confirmation ||
            Stopwatch.GetElapsedTime(independent.Started) >= BoundedWitnessHttpClient.RequestTimeout) return null;

        var coverage = new Dictionary<Guid, Coverage>();
        foreach (var (id, current) in latest.Environments)
        {
            if (!independent.Environments.Contains(id) || !first.Environments.TryGetValue(id, out var previous) ||
                previous.Checkpoint != current.Checkpoint || previous.Ingress is not { } initial || current.Ingress is not { } final ||
                initial.ScopeHash != final.ScopeHash) continue;
            var retired = new Dictionary<(Guid Node, Guid Instance), RetiredCoverage>();
            foreach (var (target, proof) in final.RetiredInstances)
                if (initial.RetiredInstances.TryGetValue(target, out var before) && before.ScopeHash == proof.ScopeHash &&
                    before.ProofHash == proof.ProofHash && before.InventoryHash == proof.InventoryHash)
                    retired.Add(target, proof with { ValidUntil = before.ValidUntil < proof.ValidUntil ? before.ValidUntil : proof.ValidUntil });
            coverage.Add(id, final with { ValidUntil = initial.ValidUntil < final.ValidUntil ? initial.ValidUntil : final.ValidUntil,
                RetiredInstances = retired });
        }
        return new(coverage);
    }

    private static async Task<Coverage?> ObserveIngressAsync(WebApiDbContext db, RevocationEnvironmentDto affected,
        SecurityLedgerMaterial ledger, ICredentialRunningIngressWitness ingressWitness,
        ICredentialArchivedIngressWitness? archiveWitness, CancellationToken ct)
    {
        var now = await SecurityDatabaseClock.ObserveAsync(db, ct);
        if (affected.CoverageState == "SealedNoIngressAtCommit")
        {
            var environment = await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(x => x.Id == affected.EnvironmentId, ct);
            var seal = await CredentialArchivedIngress.RequireAsync(db, environment, ledger.OrganizationId, archiveWitness, now, ct);
            return new(ScopeHash(new { seal.Scope, seal.ProofHash }), seal.ValidUntil, new HashSet<(Guid, Guid)>(),
                new Dictionary<(Guid, Guid), RetiredCoverage>());
        }
        var (scope, _) = await CredentialRunningIngress.ReadAsync(db, affected.EnvironmentId, ledger, now, ct);
        var until = ingressWitness.VerifiedUntil(scope, now);
        return until is DateTimeOffset bound && bound > now
            ? new(GovernanceDeploymentEvidenceProtocol.ScopeHash(GovernanceDeploymentEvidenceProtocol.RunningIngress, scope), bound,
                scope.Instances.Select(x => (x.NodeId, x.InstanceId)).ToHashSet(), new Dictionary<(Guid, Guid), RetiredCoverage>())
            : null;
    }

    private static string ScopeHash<T>(T scope) => Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(scope)));

    private static bool EvidenceUnavailable(Exception error) =>
        error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException ||
        error is ApiException { Status: 404 or 409 or 422 or 503 };
}

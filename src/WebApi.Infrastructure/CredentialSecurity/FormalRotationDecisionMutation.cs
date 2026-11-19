using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

internal sealed class FormalRotationDecisionMutation(GovernanceJournalId journal, ICredentialRevocationCoverage ingress,
    IFormalRotationRequestProofWitness? proofs, Func<WebApiDbContext, CancellationToken, Task<DateTimeOffset>> clock)
{
    internal static async Task RequireRecoveredPreparationAsync(GovernanceJournalId journal, IGovernanceJournalWitness witness,
        GovernanceWriteIntent intent, GovernanceWitnessState observed, CancellationToken ct)
    {
        if (FormalRotationJournalIntent.Kind(intent) != FormalRotationJournalIntent.Decision) return;
        if (witness is not IFormalRotationJournalWitness ordered) throw FormalRotationJournalIntent.Invalid();
        var plan = FormalRotationDecisionIntent.Read(intent);
        var evidence = await ordered.ObserveFormalRotationDecisionAsync(journal, intent, plan.ScheduleId, plan.EnvironmentId,
            plan.HardDeadline, plan.Decision, ct);
        if (evidence != observed) throw FormalRotationJournalIntent.Invalid();
    }

    internal async Task<FormalRotationVerificationDto> ReadVerificationAsync(WebApiDbContext db, GovernanceJournalPosition previous,
        Guid rotationId, CancellationToken ct)
    {
        var scope = await ReadScopeAsync(db, journal, previous, rotationId, ct);
        var now = await clock(db, ct);
        if (now >= scope.Row.HardDeadline) return new(rotationId, "Closed", null, null, []);
        try
        {
            var (coverage, ledger, snapshot, ingressExpiry) = await ReadCoverageAsync(db, scope, now, ct);
            RequireBeforeDeadline(scope, ledger, now, abortOperation: null);
            var verified = new HashSet<(Guid, Guid)>();
            if (proofs is not null)
            {
                var observation = new FormalRotationRequestProofScope(journal, previous, rotationId, scope.Replacement.Id,
                    scope.Schedule.RecordedAt, scope.Row.HardDeadline, coverage);
                var supplied = await FormalRotationDecisionEvidence.ObserveAsync(proofs, observation, ct);
                var checkedAt = await clock(db, ct);
                if (checkedAt < now || checkedAt >= scope.Row.HardDeadline || checkedAt >= ingressExpiry || coverage.Nodes.Any(x => x.FreshUntil <= checkedAt))
                    return new(rotationId, "Unavailable", null, null, []);
                verified = await FormalRotationDecisionEvidence.ReadVerifiedTargetsAsync(db, scope.Schedule, coverage,
                    snapshot, scope.Replacement, checkedAt, supplied, ct);
            }
            var targets = coverage.Nodes.Select(x => new FormalRotationVerificationTarget(x.NodeId, x.InstanceId,
                verified.Contains((x.NodeId, x.InstanceId)) ? "Verified" : "Missing")).ToArray();
            var state = proofs is null ? "Unavailable" : verified.Count == targets.Length ? "Verified" : "AwaitingProof";
            return new(rotationId, state, coverage.ConfigVersion, coverage.DeploymentSequence, targets);
        }
        catch (ApiException error) when (error.Status is 409 or 503)
        {
            return new(rotationId, "Unavailable", null, null, []);
        }
    }

    internal async Task<GovernanceWriteIntent> PlanManualAsync(WebApiDbContext db, GovernanceJournalPosition previous,
        Guid rotationId, Guid operationId, Guid actorId, string decision, string reason, string requestHash, CancellationToken ct)
    {
        var scope = await ReadScopeAsync(db, journal, previous, rotationId, ct);
        var now = await clock(db, ct);
        var (coverage, ledger, snapshot, ingressExpiry) = await ReadCoverageAsync(db, scope, now, ct);
        RequireBeforeDeadline(scope, ledger, now, abortOperation: null);
        if (await db.Set<FormalRotationDecision>().AnyAsync(x => x.ScheduleId == rotationId, ct))
            throw PostgresFormalRotationService.Changed();

        FormalRotationRequestProof[] completed = [];
        if (decision == FormalRotationDecisionIntent.Complete)
        {
            if (proofs is null) throw PostgresFormalRotationPreparation.EvidenceUnavailable();
            var observation = new FormalRotationRequestProofScope(journal, previous, rotationId, scope.Replacement.Id,
                scope.Schedule.RecordedAt, scope.Row.HardDeadline, coverage);
            completed = await FormalRotationDecisionEvidence.ObserveAsync(proofs, observation, ct);
            now = await clock(db, ct);
            if (now >= ingressExpiry) throw PostgresFormalRotationPreparation.EvidenceUnavailable();
            RequireBeforeDeadline(scope, ledger, now, abortOperation: null);
            await FormalRotationDecisionEvidence.RequireSuccessfulRequestsAsync(db, scope.Schedule, coverage, snapshot,
                scope.Replacement, now, completed, ct);
        }

        var targets = await FormalRotationDeadlineMutation.ReadTargetsAsync(db, scope.Row.EnvironmentId, scope.Schedule, ct);
        return Create(scope, coverage, previous, operationId, actorId, decision, reason, requestHash, now,
            targets, completed, null, []);
    }

    internal async Task<GovernanceWriteIntent?> PlanAbortConfirmationAsync(WebApiDbContext db, GovernanceJournalPosition previous,
        FormalRotationDecision pending, DateTimeOffset now, CancellationToken ct)
    {
        var scope = await ReadScopeAsync(db, journal, previous, pending.ScheduleId, ct);
        var original = await ReadDecisionAsync(db, journal, previous, pending, ct);
        if (original.Decision != FormalRotationDecisionIntent.AbortRequested ||
            !FormalRotationJournalIntent.Instant(now) || now >= scope.Row.HardDeadline) return null;
        var (coverage, ledger, _, ingressExpiry) = await ReadCoverageAsync(db, scope, now, ct);
        RequireBeforeDeadline(scope, ledger, now, pending.Id);
        var replacement = ledger.Revocations.Single(x => x.CredentialId == scope.Replacement.Id);
        var acks = await FormalRotationDecisionEvidence.ReadAbortAcksAsync(db, coverage, replacement, now, ct);
        if (acks is null || await clock(db, ct) >= ingressExpiry) return null;
        var operationId = AbortConfirmationId(journal, scope.Row.Id, pending.Id);
        var hash = Convert.ToHexStringLower(SHA256.HashData(SecuritySignedEncoding.Unsigned(
            "webapi.formal-rotation.abort-confirmation.v1", new { pending.Id, operationId })));
        return Create(scope, coverage, previous, operationId, original.ActorId, FormalRotationDecisionIntent.AbortConfirmed,
            original.Reason, hash, now, [], [], pending.Id, acks);
    }

    internal async Task ValidateNewAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        var plan = FormalRotationDecisionIntent.Read(intent);
        var scope = await ReadScopeAsync(db, journal, intent.Previous, plan.ScheduleId, ct);
        RequireMatchingSchedule(scope.Schedule, scope.Row.IntentHash, plan);
        var now = await clock(db, ct);
        var (coverage, ledger, snapshot, ingressExpiry) = await ReadCoverageAsync(db, scope, now, ct);
        RequireBeforeDeadline(scope, ledger, now, plan.AbortOperationId);
        if (plan.RecordedAt > now || !MatchesCoverage(plan, coverage) ||
            coverage.Nodes.Any(node => node.FreshUntil - now <= BoundedWitnessHttpClient.RequestTimeout))
            throw PostgresFormalRotationService.Changed();
        await RequirePreviousDecisionAsync(db, journal, scope.Schedule, intent, plan, ct);

        if (plan.Decision == FormalRotationDecisionIntent.AbortConfirmed)
        {
            var tombstone = ledger.Revocations.Single(x => x.OperationId == plan.AbortOperationId);
            var acks = await FormalRotationDecisionEvidence.ReadAbortAcksAsync(db, coverage, tombstone, now, ct);
            if (acks is null || !acks.SequenceEqual(plan.AbortAcks)) throw PostgresFormalRotationService.Changed();
        }
        else
        {
            var targets = await FormalRotationDeadlineMutation.ReadTargetsAsync(db, plan.EnvironmentId, scope.Schedule, ct);
            if (!targets.Select(TargetIdentity).SequenceEqual(plan.RevocationTargets.Select(TargetIdentity)))
                throw PostgresFormalRotationService.Changed();
        }

        if (plan.Decision == FormalRotationDecisionIntent.Complete)
            await FormalRotationDecisionEvidence.RequireSuccessfulRequestsAsync(db, scope.Schedule, coverage, snapshot,
                scope.Replacement, plan.RecordedAt, plan.RequestProofs, ct);
        var sampled = await clock(db, ct);
        if (sampled < now || sampled >= plan.HardDeadline || ingressExpiry - sampled <= BoundedWitnessHttpClient.RequestTimeout ||
            coverage.Nodes.Any(node =>
            node.FreshUntil - sampled <= BoundedWitnessHttpClient.RequestTimeout)) throw PostgresFormalRotationService.Changed();
    }

    internal static async Task ApplyAsync(WebApiDbContext db, GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct)
    {
        var plan = FormalRotationDecisionIntent.Read(intent);
        var scope = await ReadScopeAsync(db, journal, intent.Previous, plan.ScheduleId, ct);
        RequireMatchingSchedule(scope.Schedule, scope.Row.IntentHash, plan);
        await RequirePreviousDecisionAsync(db, journal, scope.Schedule, intent, plan, ct);
        var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(plan.EnvironmentId, ct);
        // A prepared decision may be recovered after the original leases and proof
        // have expired. The independent authority ordered the immutable plan before
        // the deadline. Recheck its predecessor and binding before applying it.
        RequireBeforeDeadline(scope, ledger, plan.RecordedAt, plan.AbortOperationId);
        if (plan.SecurityGeneration != ledger.SecurityGeneration || plan.PreviousEpoch != ledger.Epoch || plan.PreviousHash != ledger.HeadHash)
            throw FormalRotationJournalIntent.Invalid();

        if (plan.Decision != FormalRotationDecisionIntent.AbortConfirmed)
        {
            var credential = plan.Decision == FormalRotationDecisionIntent.Complete ? scope.Old : scope.Replacement;
            db.Attach(credential);
            var application = await db.Set<ApplicationRecord>().SingleAsync(x => x.Id == scope.Schedule.ApplicationId, ct);
            if (application.OrganizationId != scope.Schedule.OrganizationId) throw FormalRotationJournalIntent.Invalid();
            await FormalRotationDeadlineMutation.ApplyRevocationAsync(db, credential, application, scope.Environment,
                plan.Event(intent.OperationId, scope.Schedule), plan.ActorId, plan.Reason, AuditAction(plan), plan.RevocationTargets, ct);
        }
        db.Add(new FormalRotationDecision
        {
            Id = intent.OperationId, ScheduleId = plan.ScheduleId, Kind = plan.Decision, RecordedAt = plan.RecordedAt,
            IntentHash = FormalRotationJournalIntent.Hash(intent)
        });
        db.Add(new AuditLog
        {
            UserId = plan.ActorId, OrganizationId = scope.Schedule.OrganizationId, ProjectId = scope.Schedule.ProjectId,
            EnvironmentId = plan.EnvironmentId, ResourceType = "credential-rotation", ResourceId = plan.ScheduleId.ToString(),
            Action = AuditAction(plan), CreatedAt = plan.RecordedAt, AfterJson = DecisionAudit(intent, scope.Schedule, plan)
        });
    }

    internal static async Task<bool> VerifyAsync(WebApiDbContext db, GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct)
    {
        var plan = FormalRotationDecisionIntent.Read(intent);
        var schedule = await FormalRotationMutation.ReadConfirmedScheduleAsync(db, journal, plan.ScheduleId, intent.Previous, ct);
        var row = await db.Set<FormalRotationDecision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == intent.OperationId, ct);
        if (row is null || !MatchesProjection(row, intent, plan)) return false;
        var scheduleHash = await db.Set<FormalCredentialRotation>().Where(x => x.Id == row.ScheduleId).Select(x => x.IntentHash).SingleAsync(ct);
        RequireMatchingSchedule(schedule, scheduleHash, plan);
        var audit = await db.Set<AuditLog>().Where(x => x.Action == AuditAction(plan) && x.UserId == plan.ActorId &&
            x.OrganizationId == schedule.OrganizationId && x.ProjectId == schedule.ProjectId && x.EnvironmentId == plan.EnvironmentId &&
            x.ResourceType == "credential-rotation" && x.ResourceId == plan.ScheduleId.ToString() && x.CreatedAt == plan.RecordedAt)
            .Select(x => x.AfterJson).ToArrayAsync(ct);
        if (!audit.Any(x => FormalRotationMutation.AuditMatches(x, DecisionAudit(intent, schedule, plan)))) return false;
        if (plan.Decision == FormalRotationDecisionIntent.AbortConfirmed)
        {
            await RequireConfirmedAbortRequestAsync(db, journal, schedule, intent, plan, ct);
            return true;
        }
        return await FormalRotationDeadlineMutation.VerifyRevocationAsync(db, plan.Event(intent.OperationId, schedule),
            schedule.ApplicationId, plan.ActorId, plan.Reason, AuditAction(plan), plan.RevocationTargets, ct);
    }

    internal static async Task<Guid[]> ReadResolvedAbortsAsync(WebApiDbContext db, GovernanceJournalId journal, GovernanceJournalPosition position,
        Guid? environmentId, CancellationToken ct)
    {
        var rows = await (from decision in db.Set<FormalRotationDecision>().AsNoTracking()
                          join schedule in db.Set<FormalCredentialRotation>().AsNoTracking() on decision.ScheduleId equals schedule.Id
                          where decision.Kind == FormalRotationDecisionIntent.AbortConfirmed &&
                              (environmentId == null || schedule.EnvironmentId == environmentId)
                          select decision).ToArrayAsync(ct);
        foreach (var row in rows) await ReadDecisionAsync(db, journal, position, row, ct);
        return rows.Select(x => x.ScheduleId).ToArray();
    }

    internal static async Task<FormalRotationDecisionIntent> ReadDecisionAsync(WebApiDbContext db, GovernanceJournalId journal,
        GovernanceJournalPosition position, FormalRotationDecision row, CancellationToken ct)
    {
        var intent = await FormalRotationMutation.ReadConfirmedIntentAsync(db, journal, row.Id, position, ct);
        var plan = FormalRotationDecisionIntent.Read(intent);
        if (!MatchesProjection(row, intent, plan) || !await VerifyAsync(db, journal, intent, ct)) throw FormalRotationJournalIntent.Invalid();
        return plan;
    }

    private static bool MatchesProjection(FormalRotationDecision row, GovernanceWriteIntent intent, FormalRotationDecisionIntent plan) =>
        row.Id == intent.OperationId && row.ScheduleId == plan.ScheduleId && row.Kind == plan.Decision &&
        row.RecordedAt == plan.RecordedAt && row.IntentHash == FormalRotationJournalIntent.Hash(intent);

    private async Task<(CredentialRevocationCoverageScope Coverage, SecurityLedgerMaterial Ledger, RuntimeSnapshot Snapshot,
        DateTimeOffset IngressExpiry)> ReadCoverageAsync(
        WebApiDbContext db, DecisionScope scope, DateTimeOffset now, CancellationToken ct)
    {
        var active = await CredentialRevocationMutation.ReadRunningCoverageAsync(db, scope.Old, scope.Environment, now, ct);
        var expiry = CredentialRunningIngress.VerifiedUntil(ingress, active.Coverage, active.Ingress, now);
        if (expiry is null || expiry <= now) throw PostgresFormalRotationPreparation.EvidenceUnavailable();
        return (active.Coverage, active.Ledger, active.Snapshot, expiry.Value);
    }

    private static void RequireBeforeDeadline(DecisionScope scope, SecurityLedgerMaterial ledger, DateTimeOffset now, Guid? abortOperation)
    {
        var replacement = ledger.Revocations.SingleOrDefault(x => x.CredentialId == scope.Replacement.Id);
        if (!FormalRotationJournalIntent.Instant(now) || now < scope.Schedule.RecordedAt || now >= scope.Row.HardDeadline ||
            ledger.OrganizationId != scope.Schedule.OrganizationId || ledger.ProjectId != scope.Schedule.ProjectId ||
            ledger.Revocations.Any(x => x.CredentialId == scope.Old.Id) || replacement?.OperationId != abortOperation)
            throw PostgresFormalRotationService.Changed();
    }

    private static async Task RequirePreviousDecisionAsync(WebApiDbContext db, GovernanceJournalId journal,
        FormalRotationScheduleIntent schedule, GovernanceWriteIntent intent, FormalRotationDecisionIntent plan, CancellationToken ct)
    {
        if (plan.Decision == FormalRotationDecisionIntent.AbortConfirmed)
            await RequireConfirmedAbortRequestAsync(db, journal, schedule, intent, plan, ct);
        if (await db.Set<FormalRotationDecision>().AnyAsync(x => x.ScheduleId == plan.ScheduleId && x.Id != plan.AbortOperationId, ct))
            throw PostgresFormalRotationService.Changed();
    }

    private static async Task RequireConfirmedAbortRequestAsync(WebApiDbContext db, GovernanceJournalId journal,
        FormalRotationScheduleIntent schedule, GovernanceWriteIntent intent, FormalRotationDecisionIntent plan, CancellationToken ct)
    {
        var requestId = plan.AbortOperationId ?? throw FormalRotationJournalIntent.Invalid();
        var request = await FormalRotationMutation.ReadConfirmedIntentAsync(db, journal, requestId, intent.Previous, ct);
        var original = FormalRotationDecisionIntent.Read(request);
        if (original.Decision != FormalRotationDecisionIntent.AbortRequested || original.ScheduleId != plan.ScheduleId ||
            original.ScheduleHash != plan.ScheduleHash || original.ActorId != plan.ActorId || original.Reason != plan.Reason ||
            intent.OperationId != AbortConfirmationId(journal, plan.ScheduleId, requestId) || plan.RecordedAt < original.RecordedAt ||
            original.SecurityGeneration != plan.SecurityGeneration || original.PreviousEpoch >= plan.PreviousEpoch ||
            !original.RevocationTargets.Select(x => (x.NodeId, x.InstanceId, x.ChallengeNonce)).SequenceEqual(
                plan.AbortAcks.Select(x => (x.NodeId, x.InstanceId, x.ChallengeNonce))) ||
            !await VerifyAsync(db, journal, request, ct))
            throw FormalRotationJournalIntent.Invalid();

        var eventEpoch = original.Event(requestId, schedule).Epoch;
        var recorded = await db.Set<SecurityAckVerification>().AsNoTracking().Where(x => x.OperationId == requestId &&
                x.EnvironmentId == plan.EnvironmentId && x.SecurityGeneration == plan.SecurityGeneration &&
                x.Epoch >= eventEpoch && x.Epoch <= plan.PreviousEpoch && x.ReceivedAt >= original.RecordedAt && x.ReceivedAt <= plan.RecordedAt)
            .OrderBy(x => x.NodeId).ThenBy(x => x.InstanceGuid)
            .Select(x => new FormalRotationAbortAck(x.NodeId, x.InstanceGuid, x.ChallengeNonce, x.LeaseId,
                x.Epoch, x.CheckpointHash, x.ReceivedAt)).ToArrayAsync(ct);
        if (!recorded.SequenceEqual(plan.AbortAcks)) throw FormalRotationJournalIntent.Invalid();
    }

    private static bool MatchesCoverage(FormalRotationDecisionIntent plan, CredentialRevocationCoverageScope scope) =>
        scope.EnvironmentId == plan.EnvironmentId && scope.SecurityGeneration == plan.SecurityGeneration &&
        scope.Epoch == plan.PreviousEpoch && scope.HeadHash == plan.PreviousHash && scope.ReleaseId == plan.ReleaseId &&
        scope.SnapshotHash == plan.SnapshotHash && scope.ConfigVersion == plan.ConfigVersion && scope.DeploymentSequence == plan.DeploymentSequence &&
        scope.Nodes.Select(x => (x.NodeId, x.InstanceId)).SequenceEqual(plan.CurrentTargets.Select(x => (x.NodeId, x.InstanceId)));

    private static async Task<DecisionScope> ReadScopeAsync(WebApiDbContext db, GovernanceJournalId journal,
        GovernanceJournalPosition previous, Guid rotationId, CancellationToken ct)
    {
        var schedule = await FormalRotationMutation.ReadConfirmedScheduleAsync(db, journal, rotationId, previous, ct);
        var row = await db.Set<FormalCredentialRotation>().AsNoTracking().SingleAsync(x => x.Id == rotationId, ct);
        var old = await FormalRotationMutation.RequireBindingAsync(db, schedule, schedule.CredentialId, ct);
        var replacement = await FormalRotationMutation.RequireBindingAsync(db, schedule, row.ReplacementCredentialId, ct);
        var environment = await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == row.EnvironmentId && x.ProjectId == schedule.ProjectId, ct);
        return new(row, schedule, environment, old, replacement);
    }

    private static void RequireMatchingSchedule(FormalRotationScheduleIntent schedule, string scheduleHash, FormalRotationDecisionIntent plan)
    {
        if (scheduleHash != plan.ScheduleHash || schedule.Window.EnvironmentId != plan.EnvironmentId ||
            schedule.Window.HardDeadline != plan.HardDeadline || schedule.RecordedAt > plan.RecordedAt)
            throw FormalRotationJournalIntent.Invalid();
    }

    private static GovernanceWriteIntent Create(DecisionScope scope, CredentialRevocationCoverageScope coverage, GovernanceJournalPosition previous,
        Guid operationId, Guid actorId, string decision, string reason, string requestHash, DateTimeOffset now,
        FormalRotationDeadlineTarget[] targets, FormalRotationRequestProof[] proofs, Guid? abortId, FormalRotationAbortAck[] acks)
    {
        var plan = new FormalRotationDecisionIntent(scope.Row.Id, scope.Row.IntentHash, coverage.EnvironmentId, actorId, decision,
            reason, requestHash, scope.Row.HardDeadline, now, coverage.SecurityGeneration, coverage.Epoch, coverage.HeadHash,
            coverage.ReleaseId, coverage.ConfigVersion, coverage.DeploymentSequence, coverage.SnapshotHash,
            coverage.Nodes.Select(x => new FormalRotationPreviewTarget(x.NodeId, x.InstanceId)).ToArray(),
            targets, proofs, abortId, acks);
        var intent = FormalRotationJournalIntent.Create(plan, operationId, previous);
        _ = FormalRotationDecisionIntent.Read(intent);
        return intent;
    }

    private static Guid AbortConfirmationId(GovernanceJournalId journal, Guid scheduleId, Guid abortOperationId) =>
        FormalRotationJournalIntent.OperationId("webapi.formal-rotation.abort-confirmation-operation.v1", new { journal, scheduleId, abortOperationId });

    private static (Guid Node, Guid Instance, bool Protocol, DateTimeOffset? Until) TargetIdentity(FormalRotationDeadlineTarget target) =>
        (target.NodeId, target.InstanceId, target.ProtocolSupported, target.LastLeaseUntil);

    private static string AuditAction(FormalRotationDecisionIntent plan) => plan.Decision switch
    {
        FormalRotationDecisionIntent.Complete => "credential.formal-rotation.complete",
        FormalRotationDecisionIntent.AbortRequested => "credential.formal-rotation.abort",
        _ => "credential.formal-rotation.abort-confirm"
    };

    private static string DecisionAudit(GovernanceWriteIntent intent, FormalRotationScheduleIntent schedule, FormalRotationDecisionIntent plan) =>
        JsonSerializer.Serialize(new { intent.OperationId, plan.Decision, plan.Reason, plan.ReleaseId, plan.DeploymentSequence,
            OriginalTargets = schedule.Targets, plan.CurrentTargets, plan.AbortOperationId, IntentHash = FormalRotationJournalIntent.Hash(intent) });

    private sealed record DecisionScope(FormalCredentialRotation Row, FormalRotationScheduleIntent Schedule,
        EnvironmentRecord Environment, ApplicationCredential Old, ApplicationCredential Replacement);
}

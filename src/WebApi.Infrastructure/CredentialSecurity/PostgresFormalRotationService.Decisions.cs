using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed partial class PostgresFormalRotationService
{
    public Task<FormalRotationReceipt> CompleteAsync(Guid rotationId, FormalRotationDecisionRequest request, string? ifMatch,
        CommandRequestContext command, ActorContext actor, CancellationToken ct = default,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession = null) =>
        DecideAsync(rotationId, request, FormalRotationDecisionIntent.Complete, ifMatch, command, actor, ct, requireSession);

    public Task<FormalRotationReceipt> AbortAsync(Guid rotationId, FormalRotationDecisionRequest request, string? ifMatch,
        CommandRequestContext command, ActorContext actor, CancellationToken ct = default,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession = null) =>
        DecideAsync(rotationId, request, FormalRotationDecisionIntent.AbortRequested, ifMatch, command, actor, ct, requireSession);

    private async Task<FormalRotationReceipt> DecideAsync(Guid rotationId, FormalRotationDecisionRequest request, string decision, string? ifMatch,
        CommandRequestContext command, ActorContext actor, CancellationToken ct, Func<WebApiDbContext, CancellationToken, Task>? requireSession)
    {
        if (request.Confirm != true || !FormalRotationJournalIntent.ValidReason(request.Reason))
            throw new ApiException(422, "formal_rotation_decision_invalid", "须明确确认轮换决定并选择理由。");
        if (command.IdempotencyKey is not { Length: >= 1 and <= 128 } ||
            command.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or ':' or '-')))
            throw new ApiException(422, "idempotency_required", "该命令需要有效的 Idempotency-Key。");
        if (rotationId == Guid.Empty) throw NotFound();

        var operationId = FormalRotationJournalIntent.OperationId("webapi.formal-rotation.manual-operation.v1",
            new { journal, actor.UserId, rotationId, command.IdempotencyKey });
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(
            new { rotationId, ifMatch, request.Reason, decision })));
        var decisions = new FormalRotationDecisionMutation(journal, coverage, requestProofs, SecurityDatabaseClock.ObserveAsync);
        var mutation = new FormalRotationMutation(actor, coverage, requireSession, archive, journal: journal,
            decisionMutation: decisions);
        var store = new PostgresGovernanceCommitStore(connectionString, mutation);
        var coordinator = new GovernanceCommitCoordinator(journal, store, witness, PrepareOrderedAsync, RequireRecoveredPreparationAsync);

        Task<GovernanceWriteIntent> PlanAsync() => store.InspectAsync(journal, operationId, async (db, local, saved, token) =>
        {
            if (!await db.Set<FormalCredentialRotation>().AnyAsync(x => x.Id == rotationId, token)) throw NotFound();
            var schedule = await FormalRotationMutation.ReadConfirmedScheduleAsync(db, journal, rotationId, local.Position, token);
            await mutation.AuthorizeManualAsync(db, schedule, token);
            if (saved is not null) return RequireSameRequest(saved);
            var observed = await witness.ObserveAsync(journal, operationId, token);
            if (observed.Pending?.OperationId == operationId) return RequireSameRequest(observed.Pending);
            RequireContinuous(local, observed);
            if (observed.OperationConfirmation is not null) throw PostgresFormalRotationPreparation.EvidenceUnavailable();

            var receipt = await ReadCurrentReceiptAsync(db, rotationId, local.Position, schedule, false, token);
            RevisionTag.Require(ifMatch, receipt.Revision);
            if (receipt.Status != "Scheduled") throw Changed();
            return await decisions.PlanManualAsync(db, local.Position, rotationId, operationId, actor.UserId,
                decision, request.Reason, requestHash, token);
        }, ct);

        GovernanceWriteIntent RequireSameRequest(GovernanceWriteIntent saved)
        {
            var plan = FormalRotationDecisionIntent.Read(saved);
            if (plan.ScheduleId != rotationId || plan.ActorId != actor.UserId ||
                plan.Decision != decision || plan.RequestHash != requestHash) throw Changed();
            return saved;
        }

        var intent = await PlanAsync();
        var result = await coordinator.ExecuteAsync(intent, ct);
        if (result == GovernanceCommitResult.Conflict)
        {
            intent = await PlanAsync();
            result = await coordinator.ExecuteAsync(intent, ct);
            if (result == GovernanceCommitResult.Conflict) throw Changed();
        }
        return await ReadAsync(rotationId, actor, ct, requireSession, result == GovernanceCommitResult.Replayed);
    }

    public Task<FormalRotationVerificationDto> ReadVerificationAsync(Guid rotationId, ActorContext actor, CancellationToken ct = default,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession = null)
    {
        if (rotationId == Guid.Empty) throw NotFound();
        var mutation = new FormalRotationMutation(actor, coverage, requireSession, archive, journal: journal);
        var store = new PostgresGovernanceCommitStore(connectionString, mutation);
        return store.InspectAsync<FormalRotationVerificationDto>(journal, rotationId, async (db, local, saved, token) =>
        {
            var schedule = await RequireObservedScheduleAsync(db, rotationId, local, saved, mutation, token);
            var receipt = await ReadCurrentReceiptAsync(db, rotationId, local.Position, schedule, false, token);
            if (receipt.Status != "Scheduled") return new(rotationId, "Closed", null, null, []);
            var decisions = new FormalRotationDecisionMutation(journal, coverage, requestProofs, SecurityDatabaseClock.ObserveAsync);
            return await decisions.ReadVerificationAsync(db, local.Position, rotationId, token);
        }, ct);
    }

    private async Task<FormalRotationScheduleIntent> RequireObservedScheduleAsync(WebApiDbContext db, Guid rotationId,
        GovernanceLocalJournalState local, GovernanceWriteIntent? saved, FormalRotationMutation mutation, CancellationToken ct)
    {
        if (saved is null || FormalRotationJournalIntent.Kind(saved) != FormalRotationJournalIntent.Schedule) throw NotFound();
        var schedule = FormalRotationScheduleIntent.Read(saved);
        await mutation.AuthorizeScheduleAsync(db, schedule, ct);
        var observed = await witness.ObserveAsync(journal, rotationId, ct);
        RequireContinuous(local, observed);
        if (observed.OperationConfirmation != new GovernanceOperationConfirmation(rotationId, GovernanceJournalProtocol.NextPosition(journal, saved)))
            throw PostgresFormalRotationPreparation.EvidenceUnavailable();
        return schedule;
    }

    private async Task<FormalRotationReceipt> ReadCurrentReceiptAsync(WebApiDbContext db, Guid rotationId, GovernanceJournalPosition position,
        FormalRotationScheduleIntent schedule, bool replayed, CancellationToken ct)
    {
        var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(schedule.Window.EnvironmentId, ct);
        var revokeId = ledger.Revocations.SingleOrDefault(x => x.CredentialId == schedule.CredentialId)?.OperationId;
        var receipt = schedule.Receipt(rotationId, revokeId, replayed);
        var rows = await db.Set<FormalRotationDecision>().AsNoTracking().Where(x => x.ScheduleId == rotationId).ToArrayAsync(ct);
        foreach (var row in rows) await FormalRotationDecisionMutation.ReadDecisionAsync(db, journal, position, row, ct);
        var completed = rows.SingleOrDefault(x => x.Kind == FormalRotationDecisionIntent.Complete);
        var abort = rows.SingleOrDefault(x => x.Kind == FormalRotationDecisionIntent.AbortRequested);
        var aborted = rows.Any(x => x.Kind == FormalRotationDecisionIntent.AbortConfirmed);
        var status = aborted ? "Aborted" : completed is not null ? "ManualRevocationRecorded"
            : receipt.RevocationOperationId is not null ? "RevocationRecorded"
            : abort is not null ? "AbortPending" : "Scheduled";
        return receipt with
        {
            Status = status,
            Revision = 1 + rows.Length + (receipt.RevocationOperationId is not null && completed is null && !aborted ? 1 : 0),
            RevocationOperationId = aborted ? null : receipt.RevocationOperationId,
            AbortRevocationOperationId = abort?.Id
        };
    }

    private async Task<GovernanceWriteIntent?> FindAbortToConfirmAsync(WebApiDbContext db, GovernanceJournalPosition position,
        FormalRotationDecisionMutation decisions, DateTimeOffset now, CancellationToken ct)
    {
        var targets = db.Set<SecurityOperationTarget>().AsNoTracking();
        var acknowledgments = db.Set<SecurityAckVerification>().AsNoTracking();
        var ready = from decision in db.Set<FormalRotationDecision>().AsNoTracking()
                             join schedule in db.Set<FormalCredentialRotation>().AsNoTracking() on decision.ScheduleId equals schedule.Id
                             where decision.Kind == FormalRotationDecisionIntent.AbortRequested && now < schedule.HardDeadline &&
                                 !db.Set<FormalRotationDecision>().Any(x => x.ScheduleId == schedule.Id &&
                                     x.Kind == FormalRotationDecisionIntent.AbortConfirmed) &&
                                 !db.Set<CredentialRevocation>().Any(x => x.CredentialId == schedule.CredentialId && x.EnvironmentId == schedule.EnvironmentId) &&
                                 targets.Count(x => x.OperationId == decision.Id && x.EnvironmentId == schedule.EnvironmentId) >= 2 &&
                                 !targets.Any(x => x.OperationId == decision.Id && x.EnvironmentId == schedule.EnvironmentId &&
                                     (!x.ProtocolSupportedAtFreeze || x.SupersededByInstanceGuid != null ||
                                      !acknowledgments.Any(ack => ack.OperationId == x.OperationId && ack.EnvironmentId == x.EnvironmentId &&
                                          ack.NodeId == x.NodeId && ack.InstanceGuid == x.InstanceGuid && ack.ChallengeNonce == x.ChallengeNonce)))
                             orderby schedule.HardDeadline, schedule.Id
                             select decision;
        // Partition the scan under the writer fence. One environment waiting
        // for current evidence must not keep later ready operations out.
        for (var offset = 0; ; offset += MaximumDuePerPass)
        {
            var batch = await ready.Skip(offset).Take(MaximumDuePerPass).ToArrayAsync(ct);
            foreach (var candidate in batch)
            {
                try
                {
                    var intent = await decisions.PlanAbortConfirmationAsync(db, position, candidate, now, ct);
                    if (intent is not null) return intent;
                }
                catch (ApiException error) when (error.Status is 409 or 503)
                {
                    // This schedule's original deadline remains armed.
                }
            }
            if (batch.Length < MaximumDuePerPass) return null;
        }
    }

    private static ApiException NotFound() => new(404, "formal_rotation_not_found", "找不到正式轮换。");
}

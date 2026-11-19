using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed partial class PostgresFormalRotationService(string connectionString, GovernanceJournalId journal,
    IFormalRotationJournalWitness witness, ICredentialRevocationCoverage coverage, IFormalRotationLeaseHorizonWitness? horizon,
    ICredentialArchivedIngressWitness? archive = null, IFormalRotationRequestProofWitness? requestProofs = null)
{
    private const int MaximumDuePerPass = 32;
    internal Func<WebApiDbContext, CancellationToken, Task<DateTimeOffset>> DeadlineClock { get; init; } = SecurityDatabaseClock.ObserveAsync;

    public async Task<FormalRotationReceipt> ConfirmAsync(Guid credentialId, FormalRotationConfirmRequest request,
        string? ifMatch, CommandRequestContext command, ActorContext actor, CancellationToken ct = default,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession = null)
    {
        if (request.ConfirmAutomaticRevocation != true || !FormalRotationJournalIntent.ValidReason(request.Reason) ||
            request.Window?.ReplacementCredentialId is not Guid replacementId || replacementId == Guid.Empty ||
            !SecuritySignedEncoding.HexHash(request.PreviewHash))
            throw new ApiException(422, "formal_rotation_confirmation_invalid", "须确认自动撤旧并提交替换凭证、预览和理由。");
        if (command.IdempotencyKey is not { Length: >= 1 and <= 128 } ||
            command.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or ':' or '-')))
            throw new ApiException(422, "idempotency_required", "该命令需要有效的 Idempotency-Key。");

        var window = PostgresFormalRotationPreparation.Normalize(request.Window);
        var operationId = FormalRotationJournalIntent.OperationId("webapi.formal-rotation.schedule-operation.v1",
            new { journal, actor.UserId, credentialId, command.IdempotencyKey });
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(
            new { credentialId, window, ifMatch, request.PreviewHash, request.Reason })));
        var preparation = new PostgresFormalRotationPreparation(connectionString, journal, coverage, horizon);
        var mutation = new FormalRotationMutation(actor, coverage, requireSession, archive, preparation, journal);
        var store = new PostgresGovernanceCommitStore(connectionString, mutation);
        var coordinator = new GovernanceCommitCoordinator(journal, store, witness, PrepareOrderedAsync, RequireRecoveredPreparationAsync);

        Task<GovernanceWriteIntent> PlanAsync() => store.InspectAsync(journal, operationId, async (db, local, saved, token) =>
        {
            if (saved is not null) return RequireSameRequest(saved);
            var observed = await witness.ObserveAsync(journal, operationId, token);
            if (observed.Pending?.OperationId == operationId) return RequireSameRequest(observed.Pending);
            RequireContinuous(local, observed);
            if (observed.OperationConfirmation is not null) throw PostgresFormalRotationPreparation.EvidenceUnavailable();

            var preview = await preparation.ReadPreviewAsync(db, local, credentialId, window, ifMatch, actor, requireSession, token);
            if (preview.PreviewHash != request.PreviewHash) throw Changed();
            await FormalRotationMutation.RequireNoScheduleAsync(db, journal, local.Position, credentialId, replacementId, token);
            var credential = await db.Set<ApplicationCredential>().AsNoTracking().SingleAsync(item => item.Id == credentialId, token);
            var now = await SecurityDatabaseClock.ObserveAsync(db, token);
            var plan = new FormalRotationScheduleIntent(credentialId, preview.ApplicationId, actor.UserId, credential.OrganizationId,
                credential.ProjectId!.Value, window, preview.CredentialRevision, now, request.Reason, request.PreviewHash, requestHash, [.. preview.Targets]);
            var intent = FormalRotationJournalIntent.Create(plan, operationId, local.Position);
            _ = FormalRotationScheduleIntent.Read(intent);
            return intent;
        }, ct);

        GovernanceWriteIntent RequireSameRequest(GovernanceWriteIntent intent)
        {
            var plan = FormalRotationScheduleIntent.Read(intent);
            if (plan.CredentialId != credentialId || plan.ActorId != actor.UserId || plan.RequestHash != requestHash) throw Changed();
            return intent;
        }

        var intent = await PlanAsync();
        var result = await coordinator.ExecuteAsync(intent, ct);
        if (result == GovernanceCommitResult.Conflict)
        {
            // A concurrent exact request may have persisted the same command
            // after this caller sampled its plan.
            intent = await PlanAsync();
            result = await coordinator.ExecuteAsync(intent, ct);
            if (result == GovernanceCommitResult.Conflict) throw Changed();
        }
        return await ReadAsync(operationId, actor, ct, requireSession, result == GovernanceCommitResult.Replayed);
    }

    public Task<FormalRotationReceipt> ReadAsync(Guid rotationId, ActorContext actor, CancellationToken ct = default,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession = null) =>
        ReadAsync(rotationId, actor, ct, requireSession, false);

    private Task<FormalRotationReceipt> ReadAsync(Guid rotationId, ActorContext actor, CancellationToken ct,
        Func<WebApiDbContext, CancellationToken, Task>? requireSession, bool replayed)
    {
        var mutation = new FormalRotationMutation(actor, coverage, requireSession, archive, journal: journal);
        var store = new PostgresGovernanceCommitStore(connectionString, mutation);
        return store.InspectAsync(journal, rotationId, async (db, local, saved, token) =>
        {
            var schedule = await RequireObservedScheduleAsync(db, rotationId, local, saved, mutation, token);
            return await ReadCurrentReceiptAsync(db, rotationId, local.Position, schedule, replayed, token);
        }, ct);
    }

    public async Task<int> RevokeDueAsync(CancellationToken ct = default)
    {
        var decisions = new FormalRotationDecisionMutation(journal, coverage, requestProofs, DeadlineClock);
        var mutation = new FormalRotationMutation(null, coverage, archive: archive, journal: journal, deadlineClock: DeadlineClock,
            decisionMutation: decisions);
        var store = new PostgresGovernanceCommitStore(connectionString, mutation);
        var coordinator = new GovernanceCommitCoordinator(journal, store, witness, PrepareOrderedAsync, RequireRecoveredPreparationAsync);
        var recoveredDeadline = false;
        if (!await coordinator.RecoverSelectedAsync(intent =>
            {
                var kind = FormalRotationJournalIntent.Kind(intent);
                recoveredDeadline = kind == FormalRotationJournalIntent.DeadlineRevocation;
                return recoveredDeadline || kind is FormalRotationJournalIntent.Schedule or FormalRotationJournalIntent.Decision;
            }, ct)) return 0;
        var applied = recoveredDeadline ? 1 : 0;

        for (var attempt = 0; attempt < MaximumDuePerPass; attempt++)
        {
            var next = await store.InspectAsync(journal, Guid.NewGuid(), async (db, local, _, token) =>
            {
                RequireContinuous(local, await witness.ObserveAsync(journal, null, token));
                var now = await DeadlineClock(db, token);
                if (!FormalRotationJournalIntent.Instant(now)) throw FormalRotationJournalIntent.Invalid();
                var aborted = await FormalRotationDecisionMutation.ReadResolvedAbortsAsync(db, journal, local.Position, null, token);
                var row = await db.Set<FormalCredentialRotation>().AsNoTracking().Where(item => item.HardDeadline <= now &&
                        !aborted.Contains(item.Id) &&
                        !db.Set<CredentialRevocation>().Any(revoked => revoked.EnvironmentId == item.EnvironmentId &&
                            revoked.CredentialId == item.CredentialId))
                    .OrderBy(item => item.HardDeadline).ThenBy(item => item.Id).FirstOrDefaultAsync(token);
                return row is null ? await FindAbortToConfirmAsync(db, local.Position, decisions, now, token)
                    : await FormalRotationDeadlineMutation.PlanAsync(db, journal, local.Position, row, now, token);
            }, ct);
            if (next is null) break;
            if (await coordinator.ExecuteAsync(next, ct) == GovernanceCommitResult.Committed &&
                FormalRotationJournalIntent.Kind(next) == FormalRotationJournalIntent.DeadlineRevocation) applied++;
        }
        return applied;
    }

    private Task<GovernanceWitnessState> PrepareOrderedAsync(GovernanceWriteIntent intent, CancellationToken ct)
    {
        var kind = FormalRotationJournalIntent.Kind(intent);
        if (kind == FormalRotationJournalIntent.Schedule)
        {
            var plan = FormalRotationScheduleIntent.Read(intent);
            return witness.PrepareFormalRotationAsync(journal, intent, plan.Window.EnvironmentId, plan.Window.HardDeadline, ct);
        }
        if (kind == FormalRotationJournalIntent.Decision)
        {
            var plan = FormalRotationDecisionIntent.Read(intent);
            return witness.PrepareFormalRotationDecisionAsync(journal, intent, plan.ScheduleId, plan.EnvironmentId, plan.HardDeadline,
                plan.Decision, ct);
        }
        var due = FormalRotationDeadlineIntent.Read(intent);
        return witness.PrepareFormalRotationRevocationAsync(journal, intent, due.ScheduleId, due.EnvironmentId, due.HardDeadline, ct);
    }

    private static void RequireContinuous(GovernanceLocalJournalState local, GovernanceWitnessState observed)
    {
        if (local.AwaitingConfirmation is not null || observed.Pending is not null || local.Position != observed.Confirmed)
            throw PostgresFormalRotationPreparation.EvidenceUnavailable();
    }

    private Task RequireRecoveredPreparationAsync(GovernanceWriteIntent intent, GovernanceWitnessState observed, CancellationToken ct) =>
        FormalRotationDecisionMutation.RequireRecoveredPreparationAsync(journal, witness, intent, observed, ct);

    internal static ApiException Changed() =>
        new(409, "formal_rotation_changed", "轮换信息或已登记的决定有变更，请重新核对。");
}

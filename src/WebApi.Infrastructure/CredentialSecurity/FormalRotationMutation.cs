using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.CredentialSecurity;

// Every operation in the one journal must remain verifiable by every caller,
// even if that caller cannot initiate or recover that operation.
internal sealed class FormalRotationMutation(ActorContext? actor, ICredentialRevocationCoverage coverage,
    Func<WebApiDbContext, CancellationToken, Task>? requireSession = null, ICredentialArchivedIngressWitness? archive = null,
    PostgresFormalRotationPreparation? preparation = null, GovernanceJournalId? journal = null,
    Func<WebApiDbContext, CancellationToken, Task<DateTimeOffset>>? deadlineClock = null,
    FormalRotationDecisionMutation? decisionMutation = null) : IPostgresGovernanceMutation
{
    private const string ScheduleAction = "credential.formal-rotation.confirm";
    private readonly CredentialRevocationMutation revocation = new(actor ?? new(Guid.Empty, "formal-rotation"), coverage, requireSession, archive);

    public async Task AuthorizeAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        switch (FormalRotationJournalIntent.Kind(intent))
        {
            case FormalRotationJournalIntent.Schedule:
                var schedule = FormalRotationScheduleIntent.Read(intent);
                if (actor?.UserId != schedule.ActorId) throw PermissionDenied();
                await AuthorizeScheduleAsync(db, schedule, ct);
                break;
            case FormalRotationJournalIntent.DeadlineRevocation:
                if (actor is not null) throw PermissionDenied();
                _ = FormalRotationDeadlineIntent.Read(intent);
                break;
            case FormalRotationJournalIntent.Decision:
                await AuthorizeDecisionAsync(db, intent, ct);
                break;
            default:
                if (actor is null) throw PermissionDenied();
                await revocation.AuthorizeAsync(db, intent, ct);
                break;
        }
    }

    public Task AuthorizeRecoveryAsync(WebApiDbContext db, CancellationToken ct) =>
        actor is null ? Task.CompletedTask : revocation.AuthorizeRecoveryAsync(db, ct);

    public async Task ValidateNewAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        switch (FormalRotationJournalIntent.Kind(intent))
        {
            case FormalRotationJournalIntent.Schedule:
                var schedule = FormalRotationScheduleIntent.Read(intent);
                if (preparation is null || actor?.UserId != schedule.ActorId) throw PermissionDenied();
                var scope = await AuthorizeScheduleAsync(db, schedule, ct);
                var preview = await preparation.ReadPreviewAsync(db, new(intent.Previous, null), schedule.CredentialId,
                    schedule.Window, RevisionTag.Format(schedule.CredentialRevision), actor, requireSession, ct);
                if (preview.PreviewHash != schedule.PreviewHash || preview.ApplicationId != scope.ApplicationId ||
                    !preview.Targets.SequenceEqual(schedule.Targets) || schedule.RecordedAt > await SecurityDatabaseClock.ObserveAsync(db, ct))
                    throw PostgresFormalRotationService.Changed();
                await RequireNoScheduleAsync(db, journal ?? throw FormalRotationJournalIntent.Invalid(), intent.Previous,
                    schedule.CredentialId, schedule.Window.ReplacementCredentialId!.Value, ct);
                break;
            case FormalRotationJournalIntent.DeadlineRevocation:
                if (actor is not null || journal is null) throw PermissionDenied();
                await FormalRotationDeadlineMutation.ValidateNewAsync(db, journal, intent, Clock, ct);
                break;
            case FormalRotationJournalIntent.Decision:
                if (decisionMutation is null) throw PermissionDenied();
                await AuthorizeDecisionAsync(db, intent, ct);
                await decisionMutation.ValidateNewAsync(db, intent, ct);
                break;
            default:
                await revocation.ValidateNewAsync(db, intent, ct);
                break;
        }
    }

    public async Task ApplyAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        switch (FormalRotationJournalIntent.Kind(intent))
        {
            case FormalRotationJournalIntent.Schedule:
                // The exact plan was durably prepared at the deadline-aware
                // authority. Recovery preserves that decision across time/role changes.
                var plan = FormalRotationScheduleIntent.Read(intent);
                await RequireNoScheduleAsync(db, journal ?? throw FormalRotationJournalIntent.Invalid(), intent.Previous,
                    plan.CredentialId, plan.Window.ReplacementCredentialId!.Value, ct);
                await RequireBindingAsync(db, plan, plan.CredentialId, ct);
                db.Add(new FormalCredentialRotation
                {
                    Id = intent.OperationId, CredentialId = plan.CredentialId, EnvironmentId = plan.Window.EnvironmentId,
                    ReplacementCredentialId = plan.Window.ReplacementCredentialId.Value, HardDeadline = plan.Window.HardDeadline,
                    RecordedAt = plan.RecordedAt, IntentHash = FormalRotationJournalIntent.Hash(intent)
                });
                db.Add(new AuditLog
                {
                    UserId = plan.ActorId, OrganizationId = plan.OrganizationId, ProjectId = plan.ProjectId,
                    EnvironmentId = plan.Window.EnvironmentId, ResourceType = "credential", ResourceId = plan.CredentialId.ToString(),
                    Action = ScheduleAction, CreatedAt = plan.RecordedAt, AfterJson = ScheduleAudit(intent, plan)
                });
                break;
            case FormalRotationJournalIntent.DeadlineRevocation:
                await FormalRotationDeadlineMutation.ApplyAsync(db, journal ?? throw FormalRotationJournalIntent.Invalid(), intent, Clock, ct);
                break;
            case FormalRotationJournalIntent.Decision:
                await FormalRotationDecisionMutation.ApplyAsync(db, journal ?? throw FormalRotationJournalIntent.Invalid(), intent, ct);
                break;
            default:
                if (actor is null) throw PermissionDenied();
                await revocation.ApplyAsync(db, intent, ct);
                break;
        }
    }

    public Task<bool> VerifyAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct) => VerifyCommittedAsync(db, journal, intent, ct);

    internal static Task<bool> VerifyCommittedAsync(WebApiDbContext db, GovernanceJournalId? journal, GovernanceWriteIntent intent,
        CancellationToken ct) =>
        FormalRotationJournalIntent.Kind(intent) switch
        {
            FormalRotationJournalIntent.Schedule => VerifyScheduleAsync(db, intent, ct),
            FormalRotationJournalIntent.DeadlineRevocation => FormalRotationDeadlineMutation.VerifyAsync(db,
                journal ?? throw FormalRotationJournalIntent.Invalid(), intent, ct),
            FormalRotationJournalIntent.Decision => FormalRotationDecisionMutation.VerifyAsync(db,
                journal ?? throw FormalRotationJournalIntent.Invalid(), intent, ct),
            _ => CredentialRevocationMutation.VerifyCommittedAsync(db, intent, ct)
        };

    internal async Task<ApplicationCredential> AuthorizeScheduleAsync(WebApiDbContext db, FormalRotationScheduleIntent plan, CancellationToken ct)
    {
        if (actor is null) throw PermissionDenied();
        if (requireSession is not null) await requireSession(db, ct);
        var credential = await RequireBindingAsync(db, plan, plan.CredentialId, ct);
        var target = new ResourceRef("credential", credential.Id, new(plan.OrganizationId, plan.ProjectId, plan.Window.EnvironmentId));
        var auth = new AuthorizationService(db);
        await auth.RequireAsync(actor, "credential.manage", target, ct);
        await auth.RequireAsync(actor, "credential.revoke", target, ct);
        return credential;
    }

    private async Task AuthorizeDecisionAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        var plan = FormalRotationDecisionIntent.Read(intent);
        if (plan.Decision == FormalRotationDecisionIntent.AbortConfirmed)
        {
            if (actor is not null) throw PermissionDenied();
            return;
        }
        if (actor?.UserId != plan.ActorId || journal is null) throw PermissionDenied();
        var schedule = await ReadConfirmedScheduleAsync(db, journal, plan.ScheduleId, intent.Previous, ct);
        await AuthorizeManualAsync(db, schedule, ct);
    }

    internal async Task AuthorizeManualAsync(WebApiDbContext db, FormalRotationScheduleIntent schedule, CancellationToken ct)
    {
        await AuthorizeScheduleAsync(db, schedule, ct);
        var replacement = await RequireBindingAsync(db, schedule, schedule.Window.ReplacementCredentialId!.Value, ct);
        var target = new ResourceRef("credential", replacement.Id, new(schedule.OrganizationId, schedule.ProjectId, schedule.Window.EnvironmentId));
        var auth = new AuthorizationService(db);
        await auth.RequireAsync(actor!, "credential.manage", target, ct);
        await auth.RequireAsync(actor!, "credential.revoke", target, ct);
    }

    internal static async Task RequireNoScheduleAsync(WebApiDbContext db, GovernanceJournalId journal, GovernanceJournalPosition position,
        Guid credentialId, Guid replacementId, CancellationToken ct)
    {
        if (await db.Set<FormalCredentialRotation>().AnyAsync(item => item.CredentialId == replacementId || item.ReplacementCredentialId == replacementId, ct) ||
            await db.Set<SecurityOperation>().AnyAsync(item => item.Kind == "CredentialRevoke" && item.CredentialId == credentialId, ct))
            throw PostgresFormalRotationService.Changed();
        var existing = await db.Set<FormalCredentialRotation>().Where(x => x.CredentialId == credentialId).Select(x => x.Id).ToArrayAsync(ct);
        if (existing.Length == 0) return;
        var aborted = await db.Set<FormalRotationDecision>().AsNoTracking()
            .Where(x => existing.Contains(x.ScheduleId) && x.Kind == FormalRotationDecisionIntent.AbortConfirmed).ToArrayAsync(ct);
        if (aborted.Length != existing.Length) throw PostgresFormalRotationService.Changed();
        foreach (var decision in aborted)
            await FormalRotationDecisionMutation.ReadDecisionAsync(db, journal, position, decision, ct);
    }

    internal static async Task<FormalRotationScheduleIntent> ReadConfirmedScheduleAsync(WebApiDbContext db, GovernanceJournalId journal,
        Guid id, GovernanceJournalPosition latest, CancellationToken ct)
    {
        var intent = await ReadConfirmedIntentAsync(db, journal, id, latest, ct);
        if (!await VerifyScheduleAsync(db, intent, ct)) throw FormalRotationJournalIntent.Invalid();
        return FormalRotationScheduleIntent.Read(intent);
    }

    internal static async Task<GovernanceWriteIntent> ReadConfirmedIntentAsync(WebApiDbContext db, GovernanceJournalId journal,
        Guid id, GovernanceJournalPosition latest, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<ScheduleJournalRow>($"""
            SELECT prepared.sequence AS "Sequence", prepared.previous_hash AS "PreviousHash",
                   prepared.recovery_material AS "RecoveryMaterial", prepared.position_hash AS "PositionHash"
              FROM credential_governance.journal_entries prepared
              JOIN credential_governance.journal_entries confirmed
                ON confirmed.sequence = prepared.sequence AND confirmed.operation_id = prepared.operation_id
               AND confirmed.is_confirmation AND confirmed.position_hash = prepared.position_hash
             WHERE NOT prepared.is_confirmation AND prepared.operation_id = {id}
            """).SingleOrDefaultAsync(ct);
        if (row is null || row.Sequence is <= 0 || row.Sequence > latest.Sequence) throw FormalRotationJournalIntent.Invalid();
        var intent = new GovernanceWriteIntent(id, new(row.Sequence - 1, row.PreviousHash), row.RecoveryMaterial);
        var position = GovernanceJournalProtocol.NextPosition(journal, intent);
        if (position.Hash != row.PositionHash || position.Sequence == latest.Sequence && position != latest)
            throw FormalRotationJournalIntent.Invalid();
        return intent;
    }

    internal static async Task<bool> VerifyScheduleAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct)
    {
        var plan = FormalRotationScheduleIntent.Read(intent);
        var hash = FormalRotationJournalIntent.Hash(intent);
        if (!await db.Set<FormalCredentialRotation>().AnyAsync(item => item.Id == intent.OperationId && item.IntentHash == hash &&
                item.CredentialId == plan.CredentialId && item.ReplacementCredentialId == plan.Window.ReplacementCredentialId &&
                item.EnvironmentId == plan.Window.EnvironmentId && item.RecordedAt == plan.RecordedAt && item.HardDeadline == plan.Window.HardDeadline, ct))
            return false;
        var audit = ScheduleAudit(intent, plan);
        return (await db.Set<AuditLog>().Where(item => item.Action == ScheduleAction && item.UserId == plan.ActorId &&
            item.OrganizationId == plan.OrganizationId && item.ProjectId == plan.ProjectId && item.EnvironmentId == plan.Window.EnvironmentId &&
            item.ResourceType == "credential" && item.ResourceId == plan.CredentialId.ToString() && item.CreatedAt == plan.RecordedAt)
            .Select(item => item.AfterJson).ToArrayAsync(ct)).Any(value => AuditMatches(value, audit));
    }

    internal static bool AuditMatches(string? stored, string expected)
    {
        if (stored is null) return false;
        using var recorded = JsonDocument.Parse(stored);
        using var expectedDocument = JsonDocument.Parse(expected);
        return JsonElement.DeepEquals(recorded.RootElement, expectedDocument.RootElement);
    }

    private static string ScheduleAudit(GovernanceWriteIntent intent, FormalRotationScheduleIntent plan) =>
        JsonSerializer.Serialize(new { RotationId = intent.OperationId, plan.Window, plan.Reason, plan.PreviewHash, plan.Targets });

    internal static async Task<ApplicationCredential> RequireBindingAsync(WebApiDbContext db, FormalRotationScheduleIntent plan, Guid credentialId,
        CancellationToken ct)
    {
        return await db.Set<ApplicationCredential>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == credentialId &&
            item.ApplicationId == plan.ApplicationId && item.OrganizationId == plan.OrganizationId && item.ProjectId == plan.ProjectId &&
            item.EnvironmentId == plan.Window.EnvironmentId && item.BindingMode == "EnvironmentBound", ct)
            ?? throw FormalRotationJournalIntent.Invalid();
    }

    private Func<WebApiDbContext, CancellationToken, Task<DateTimeOffset>> Clock => deadlineClock ?? SecurityDatabaseClock.ObserveAsync;
    private static ApiException PermissionDenied() => new(403, "permission_denied", "缺少操作权限。");

    private sealed class ScheduleJournalRow
    {
        public long Sequence { get; init; }
        public string PreviousHash { get; init; } = "";
        public string RecoveryMaterial { get; init; } = "";
        public string PositionHash { get; init; } = "";
    }
}

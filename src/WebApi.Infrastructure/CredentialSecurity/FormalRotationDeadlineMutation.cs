using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

internal static class FormalRotationDeadlineMutation
{
    private const string AuditAction = "credential.formal-rotation.deadline-revoke";

    internal static async Task<GovernanceWriteIntent> PlanAsync(WebApiDbContext db, GovernanceJournalId journal,
        GovernanceJournalPosition previous, FormalCredentialRotation row, DateTimeOffset now, CancellationToken ct)
    {
        var schedule = await FormalRotationMutation.ReadConfirmedScheduleAsync(db, journal, row.Id, previous, ct);
        var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(row.EnvironmentId, ct);
        if (ledger.OrganizationId != schedule.OrganizationId || ledger.ProjectId != schedule.ProjectId ||
            ledger.Revocations.Any(item => item.CredentialId == row.CredentialId) || now < row.HardDeadline)
            throw FormalRotationJournalIntent.Invalid();
        var targets = await ReadTargetsAsync(db, row.EnvironmentId, schedule, ct);
        var plan = new FormalRotationDeadlineIntent(row.Id, row.IntentHash, row.CredentialId, schedule.ApplicationId,
            schedule.ActorId, ledger.OrganizationId, ledger.ProjectId, row.EnvironmentId, ledger.SecurityGeneration, ledger.Epoch,
            ledger.HeadHash, row.HardDeadline, now, targets);
        return FormalRotationJournalIntent.Create(plan, FormalRotationDeadlineIntent.OperationId(journal, row.Id), previous);
    }

    internal static async Task ValidateNewAsync(WebApiDbContext db, GovernanceJournalId journal, GovernanceWriteIntent intent,
        Func<WebApiDbContext, CancellationToken, Task<DateTimeOffset>> clock, CancellationToken ct)
    {
        var (plan, schedule, _, _, _) = await RequirePreparedScopeAsync(db, journal, intent, clock, ct);
        var current = await ReadTargetsAsync(db, plan.EnvironmentId, schedule, ct);
        var plannedTargets = plan.Targets.Select(item => (item.NodeId, item.InstanceId, item.ProtocolSupported, item.LastLeaseUntil));
        if (!plannedTargets.SequenceEqual(current.Select(item => (item.NodeId, item.InstanceId, item.ProtocolSupported, item.LastLeaseUntil))))
            throw PostgresFormalRotationService.Changed();
    }

    internal static async Task ApplyAsync(WebApiDbContext db, GovernanceJournalId journal, GovernanceWriteIntent intent,
        Func<WebApiDbContext, CancellationToken, Task<DateTimeOffset>> clock, CancellationToken ct)
    {
        var (plan, _, credential, application, environment) = await RequirePreparedScopeAsync(db, journal, intent, clock, ct);
        CredentialRevocationMutation.RecordOperation(db, credential, application, intent.OperationId, plan.ActorId,
            FormalRotationDeadlineIntent.Reason, plan.EventTime);
        var fact = plan.Event(intent.OperationId);
        await CredentialRevocationMutation.AppendRevocationAsync(db, environment, fact, plan.ApplicationId, plan.ActorId,
            intent.OperationId, AuditAction, ct);
        db.AddRange(plan.Targets.Select(target => target.ToRow(intent.OperationId, plan.EnvironmentId, plan.EventTime)));
    }

    internal static async Task<bool> VerifyAsync(WebApiDbContext db, GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct)
    {
        var plan = FormalRotationDeadlineIntent.Read(intent);
        await ReadMatchingScheduleAsync(db, journal, intent, plan, ct);
        var fact = plan.Event(intent.OperationId);
        var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(plan.EnvironmentId, ct);
        if (!ledger.Events.Any(item => item.OperationId == intent.OperationId && item.EventHash == fact.EventHash) ||
            !ledger.Revocations.Any(item => item.OperationId == intent.OperationId && item.CredentialId == plan.CredentialId && item.Epoch == fact.Epoch) ||
            !await db.Set<SecurityOperation>().AnyAsync(item => item.Id == intent.OperationId && item.Kind == "CredentialRevoke" &&
                item.OrganizationId == plan.OrganizationId && item.ApplicationId == plan.ApplicationId && item.CredentialId == plan.CredentialId &&
                item.RequestedBy == plan.ActorId && item.Reason == FormalRotationDeadlineIntent.Reason && item.CommittedAt == plan.EventTime, ct) ||
            !await db.Set<SecurityOutboxMessage>().AnyAsync(item => item.Id == intent.OperationId && item.OperationId == intent.OperationId &&
                item.EnvironmentId == plan.EnvironmentId && item.SecurityGeneration == plan.SecurityGeneration &&
                item.Epoch == fact.Epoch && item.CreatedAt == plan.EventTime, ct)) return false;

        var targets = await db.Set<SecurityOperationTarget>().AsNoTracking().Where(item => item.OperationId == intent.OperationId &&
            item.EnvironmentId == plan.EnvironmentId).ToArrayAsync(ct);
        if (!plan.Targets.All(target => targets.Any(item => item.NodeId == target.NodeId && item.InstanceGuid == target.InstanceId &&
                item.ChallengeNonce == target.ChallengeNonce && item.ProtocolSupportedAtFreeze == target.ProtocolSupported &&
                item.CreatedAt == plan.EventTime && (target.LastLeaseUntil is null || item.LastLeaseUntil >= target.LastLeaseUntil))))
            return false;

        var audit = await db.Set<AuditLog>().Where(item => item.Action == AuditAction && item.UserId == plan.ActorId &&
                item.OrganizationId == plan.OrganizationId && item.ProjectId == plan.ProjectId && item.EnvironmentId == plan.EnvironmentId &&
                item.ResourceType == "credential" && item.ResourceId == plan.CredentialId.ToString() && item.CreatedAt == plan.EventTime)
            .Select(item => item.AfterJson).ToArrayAsync(ct);
        return audit.Any(item => FormalRotationMutation.AuditMatches(item, CredentialRevocationMutation.AuditFact(fact)));
    }

    private static async Task<(FormalRotationDeadlineIntent Plan, FormalRotationScheduleIntent Schedule, ApplicationCredential Credential,
        ApplicationRecord Application, EnvironmentRecord Environment)> RequirePreparedScopeAsync(WebApiDbContext db,
        GovernanceJournalId journal, GovernanceWriteIntent intent, Func<WebApiDbContext, CancellationToken, Task<DateTimeOffset>> clock,
        CancellationToken ct)
    {
        var plan = FormalRotationDeadlineIntent.Read(intent);
        var schedule = await ReadMatchingScheduleAsync(db, journal, intent, plan, ct);
        var environment = await db.Set<EnvironmentRecord>().FromSqlInterpolated(
            $"SELECT * FROM environments WHERE id={plan.EnvironmentId} FOR UPDATE").SingleAsync(ct);
        var credential = await db.Set<ApplicationCredential>().FromSqlInterpolated(
            $"SELECT * FROM application_credentials WHERE id={plan.CredentialId} FOR UPDATE").SingleAsync(ct);
        var application = await db.Set<ApplicationRecord>().FromSqlInterpolated(
            $"SELECT * FROM applications WHERE id={plan.ApplicationId} FOR UPDATE").SingleAsync(ct);
        var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(plan.EnvironmentId, ct);
        var now = await clock(db, ct);
        if (!FormalRotationJournalIntent.Instant(now) || now < plan.EventTime || environment.ProjectId != plan.ProjectId ||
            credential.ApplicationId != application.Id || application.OrganizationId != plan.OrganizationId ||
            credential.BindingMode != "EnvironmentBound" || credential.OrganizationId != plan.OrganizationId ||
            credential.ProjectId != plan.ProjectId || credential.EnvironmentId != plan.EnvironmentId ||
            ledger.OrganizationId != plan.OrganizationId || ledger.ProjectId != plan.ProjectId || ledger.SecurityGeneration != plan.SecurityGeneration ||
            ledger.Epoch != plan.PreviousEpoch || ledger.HeadHash != plan.PreviousHash ||
            ledger.Revocations.Any(item => item.CredentialId == credential.Id))
            throw FormalRotationJournalIntent.Invalid();
        return (plan, schedule, credential, application, environment);
    }

    private static async Task<FormalRotationScheduleIntent> ReadMatchingScheduleAsync(WebApiDbContext db, GovernanceJournalId journal,
        GovernanceWriteIntent intent, FormalRotationDeadlineIntent plan, CancellationToken ct)
    {
        var schedule = await FormalRotationMutation.ReadConfirmedScheduleAsync(db, journal, plan.ScheduleId, intent.Previous, ct);
        if (intent.OperationId != FormalRotationDeadlineIntent.OperationId(journal, plan.ScheduleId) ||
            schedule.CredentialId != plan.CredentialId || schedule.ApplicationId != plan.ApplicationId || schedule.ActorId != plan.ActorId ||
            schedule.OrganizationId != plan.OrganizationId || schedule.ProjectId != plan.ProjectId ||
            schedule.Window.EnvironmentId != plan.EnvironmentId || schedule.Window.HardDeadline != plan.HardDeadline ||
            !await db.Set<FormalCredentialRotation>().AnyAsync(item => item.Id == plan.ScheduleId && item.IntentHash == plan.ScheduleHash, ct))
            throw FormalRotationJournalIntent.Invalid();
        return schedule;
    }

    private static async Task<FormalRotationDeadlineTarget[]> ReadTargetsAsync(WebApiDbContext db, Guid environmentId,
        FormalRotationScheduleIntent schedule, CancellationToken ct)
    {
        var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(node => node.EnvironmentId == environmentId && node.Enabled).ToArrayAsync(ct);
        var possibleLeases = await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().Where(item => item.EnvironmentId == environmentId)
            .GroupBy(item => new { item.NodeId, item.InstanceId })
            .Select(group => new { group.Key.NodeId, group.Key.InstanceId, LastUntil = group.Max(item => item.FreshUntil) }).ToArrayAsync(ct);
        var latest = possibleLeases.ToDictionary(item => (item.NodeId, item.InstanceId), item => (DateTimeOffset?)item.LastUntil);
        var instances = schedule.Targets.ToDictionary(item => (item.NodeId, item.InstanceId), _ => true);
        foreach (var lease in possibleLeases.Where(item => item.LastUntil >= schedule.RecordedAt))
            instances.TryAdd((lease.NodeId, lease.InstanceId), true);
        foreach (var node in nodes)
        {
            var instance = Guid.TryParse(node.InstanceId, out var current) ? current : Guid.Empty;
            instances[(node.Id, instance)] = instance != Guid.Empty && SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(node, "2.4");
        }
        return instances.OrderBy(item => item.Key).Select(item => new FormalRotationDeadlineTarget(item.Key.NodeId, item.Key.InstanceId,
            Guid.NewGuid(), item.Value, latest.GetValueOrDefault(item.Key))).ToArray();
    }
}

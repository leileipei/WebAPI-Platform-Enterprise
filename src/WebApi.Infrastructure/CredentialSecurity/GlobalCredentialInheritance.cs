using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Runtime;

namespace WebApi.Infrastructure.CredentialSecurity;

internal static class GlobalCredentialInheritance
{
    private sealed record Origin(Guid OperationId, Guid CredentialId, Guid ApplicationId, DateTimeOffset RevokedAt);

    private static IQueryable<Origin> Applicable(WebApiDbContext db, Guid organizationId, Guid projectId) =>
        from operation in GlobalCredentialPublicationGuard.GlobalOperations(db)
        join credential in db.Set<ApplicationCredential>() on operation.CredentialId equals (Guid?)credential.Id
        join application in db.Set<ApplicationRecord>() on operation.ApplicationId equals (Guid?)application.Id
        where operation.OrganizationId == organizationId &&
            (credential.ProjectId == null || credential.ProjectId == projectId) &&
            (application.ProjectId == null || application.ProjectId == projectId)
        orderby operation.CommittedAt, operation.Id
        select new Origin(operation.Id, credential.Id, application.Id, operation.CommittedAt);

    internal static Task<bool> HasApplicableAsync(WebApiDbContext db, Guid organizationId, Guid projectId, CancellationToken ct) =>
        Applicable(db, organizationId, projectId).AnyAsync(ct);

    internal static async Task InitializeNewAsync(WebApiDbContext db, Guid environmentId, ScopeRef scope, ActorContext actor, CancellationToken ct)
    {
        var facts = await Applicable(db, scope.OrganizationId, scope.ProjectId!.Value).ToArrayAsync(ct);
        if (facts.Length == 0) return;

        // The creation command has saved its environment, fresh-origin row and
        // audit in the same transaction. The database only permits a new genesis.
        var environment = await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == environmentId, ct);
        db.Add(new EnvironmentSecurityState { OrganizationId = scope.OrganizationId, ProjectId = environment.ProjectId,
            EnvironmentId = environmentId, UpdatedAt = await SecurityDatabaseClock.ObserveAsync(db, ct) });
        await db.SaveChangesAsync(ct);
        await ApplyAsync(db, environment, scope.OrganizationId, actor, facts, ct);
        await db.SaveChangesAsync(ct);
    }

    internal static async Task ReactivateAsync(WebApiDbContext db, EnvironmentRecord environment, Guid organizationId,
        ActorContext actor, ICredentialArchivedIngressWitness? witness, CancellationToken ct)
    {
        var facts = await Applicable(db, organizationId, environment.ProjectId).ToArrayAsync(ct);
        if (facts.Length == 0) return;

        var before = await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(item => item.Id == environment.Id, ct);
        var proof = await CredentialArchivedIngress.RequireAsync(db, before, organizationId, witness,
            await SecurityDatabaseClock.ObserveAsync(db, ct), ct);
        await ApplyAsync(db, environment, organizationId, actor, facts, ct);
        var now = await SecurityDatabaseClock.ObserveAsync(db, ct);
        var current = await CredentialArchivedIngress.RequireAsync(db, before, organizationId, witness, now, ct);
        if (current.Scope != proof.Scope || current.ProofHash != proof.ProofHash) throw ArchiveUnverified();

        // Preserve the prior release/version and immutable payload for audit.
        // The next governed publication must establish a new desired pointer.
        environment.DesiredConfigVersion = null;
        var oldNodes = await db.Set<GatewayNode>().Where(node => node.EnvironmentId == environment.Id).Select(node => node.Id).ToArrayAsync(ct);
        foreach (var node in oldNodes)
            db.Add(new GatewayNodeEvent { GatewayNodeId = node, EventType = RuntimeEnvironmentAccess.ArchiveRetiredNodeEvent, Message = "封存入口已退役" });
        db.Add(new AuditLog { OrganizationId = organizationId, ProjectId = environment.ProjectId, EnvironmentId = environment.Id,
            UserId = actor.UserId, TraceId = actor.TraceId, Action = "environment.archive.reactivate", ResourceType = "environment",
            ResourceId = environment.Id.ToString(), CreatedAt = now, AfterJson = JsonSerializer.Serialize(new { proof.ProofHash }) });
    }

    internal static async Task RequireSafeParentActivationAsync(WebApiDbContext db, Guid organizationId, Guid? projectId, CancellationToken ct)
    {
        var activeProjects = await (from environment in db.Set<EnvironmentRecord>().AsNoTracking()
                                    join project in db.Set<Project>().AsNoTracking() on environment.ProjectId equals project.Id
                                    where project.OrganizationId == organizationId && (projectId == null || project.Id == projectId) &&
                                        environment.Status == "Active"
                                    select project.Id).Distinct().ToArrayAsync(ct);
        foreach (var id in activeProjects)
            if (await Applicable(db, organizationId, id).AnyAsync(ct)) throw ArchiveUnverified();
    }

    private static async Task ApplyAsync(WebApiDbContext db, EnvironmentRecord environment, Guid organizationId,
        ActorContext actor, Origin[] facts, CancellationToken ct)
    {
        var reader = new SecurityLedgerReader(db);
        var ledger = await ReadAsync(reader, environment.Id, ct);
        if (ledger.OrganizationId != organizationId || ledger.ProjectId != environment.ProjectId)
            throw GlobalCredentialPublicationGuard.InheritanceRequired();

        var state = await db.Set<EnvironmentSecurityState>().SingleAsync(x => x.EnvironmentId == environment.Id, ct);
        var existing = ledger.Revocations.ToDictionary(x => x.CredentialId);
        var now = await SecurityDatabaseClock.ObserveAsync(db, ct);
        var changed = false;
        foreach (var fact in facts)
        {
            if (existing.TryGetValue(fact.CredentialId, out var tombstone))
            {
                if (tombstone.OperationId != fact.OperationId || tombstone.ApplicationId != fact.ApplicationId)
                    throw GlobalCredentialPublicationGuard.InheritanceRequired();
                continue;
            }
            if (fact.RevokedAt > now) throw GlobalCredentialPublicationGuard.InheritanceRequired();

            await RequireOriginAsync(db, reader, fact, ct);
            var item = Append(db, state, fact.OperationId, CredentialRevocationEvents.Inherited, fact.CredentialId, null, fact.RevokedAt, now);
            db.Add(new CredentialRevocation { EnvironmentId = environment.Id, OrganizationId = organizationId, ProjectId = environment.ProjectId,
                CredentialId = fact.CredentialId, ApplicationId = fact.ApplicationId, SecurityGeneration = state.SecurityGeneration,
                Epoch = item.Epoch, OperationId = fact.OperationId, RevokedAt = fact.RevokedAt });
            db.Add(new AuditLog { OrganizationId = organizationId, ProjectId = environment.ProjectId, EnvironmentId = environment.Id,
                UserId = actor.UserId, TraceId = actor.TraceId, Action = "credential.revoke.inherit", ResourceType = "credential",
                ResourceId = fact.CredentialId.ToString(), CreatedAt = now,
                AfterJson = JsonSerializer.Serialize(new { fact.OperationId, item.Epoch, item.EventHash }) });
            changed = true;
        }

        if (state.MinimumSnapshotSchema != "2.4")
        {
            var operation = new SecurityOperation { OrganizationId = organizationId, RequestedBy = actor.UserId,
                Kind = "EnvironmentPolicyCutover", Reason = "global_credential_revocation_inheritance", CommittedAt = now };
            db.Add(operation);
            var policy = Encoding.UTF8.GetString(CanonicalJson.Serialize(new EnvironmentSecurityPolicy(state.RequireEnvironmentCredentials, "2.4")));
            Append(db, state, operation.Id, "EnvironmentPolicyChanged", null, policy, now, now);
            state.MinimumSnapshotSchema = "2.4";
            changed = true;
        }
        if (changed) environment.AuthorizationRevision = checked(environment.AuthorizationRevision + 1);
    }

    private static async Task RequireOriginAsync(WebApiDbContext db, SecurityLedgerReader reader, Origin fact, CancellationToken ct)
    {
        var source = await db.Set<SecurityEvent>().AsNoTracking().Where(item => item.OperationId == fact.OperationId &&
                (item.EventType == CredentialRevocationEvents.Original || item.EventType == CredentialRevocationEvents.Archived))
            .OrderBy(item => item.EnvironmentId).FirstOrDefaultAsync(ct);
        if (source is null) throw GlobalCredentialPublicationGuard.InheritanceRequired();
        var ledger = await ReadAsync(reader, source.EnvironmentId, ct);
        if (!ledger.Revocations.Any(item => item.OperationId == fact.OperationId && item.CredentialId == fact.CredentialId &&
                item.ApplicationId == fact.ApplicationId && item.RevokedAt == fact.RevokedAt && item.Epoch == source.Epoch))
            throw GlobalCredentialPublicationGuard.InheritanceRequired();
    }

    private static SecurityEvent Append(WebApiDbContext db, EnvironmentSecurityState state, Guid operationId, string kind,
        Guid? credentialId, string? material, DateTimeOffset occurredAt, DateTimeOffset appendedAt)
    {
        var item = new SecurityEvent { OrganizationId = state.OrganizationId, ProjectId = state.ProjectId, EnvironmentId = state.EnvironmentId,
            SecurityGeneration = state.SecurityGeneration, Epoch = checked(state.Epoch + 1), PreviousHash = state.HeadHash,
            OperationId = operationId, EventType = kind, CredentialId = credentialId, PolicyJson = material, OccurredAt = occurredAt };
        item.EventHash = SecurityEventHash.Compute(item);
        state.Epoch = item.Epoch;
        state.HeadHash = item.EventHash;
        state.Revision = checked(state.Revision + 1);
        state.UpdatedAt = appendedAt;
        db.AddRange(item, new SecurityOutboxMessage { EnvironmentId = item.EnvironmentId, SecurityGeneration = item.SecurityGeneration,
            Epoch = item.Epoch, OperationId = operationId, CreatedAt = appendedAt });
        return item;
    }

    private static async Task<SecurityLedgerMaterial> ReadAsync(SecurityLedgerReader reader, Guid environmentId, CancellationToken ct)
    {
        try { return await reader.ReadCompleteLockedAsync(environmentId, ct); }
        catch (InvalidOperationException) { throw GlobalCredentialPublicationGuard.InheritanceRequired(); }
    }

    internal static string SealDigest(CredentialArchivedIngressSeal seal) =>
        Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(new { seal.Scope, seal.ProofHash })));

    private static ApiException ArchiveUnverified() => new(409, "credential_revocation_archive_unverified",
        "重新启用前须核验旧入口隔离及环境撤销记录。");
}

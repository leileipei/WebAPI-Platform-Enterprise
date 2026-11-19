using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.CredentialSecurity;

internal static class GlobalCredentialPublicationGuard
{
    internal static Task<bool> HasLocalRevocationAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct)
    {
        var localOperations = LocalOperationIds(db, environmentId);
        return GlobalOperations(db).AnyAsync(operation => localOperations.Contains(operation.Id), ct);
    }

    internal static Task<bool> RequiresGovernance24Async(WebApiDbContext db, Guid environmentId, CancellationToken ct)
    {
        var localOperations = LocalOperationIds(db, environmentId);
        return db.Set<FormalCredentialRotation>().Where(item => item.EnvironmentId == environmentId).Select(item => item.Id)
            .Concat(GlobalOperations(db).Where(operation => localOperations.Contains(operation.Id)).Select(operation => operation.Id))
            .Concat(db.Set<EnvironmentRecord>().Where(item => item.Id == environmentId && item.RequireEnvironmentCredential).Select(item => item.Id))
            .Concat(db.Set<EnvironmentSecurityState>().Where(item => item.EnvironmentId == environmentId &&
                (item.RequireEnvironmentCredentials || item.MinimumSnapshotSchema == "2.3" || item.MinimumSnapshotSchema == "2.4"))
                .Select(item => item.EnvironmentId))
            .Concat(db.Set<EnvironmentSecurityInitialization>().Where(item => item.EnvironmentId == environmentId && item.State == "Governed")
                .Select(item => item.EnvironmentId)).AnyAsync(ct);
    }

    internal static async Task RequireApplicationInheritanceAsync(WebApiDbContext db, ScopeRef target, Guid applicationId,
        CancellationToken ct)
    {
        var required = await GlobalOperations(db)
            .Where(operation => operation.OrganizationId == target.OrganizationId && operation.ApplicationId == applicationId)
            .Select(operation => new { operation.Id, CredentialId = operation.CredentialId!.Value }).ToArrayAsync(ct);
        if (required.Length == 0) return;

        if (target.EnvironmentId is not Guid environmentId || target.ProjectId is not Guid projectId)
            throw InheritanceRequired();
        var ledger = await ReadLedgerAsync(db, target.OrganizationId, projectId, environmentId, ct);
        var tombstones = TombstoneIdentities(ledger);
        if (required.Any(fact => !tombstones.Contains((fact.Id, fact.CredentialId, applicationId))))
            throw InheritanceRequired();
    }

    // The caller holds the governance writer fence through the desired config update.
    internal static async Task<string> ValidateAsync(WebApiDbContext db, EnvironmentRecord environment,
        Guid organizationId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var globalOperations = GlobalOperations(db);
        var hasRotation = await db.Set<FormalCredentialRotation>().AnyAsync(item => item.EnvironmentId == environment.Id, ct);
        if (!hasRotation && !await globalOperations.AnyAsync(ct))
        {
            // The compiler or historical reader has already validated the payload.
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.GetProperty("schemaVersion").GetString()!;
        }

        RuntimeSnapshot snapshot;
        try { snapshot = SnapshotValidator.ParsePayload(payload.Span, environment.Id); }
        catch (ArgumentException) { throw InheritanceRequired(); }

        // SnapshotValidator verifies that JWT application mappings also have runtime application entries.
        var applications = snapshot.Applications.Select(app => app.Id).ToArray();
        var credentials = new Dictionary<Guid, (Guid ApplicationId, RuntimeCredential Credential)>();
        foreach (var app in snapshot.Applications)
        {
            if (app.Id == Guid.Empty) throw InheritanceRequired();
            foreach (var credential in app.Credentials)
                if (credential.Id == Guid.Empty || !credentials.TryAdd(credential.Id, (app.Id, credential)))
                    throw InheritanceRequired();
        }
        var credentialIds = credentials.Keys.Select(id => (Guid?)id).ToArray();
        var localOperations = LocalOperationIds(db, environment.Id);
        var facts = await (from operation in globalOperations
                           join app in db.Set<ApplicationRecord>().AsNoTracking()
                               on new { operation.ApplicationId, operation.OrganizationId }
                               equals new { ApplicationId = (Guid?)app.Id, app.OrganizationId }
                           where applications.Contains(app.Id) || credentialIds.Contains(operation.CredentialId) ||
                               localOperations.Contains(operation.Id)
                           select new { OperationId = operation.Id, CredentialId = operation.CredentialId!.Value, ApplicationId = app.Id,
                               app.OrganizationId, app.ProjectId }).ToArrayAsync(ct);
        if (!hasRotation && facts.Length == 0) return snapshot.SchemaVersion;

        if (snapshot.SchemaVersion != "2.4" || !payload.Span.SequenceEqual(CanonicalJson.Serialize(snapshot)))
            throw InheritanceRequired();

        var ledger = await ReadLedgerAsync(db, organizationId, environment.ProjectId, environment.Id, ct);

        if (snapshot.RequiredSecurityGeneration != ledger.SecurityGeneration || snapshot.RequiredSecurityEpoch != ledger.Epoch ||
            snapshot.RequireEnvironmentCredentials != ledger.RequireEnvironmentCredentials || snapshot.MinimumSnapshotSchema != ledger.MinimumSnapshotSchema)
            throw InheritanceRequired();

        var tombstones = TombstoneIdentities(ledger);
        foreach (var fact in facts)
        {
            if (fact.OrganizationId != organizationId || fact.ProjectId is Guid projectId && projectId != environment.ProjectId ||
                !tombstones.Contains((fact.OperationId, fact.CredentialId, fact.ApplicationId)))
                throw InheritanceRequired();

            if (credentials.TryGetValue(fact.CredentialId, out var published) &&
                (published.ApplicationId != fact.ApplicationId || published.Credential.BindingMode != "LegacyGlobal"))
                throw InheritanceRequired();
        }
        return snapshot.SchemaVersion;
    }

    private static async Task<SecurityLedgerMaterial> ReadLedgerAsync(WebApiDbContext db, Guid organizationId,
        Guid projectId, Guid environmentId, CancellationToken ct)
    {
        SecurityLedgerMaterial ledger;
        try { ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(environmentId, ct); }
        catch (InvalidOperationException) { throw InheritanceRequired(); }
        if (ledger.OrganizationId != organizationId || ledger.ProjectId != projectId || ledger.EnvironmentId != environmentId)
            throw InheritanceRequired();
        return ledger;
    }

    private static HashSet<(Guid OperationId, Guid CredentialId, Guid ApplicationId)> TombstoneIdentities(SecurityLedgerMaterial ledger) =>
        ledger.Revocations.Select(item => (item.OperationId, item.CredentialId, item.ApplicationId)).ToHashSet();

    internal static IQueryable<SecurityOperation> GlobalOperations(WebApiDbContext db) =>
        from operation in db.Set<SecurityOperation>().AsNoTracking()
        join credential in db.Set<ApplicationCredential>().AsNoTracking()
            on new { operation.CredentialId, operation.ApplicationId, operation.OrganizationId }
            equals new { CredentialId = (Guid?)credential.Id, ApplicationId = (Guid?)credential.ApplicationId, credential.OrganizationId }
        where operation.Kind == "CredentialRevoke" && credential.BindingMode == "LegacyGlobal"
        select operation;

    // Local membership persists after the application is removed. Its ledger event
    // also identifies an affected environment with an incomplete tombstone set.
    private static IQueryable<Guid> LocalOperationIds(WebApiDbContext db, Guid environmentId) =>
        db.Set<CredentialRevocation>().Where(item => item.EnvironmentId == environmentId).Select(item => item.OperationId)
            .Union(db.Set<SecurityEvent>().Where(item => item.EnvironmentId == environmentId).Select(item => item.OperationId));

    internal static ApiException InheritanceRequired() => new(409, "global_revocation_inheritance_required",
        "目标环境须先完整继承当前安全账本中的全局凭证撤销事实。");
}

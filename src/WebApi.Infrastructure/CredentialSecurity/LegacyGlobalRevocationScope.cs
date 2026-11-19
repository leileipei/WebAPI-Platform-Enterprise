using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed record CredentialRevocationHistoryEnvironment(Guid EnvironmentId, Guid ProjectId);

public sealed record CredentialRevocationHistoryArtifact(string Kind, Guid EnvironmentId, Guid RecordId,
    long? ConfigVersion, string Hash);

public sealed record CredentialRevocationHistoryScope(Guid OrganizationId, Guid? ProjectId, Guid ApplicationId,
    Guid CredentialId, IReadOnlyList<CredentialRevocationHistoryEnvironment> Environments,
    IReadOnlyList<CredentialRevocationHistoryArtifact> Artifacts);

public interface ICredentialRevocationHistory
{
    // Compare the complete inventory with independently verified immutable
    // history. Agreement with hashes stored in the same database is insufficient.
    // The caller holds the writer fence. This method must not perform I/O.
    bool IsVerified(CredentialRevocationHistoryScope scope);
}

internal static class LegacyGlobalRevocationScope
{
    private static readonly JsonSerializerOptions FrozenJson = new(CanonicalJson.Options)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    internal static async Task<IReadOnlyList<Guid>> ReadAsync(WebApiDbContext db, ApplicationCredential credential,
        ApplicationRecord application, Guid? expectedEnvironment, ActorContext actor, ICredentialRevocationHistory history,
        CancellationToken ct, bool allowArchive = false)
    {
        if (db.Database.CurrentTransaction is null || !await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_locks
                WHERE pid = pg_catalog.pg_backend_pid() AND locktype = 'advisory' AND mode = 'ExclusiveLock'
                    AND classid = 0 AND objid = 8901202 AND objsubid = 1 AND granted
            ) AS "Value"
            """).SingleAsync(ct))
            throw new InvalidOperationException("Global credential scope requires the governance writer fence.");

        var projectId = credential.ProjectId ?? application.ProjectId;
        var auth = new AuthorizationService(db);
        await auth.RequireAsync(actor, "credential.revoke",
            new("credential", credential.Id, new(credential.OrganizationId, projectId)), ct);
        if (credential.BindingMode != "LegacyGlobal" || credential.EnvironmentId is not null || expectedEnvironment is not null ||
            credential.Id == Guid.Empty || application.Id == Guid.Empty || credential.ApplicationId != application.Id ||
            credential.OrganizationId == Guid.Empty || credential.OrganizationId != application.OrganizationId ||
            credential.ProjectId is Guid credentialProject && application.ProjectId is Guid appProject && credentialProject != appProject)
            throw Unknown();

        var environmentIds = await (from env in db.Set<EnvironmentRecord>().AsNoTracking()
                                    join project in db.Set<Project>().AsNoTracking() on env.ProjectId equals project.Id
                                    where project.OrganizationId == credential.OrganizationId &&
                                        (projectId == null || project.Id == projectId)
                                    orderby env.Id
                                    select env.Id).ToArrayAsync(ct);
        var environments = await db.Set<EnvironmentRecord>()
            .FromSqlInterpolated($"SELECT * FROM environments WHERE id = ANY({environmentIds}) ORDER BY id FOR UPDATE")
            .AsNoTracking().ToArrayAsync(ct);
        if (environments.Length != environmentIds.Length) throw Unknown();
        var byId = environments.ToDictionary(env => env.Id);
        var affected = (await db.Set<ApplicationApiPermission>().AsNoTracking()
            .Where(grant => grant.ApplicationId == application.Id).Select(grant => grant.EnvironmentId)
            .Distinct().ToArrayAsync(ct)).ToHashSet();
        if (affected.Any(id => !byId.ContainsKey(id))) throw Unknown();

        var mappings = await (from binding in db.Set<RoutePolicyBinding>().AsNoTracking()
                              join route in db.Set<ApiRoute>().AsNoTracking() on binding.RouteId equals route.Id
                              join policy in db.Set<Policy>().AsNoTracking() on binding.PolicyId equals policy.Id
                              where policy.Type == "authentication" &&
                                  (policy.OrganizationId == credential.OrganizationId || environmentIds.Contains(route.EnvironmentId))
                              select new { route.EnvironmentId, policy.OrganizationId, policy.ProjectId, policy.Type, policy.Config })
            .Distinct().ToArrayAsync(ct);
        foreach (var mapping in mappings)
        {
            if (!ReferencesApplication(mapping.Type, mapping.Config, application.Id)) continue;
            if (!byId.TryGetValue(mapping.EnvironmentId, out var env) || mapping.OrganizationId != credential.OrganizationId ||
                mapping.ProjectId is Guid policyProject && policyProject != env.ProjectId)
                throw Unknown();
            affected.Add(mapping.EnvironmentId);
        }

        var releases = await db.Set<ReleaseRecord>().AsNoTracking().Where(r => environmentIds.Contains(r.EnvironmentId))
            .OrderBy(r => r.Id).Select(r => new { r.Id, r.EnvironmentId, r.FromConfigVersion, r.ToConfigVersion,
                r.BaselineConfigVersion }).ToArrayAsync(ct);
        var versions = await db.Set<GatewayConfigVersion>().AsNoTracking().Where(v => environmentIds.Contains(v.EnvironmentId))
            .OrderBy(v => v.Id).ToArrayAsync(ct);
        var available = versions.Select(v => (v.EnvironmentId, v.VersionNo)).ToHashSet();
        var nodeVersions = await db.Set<GatewayNode>().AsNoTracking().Where(n => environmentIds.Contains(n.EnvironmentId))
            .Select(n => new { n.EnvironmentId, n.CurrentConfigVersion }).ToArrayAsync(ct);
        if (versions.Any(v => v.VersionNo <= 0) ||
            environments.Any(env => env.DesiredConfigVersion is long version && !available.Contains((env.Id, version))) ||
            nodeVersions.Any(n => n.CurrentConfigVersion < 0 || n.CurrentConfigVersion > 0 && !available.Contains((n.EnvironmentId, n.CurrentConfigVersion))) ||
            releases.Any(r => new[] { r.FromConfigVersion, r.ToConfigVersion, r.BaselineConfigVersion }
                .Any(version => version < 0 || version > 0 && !available.Contains((r.EnvironmentId, version)))))
            throw Unknown();

        var artifacts = new List<CredentialRevocationHistoryArtifact>();
        foreach (var release in releases)
        {
            var env = byId[release.EnvironmentId];
            var bytes = await db.Set<ReleaseRecord>().AsNoTracking().Where(r => r.Id == release.Id)
                .Select(r => r.CandidateBytes).SingleOrDefaultAsync(ct);
            var candidate = ReadFrozen(bytes, env, credential.OrganizationId);
            if (CandidateUsesApplication(candidate, application.Id, credential.Id)) affected.Add(env.Id);
            artifacts.Add(new("candidate", env.Id, release.Id, null,
                Convert.ToHexStringLower(SHA256.HashData(bytes!))));
        }

        var historical = new HistoricalSnapshotService(db, new ScopeResolver(db));
        foreach (var version in versions)
        {
            HistoricalArtifact artifact;
            try { artifact = await historical.ReadAsync(version.EnvironmentId, version.VersionNo, ct); }
            catch (ApiException error) when (error.Status is 404 or 422) { throw Unknown(); }
            catch (ArgumentException) { throw Unknown(); }
            if (!artifact.Payload.AsSpan().SequenceEqual(CanonicalJson.Serialize(artifact.Snapshot)))
                throw Unknown();
            if (SnapshotUsesApplication(artifact.Snapshot, application.Id, credential.Id)) affected.Add(version.EnvironmentId);
            artifacts.Add(new("snapshot", version.EnvironmentId, version.Id, version.VersionNo, artifact.Hash));
        }

        if (affected.Count == 0) throw Unknown();
        var result = affected.Order().ToArray();
        foreach (var envId in result)
        {
            var env = byId[envId];
            // A disabled row may enter the candidate scope only when the
            // mutation can subsequently verify a distinct archive seal.
            if (env.Status != "Active" && !(allowArchive && env.Status == "Disabled")) throw Unknown();
            try
            {
                var target = new ResourceRef("credential", credential.Id, new(credential.OrganizationId, env.ProjectId, envId));
                if (env.Status == "Disabled") await auth.RequireArchivedCredentialRevocationAsync(actor, target, ct);
                else await auth.RequireAsync(actor, "credential.revoke", target, ct);
            }
            catch (ApiException error) when (error.Code == "inactive_scope") { throw Unknown(); }
        }

        var scope = new CredentialRevocationHistoryScope(credential.OrganizationId, projectId, application.Id, credential.Id,
            environments.OrderBy(env => env.Id).Select(env => new CredentialRevocationHistoryEnvironment(env.Id, env.ProjectId)).ToArray(),
            artifacts.ToArray());
        if (!history.IsVerified(scope)) throw Unknown();
        return result;
    }

    private static FrozenReleaseCandidate ReadFrozen(byte[]? bytes, EnvironmentRecord env, Guid organizationId)
    {
        if (bytes is not { Length: > 0 }) throw Unknown();
        try
        {
            var candidate = JsonSerializer.Deserialize<FrozenReleaseCandidate>(bytes, FrozenJson);
            if (candidate is null || candidate.EnvironmentId != env.Id || candidate.ProjectId != env.ProjectId ||
                candidate.OrganizationId != organizationId || candidate.Applications is null || candidate.Policies is null ||
                !bytes.AsSpan().SequenceEqual(CanonicalJson.Serialize(candidate)))
                throw Unknown();
            foreach (var app in candidate.Applications)
                if (app?.Application is null || app.Application.OrganizationId != organizationId ||
                    app.Application.ProjectId is Guid projectId && projectId != env.ProjectId)
                    throw Unknown();
            return candidate;
        }
        catch (JsonException) { throw Unknown(); }
    }

    private static bool CandidateUsesApplication(FrozenReleaseCandidate candidate, Guid applicationId, Guid credentialId)
    {
        var applications = new HashSet<Guid>();
        var credentials = new HashSet<Guid>();
        var found = false;
        foreach (var item in candidate.Applications)
        {
            var id = item.Application.Id;
            if (id == Guid.Empty || !applications.Add(id) || item.Credentials is null) throw Unknown();
            foreach (var credential in item.Credentials)
                found |= CheckCredential(credential?.Id, id, credentialId, applicationId, credentials);
        }
        foreach (var policy in candidate.Policies)
        {
            if (policy is null) throw Unknown();
            try
            {
                var normalized = PolicyConfigurationValidator.Normalize(policy.Type, policy.Config);
                found |= ReferencesApplication(policy.Type, normalized, applicationId);
            }
            catch (ApiException error) when (error.Status == 422) { throw Unknown(); }
        }
        return found;
    }

    private static bool SnapshotUsesApplication(RuntimeSnapshot snapshot, Guid applicationId, Guid credentialId)
    {
        var credentials = new HashSet<Guid>();
        var found = false;
        foreach (var app in snapshot.Applications)
        {
            if (app.Id == Guid.Empty || app.Credentials is null) throw Unknown();
            foreach (var credential in app.Credentials)
            {
                var matches = CheckCredential(credential?.Id, app.Id, credentialId, applicationId, credentials);
                if (matches && (credential!.EnvironmentId is not null ||
                    credential.BindingMode is not (null or "LegacyGlobal")))
                    throw Unknown();
                found |= matches;
            }
        }
        foreach (var policy in snapshot.Policies)
            found |= ReferencesApplication(policy.Type, policy.Config, applicationId);
        return found;
    }

    private static bool CheckCredential(Guid? credential, Guid owner, Guid expectedCredential, Guid expectedOwner,
        HashSet<Guid> seen)
    {
        if (credential is not Guid id || id == Guid.Empty || !seen.Add(id) ||
            id == expectedCredential && owner != expectedOwner)
            throw Unknown();
        return id == expectedCredential;
    }

    private static bool ReferencesApplication(string type, string config, Guid application)
    {
        if (string.IsNullOrEmpty(type) || config is null) throw Unknown();
        try { return JwtApplicationBindingService.ReferencedApplicationIds(type, config).Contains(application); }
        catch (ApiException error) when (error.Status == 422) { throw Unknown(); }
    }

    internal static ApiException Unknown() =>
        new(409, "credential_revocation_scope_unknown", "凭证的即时撤销范围尚未核验。");
}

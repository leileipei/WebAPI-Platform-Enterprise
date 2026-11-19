using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Runtime;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed record GovernanceDeliverySettings(bool RequireIndependentContinuity)
{
    public static GovernanceDeliverySettings Read(string? enabled) => new(
        enabled is not null && (!bool.TryParse(enabled, out var configured) || configured));
}

internal static class GovernanceSnapshotDelivery
{
    internal static bool RequiresFence(IGovernanceRecoveryContinuity? continuity, GovernanceDeliverySettings? settings) =>
        continuity is not null || settings?.RequireIndependentContinuity == true;

    internal static async Task<bool> RequireAsync(WebApiDbContext db, Guid environmentId, DesiredConfigResponse desired,
        IGovernanceRecoveryContinuity? continuity, GovernanceDeliverySettings? settings, CancellationToken ct, bool published = false,
        PublishSettings? publication = null)
    {
        var guarded = await GlobalCredentialPublicationGuard.RequiresGovernance24Async(db, environmentId, ct);
        if (continuity is null)
        {
            if (guarded || settings?.RequireIndependentContinuity == true ||
                !await LegacyEnvironmentCompatibility.IsVerifiedAsync(db, environmentId, ct)) throw Unavailable();
            var legacy = RedisSnapshotStore.ReadArtifact(environmentId, desired.Envelope, desired.Payload, allowGovernance24: false);
            await RequireCredentialScopeAsync(db, legacy, ct);
            return false;
        }

        GovernanceContinuity verified;
        try { verified = await continuity.RequireAsync(db, environmentId, ct); }
        catch (Exception error) when (error is IOException or InvalidDataException) { throw Unavailable(); }
        var ledger = verified.Ledger;
        if (ledger.EnvironmentId != environmentId) throw Unavailable();
        var snapshot = RedisSnapshotStore.ReadArtifact(environmentId, desired.Envelope, desired.Payload, allowGovernance24: true);
        await RequireCredentialScopeAsync(db, snapshot, ct);
        if (snapshot.SchemaVersion != "2.4")
        {
            if (guarded || ledger.RequireEnvironmentCredentials || ledger.Revocations.Count > 0 || ledger.MinimumSnapshotSchema is "2.3" or "2.4" ||
                !await LegacyEnvironmentCompatibility.IsVerifiedAsync(db, environmentId, ct)) throw Unavailable();
            return false;
        }
        if (snapshot.RequiredSecurityGeneration != ledger.SecurityGeneration || snapshot.RequiredSecurityEpoch is not long epoch ||
            (published ? epoch > ledger.Epoch : epoch != ledger.Epoch) ||
            snapshot.RequireEnvironmentCredentials != ledger.RequireEnvironmentCredentials || snapshot.MinimumSnapshotSchema != ledger.MinimumSnapshotSchema ||
            !desired.Payload.AsSpan().SequenceEqual(CanonicalJson.Serialize(snapshot))) throw Unavailable();
        var frozenPolicy = SecurityPolicyRules.AtEpoch(ledger, epoch);
        if (frozenPolicy.MinimumSnapshotSchema != snapshot.MinimumSnapshotSchema ||
            frozenPolicy.RequireEnvironmentCredentials != snapshot.RequireEnvironmentCredentials) throw Unavailable();
        if (!published) await RequireFrozenPublicationAsync(db, desired.Envelope.ReleaseId, ledger, publication ?? new(), ct);
        return true;
    }

    private static async Task RequireFrozenPublicationAsync(WebApiDbContext db, Guid releaseId, SecurityLedgerMaterial ledger,
        PublishSettings publication, CancellationToken ct)
    {
        var bytes = await db.Set<ReleaseRecord>().AsNoTracking().Where(item => item.Id == releaseId &&
            item.EnvironmentId == ledger.EnvironmentId && item.Status == "Publishing").Select(item => item.CandidateBytes).SingleOrDefaultAsync(ct);
        var candidate = bytes is null ? null : JsonSerializer.Deserialize<FrozenReleaseCandidate>(bytes, CanonicalJson.Options);
        if (candidate is null || candidate.EnvironmentId != ledger.EnvironmentId || candidate.OrganizationId != ledger.OrganizationId ||
            candidate.ProjectId != ledger.ProjectId || candidate.Governance is not { } frozen ||
            frozen.SecurityGeneration != ledger.SecurityGeneration || frozen.Epoch != ledger.Epoch || frozen.HeadHash != ledger.HeadHash ||
            frozen.Targets is not { Count: >= 2 }) throw Unavailable();

        var targets = await db.Set<ReleaseTarget>().AsNoTracking().Where(item => item.ReleaseId == releaseId)
            .OrderBy(item => item.NodeId).ToArrayAsync(ct);
        var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(item => item.EnvironmentId == ledger.EnvironmentId && item.Enabled)
            .OrderBy(item => item.Id).ToArrayAsync(ct);
        var heartbeatCutoff = (await SecurityDatabaseClock.ObserveAsync(db, ct)).AddSeconds(-publication.HeartbeatGraceSeconds);
        if (!frozen.Targets.Select(item => (item.NodeId, item.InstanceId)).SequenceEqual(targets.Select(item => (item.NodeId, item.InstanceId))) ||
            !frozen.Targets.Select(item => (item.NodeId, item.InstanceId)).SequenceEqual(nodes.Select(item => (item.Id, item.InstanceId))) ||
            nodes.Zip(frozen.Targets).Any(item => item.First.LastHeartbeatAt is not DateTimeOffset heartbeat || heartbeat < heartbeatCutoff ||
                !SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(item.First, "2.4") ||
                !SnapshotSchemaCapabilities.Read(item.First).SequenceEqual(item.Second.SupportedSnapshotSchemas)))
            throw Unavailable();

        var routeIds = frozen.Routes.Select(item => item.RouteId).ToArray();
        var routes = await db.Set<ApiRoute>().AsNoTracking().Where(item => item.EnvironmentId == ledger.EnvironmentId && routeIds.Contains(item.Id))
            .OrderBy(item => item.Id).Select(item => new FrozenGovernanceRoute(item.Id, item.AuthorizationRevision)).ToArrayAsync(ct);
        if (!frozen.Routes.SequenceEqual(routes)) throw Unavailable();
        await RequireFrozenRevisionsAsync(db, candidate.ResourceRevisions, ledger, ct);
    }

    private static async Task RequireFrozenRevisionsAsync(WebApiDbContext db, IReadOnlyList<ResourceRevision> revisions,
        SecurityLedgerMaterial ledger, CancellationToken ct)
    {
        if (revisions is null || revisions.Any(item => item is null || item.Id == Guid.Empty || item.Revision <= 0) ||
            !revisions.Any(item => item.Type == "environment" && item.Id == ledger.EnvironmentId)) throw Unavailable();

        var apis = db.Set<Api>().AsNoTracking().Where(item => item.OrganizationId == ledger.OrganizationId && item.ProjectId == ledger.ProjectId);
        var clusters = db.Set<UpstreamCluster>().AsNoTracking().Where(item => item.EnvironmentId == ledger.EnvironmentId && item.ProjectId == ledger.ProjectId);
        var applications = db.Set<ApplicationRecord>().AsNoTracking().Where(item => item.OrganizationId == ledger.OrganizationId &&
            (item.ProjectId == null || item.ProjectId == ledger.ProjectId));
        foreach (var type in revisions.GroupBy(item => item.Type, StringComparer.Ordinal))
        {
            // Ledger and target checks above already qualify the synthetic governance stamp.
            if (type.Key == "governance_snapshot") continue;
            var ids = type.Select(item => item.Id).ToArray();
            var current = type.Key switch
            {
                "route" => db.Set<ApiRoute>().AsNoTracking().Where(item => ids.Contains(item.Id) && item.EnvironmentId == ledger.EnvironmentId)
                    .Select(item => new ResourceRevision("route", item.Id, item.Revision)),
                "cluster" => clusters.Where(item => ids.Contains(item.Id))
                    .Select(item => new ResourceRevision("cluster", item.Id, item.Revision)),
                "destination" => db.Set<UpstreamDestination>().AsNoTracking().Where(item => ids.Contains(item.Id) &&
                        clusters.Any(cluster => cluster.Id == item.ClusterId))
                    .Select(item => new ResourceRevision("destination", item.Id, item.Revision)),
                "policy" => db.Set<Policy>().AsNoTracking().Where(item => ids.Contains(item.Id) && item.OrganizationId == ledger.OrganizationId &&
                        (item.ProjectId == null || item.ProjectId == ledger.ProjectId))
                    .Select(item => new ResourceRevision("policy", item.Id, item.VersionNo)),
                "api" => apis.Where(item => ids.Contains(item.Id))
                    .Select(item => new ResourceRevision("api", item.Id, item.VersionNo)),
                "version" => db.Set<ApiVersion>().AsNoTracking().Where(item => ids.Contains(item.Id) && apis.Any(api => api.Id == item.ApiId))
                    .Select(item => new ResourceRevision("version", item.Id, item.Revision)),
                "application" => applications.Where(item => ids.Contains(item.Id))
                    .Select(item => new ResourceRevision("application", item.Id, item.Revision)),
                "credential" => db.Set<ApplicationCredential>().AsNoTracking()
                    .Where(item => ids.Contains(item.Id) && item.OrganizationId == ledger.OrganizationId &&
                        applications.Any(app => app.Id == item.ApplicationId) &&
                        (!ledger.RequireEnvironmentCredentials && item.BindingMode == "LegacyGlobal" && item.EnvironmentId == null ||
                         item.BindingMode == "EnvironmentBound" && item.EnvironmentId == ledger.EnvironmentId && item.ProjectId == ledger.ProjectId))
                    .Select(item => new ResourceRevision("credential", item.Id, item.Revision)),
                "authorization" => db.Set<ApplicationApiPermission>().AsNoTracking()
                    .Where(item => ids.Contains(item.Id) && item.EnvironmentId == ledger.EnvironmentId)
                    .Select(item => new ResourceRevision("authorization", item.Id, item.Revision)),
                "environment" => db.Set<EnvironmentRecord>().AsNoTracking().Where(item => ids.Contains(item.Id) &&
                        item.Id == ledger.EnvironmentId && item.ProjectId == ledger.ProjectId && item.Status == "Active")
                    .Select(item => new ResourceRevision("environment", item.Id, item.Revision)),
                "approval_flow" => db.Set<ApprovalFlow>().AsNoTracking().Where(item => ids.Contains(item.Id) &&
                        item.OrganizationId == ledger.OrganizationId && item.Enabled)
                    .Select(item => new ResourceRevision("approval_flow", item.Id, item.Revision)),
                _ => throw Unavailable()
            };
            var actual = await current.ToDictionaryAsync(item => item.Id, item => item.Revision, ct);
            if (actual.Count != ids.Length || type.Any(item => !actual.TryGetValue(item.Id, out var revision) || revision != item.Revision))
                throw Unavailable();
        }
    }

    private static async Task RequireCredentialScopeAsync(WebApiDbContext db, RuntimeSnapshot snapshot, CancellationToken ct)
    {
        var keys = snapshot.Applications.SelectMany(application => application.Credentials).ToDictionary(key => key.Id);
        if (keys.Count == 0) return;
        var ids = keys.Keys.ToArray();
        var bound = await db.Set<ApplicationCredential>().AsNoTracking().Where(item => ids.Contains(item.Id) && item.BindingMode == "EnvironmentBound")
            .Select(item => new { item.Id, item.EnvironmentId }).ToArrayAsync(ct);
        if (bound.Any(item => snapshot.SchemaVersion != "2.4" || item.EnvironmentId != snapshot.EnvironmentId ||
                keys[item.Id].BindingMode != "EnvironmentBound" || keys[item.Id].EnvironmentId != item.EnvironmentId))
            throw Unavailable();
    }

    internal static ApiException Unavailable() =>
        new(503, "credential_governance_delivery_unavailable", "无法验证配置下发所需的安全账本连续性。");
}

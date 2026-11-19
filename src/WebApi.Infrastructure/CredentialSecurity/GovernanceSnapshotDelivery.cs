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
            if (guarded || settings?.RequireIndependentContinuity == true) throw Unavailable();
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
            if (guarded || ledger.Revocations.Count > 0 || ledger.MinimumSnapshotSchema is "2.3" or "2.4") throw Unavailable();
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
        var permissions = candidate.ResourceRevisions.Where(item => item.Type == "authorization").OrderBy(item => item.Id).ToArray();
        var permissionIds = permissions.Select(item => item.Id).ToArray();
        var currentPermissions = await db.Set<ApplicationApiPermission>().AsNoTracking().Where(item => item.EnvironmentId == ledger.EnvironmentId &&
                permissionIds.Contains(item.Id)).OrderBy(item => item.Id).Select(item => new ResourceRevision("authorization", item.Id, item.Revision)).ToArrayAsync(ct);
        if (!frozen.Routes.SequenceEqual(routes) || !permissions.SequenceEqual(currentPermissions)) throw Unavailable();
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

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed record CredentialArchivedIngressScope(Guid OrganizationId, Guid ProjectId, Guid EnvironmentId, string Status,
    long Revision, long AuthorizationRevision, long AccessAddressRevision, string IngressAddressHash,
    long DeploymentSequence, long? DesiredConfigVersion, Guid SecurityGeneration, long Epoch, string HeadHash,
    string HistoricalInventoryHash, string NodeLeaseInventoryHash);

public sealed record CredentialArchivedIngressSeal(CredentialArchivedIngressScope Scope, string ProofHash,
    DateTimeOffset VerifiedAt, DateTimeOffset ValidUntil);

public interface ICredentialArchivedIngressWitness
{
    // Read only already authenticated, independently current evidence. This method runs under the writer fence and must not perform I/O.
    // A seal attests complete historical and lease inventories and isolation of every old or bypass entrance across a status change.
    // Old entrances must stay isolated when a separately controlled bootstrap admits new ingress. The proof hash identifies that durable fact.
    CredentialArchivedIngressSeal? GetVerifiedSeal(CredentialArchivedIngressScope scope);
}

internal static class CredentialArchivedIngress
{
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(2);

    internal static async Task<CredentialArchivedIngressSeal> RequireAsync(WebApiDbContext db, EnvironmentRecord env,
        Guid organizationId, ICredentialArchivedIngressWitness? witness, DateTimeOffset now, CancellationToken ct)
    {
        if (witness is null || env.Id == Guid.Empty || env.ProjectId == Guid.Empty || organizationId == Guid.Empty ||
            env.Status != "Disabled" || !UtcInstant(now) || db.Database.CurrentTransaction is null ||
            !await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_locks
                    WHERE pid = pg_catalog.pg_backend_pid() AND locktype = 'advisory' AND mode = 'ExclusiveLock'
                        AND classid = 0 AND objid = 8901202 AND objsubid = 1 AND granted
                ) AS "Value"
                """).SingleAsync(ct)) throw Unverified();

        SecurityLedgerMaterial ledger;
        try { ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(env.Id, ct); }
        catch (InvalidOperationException) { throw Unverified(); }
        if (ledger.OrganizationId != organizationId || ledger.ProjectId != env.ProjectId) throw Unverified();

        var stored = await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(row => row.Id == env.Id, ct);
        if (stored.Status != "Disabled" || stored.ProjectId != env.ProjectId || stored.Revision <= 0 || stored.AuthorizationRevision <= 0 ||
            stored.AccessAddressRevision <= 0 || stored.DeploymentSequence < 0 || stored.Revision != env.Revision ||
            stored.AuthorizationRevision != env.AuthorizationRevision || stored.AccessAddressRevision != env.AccessAddressRevision ||
            stored.DeploymentSequence != env.DeploymentSequence || stored.DesiredConfigVersion != env.DesiredConfigVersion)
            throw Unverified();

        var versions = await db.Set<GatewayConfigVersion>().AsNoTracking().Where(row => row.EnvironmentId == env.Id)
            .OrderBy(row => row.Id).ToArrayAsync(ct);
        var availableVersions = versions.Select(row => row.VersionNo).ToHashSet();
        if (availableVersions.Count != versions.Length || versions.Any(row => row.VersionNo <= 0) ||
            (stored.DesiredConfigVersion is long desired && (stored.DeploymentSequence <= 0 || !availableVersions.Contains(desired))))
            throw Unverified();

        var historicalHash = await ReadHistoryAsync(db, stored, versions, availableVersions, ct);
        var nodeLeases = await ReadNodeLeasesAsync(db, ledger, availableVersions, now, ct);
        var addressHash = Digest("ingress-addresses", new { stored.GatewayPublicUrl, stored.GatewayInternalUrl, stored.BasePath });
        var scope = new CredentialArchivedIngressScope(organizationId, stored.ProjectId, stored.Id, stored.Status, stored.Revision,
            stored.AuthorizationRevision, stored.AccessAddressRevision, addressHash, stored.DeploymentSequence, stored.DesiredConfigVersion,
            ledger.SecurityGeneration, ledger.Epoch, ledger.HeadHash, historicalHash, nodeLeases.Hash);

        ct.ThrowIfCancellationRequested();
        var seal = witness.GetVerifiedSeal(scope);
        if (seal is null || seal.Scope != scope || !SecuritySignedEncoding.HexHash(seal.ProofHash) ||
            !UtcInstant(seal.VerifiedAt) || !UtcInstant(seal.ValidUntil) || seal.VerifiedAt > now || now >= seal.ValidUntil ||
            (nodeLeases.LatestExpiry is DateTimeOffset expiry && seal.VerifiedAt - expiry < MaximumClockSkew))
            throw Unverified();
        return seal;
    }

    private static async Task<string> ReadHistoryAsync(WebApiDbContext db, EnvironmentRecord environment,
        GatewayConfigVersion[] versions, HashSet<long> availableVersions, CancellationToken ct)
    {
        var releases = new List<string>();
        var desiredReleaseCount = 0;
        await foreach (var release in db.Set<ReleaseRecord>().AsNoTracking().Where(row => row.EnvironmentId == environment.Id)
            .OrderBy(row => row.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (release.Status is "WaitingApproval" or "Ready" or "Building" or "Publishing" ||
                !KnownVersion(release.FromConfigVersion, availableVersions) || !KnownVersion(release.ToConfigVersion, availableVersions) ||
                !KnownVersion(release.BaselineConfigVersion, availableVersions)) throw Unverified();
            if (release.ToConfigVersion == environment.DesiredConfigVersion && release.DeploymentSequence == environment.DeploymentSequence)
                desiredReleaseCount++;
            releases.Add(Digest("release", release));
        }
        if (environment.DesiredConfigVersion is not null && desiredReleaseCount != 1) throw Unverified();

        var historical = new HistoricalSnapshotService(db, new ScopeResolver(db));
        var snapshots = new List<HistoricalSnapshot>(versions.Length);
        foreach (var version in versions)
        {
            try
            {
                var artifact = await historical.ReadAsync(environment.Id, version.VersionNo, ct);
                snapshots.Add(new(version, artifact.Hash, artifact.Size));
            }
            catch (ApiException error) when (error.Status is 404 or 422) { throw Unverified(); }
            catch (ArgumentException) { throw Unverified(); }
        }
        return Digest("history", new { releases, snapshots });
    }

    private static async Task<LeaseInventory> ReadNodeLeasesAsync(WebApiDbContext db, SecurityLedgerMaterial ledger,
        HashSet<long> availableVersions, DateTimeOffset now, CancellationToken ct)
    {
        var nodes = await db.Set<GatewayNode>().AsNoTracking().Where(node => node.EnvironmentId == ledger.EnvironmentId)
            .OrderBy(node => node.Id).ToArrayAsync(ct);
        if (nodes.Any(node => node.Enabled || !KnownVersion(node.CurrentConfigVersion, availableVersions) ||
            (node.TargetConfigVersion is long target && !KnownVersion(target, availableVersions)))) throw Unverified();
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        // An older registered instance can lack any governance lease record.
        var nodeHistory = await db.Set<GatewayNodeEvent>().AsNoTracking().Where(item => nodeIds.Contains(item.GatewayNodeId) &&
                (item.EventType == "registered" || item.EventType == "environment_archive_retired"))
            .OrderBy(item => item.GatewayNodeId).ThenBy(item => item.Id).ToArrayAsync(ct);
        var leases = await db.Set<GatewaySecurityLease>().AsNoTracking().Where(lease => lease.EnvironmentId == ledger.EnvironmentId)
            .OrderBy(lease => lease.NodeId).ThenBy(lease => lease.InstanceId).ToArrayAsync(ct);
        var issuances = await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().Where(lease => lease.EnvironmentId == ledger.EnvironmentId)
            .OrderBy(lease => lease.LeaseId).ToArrayAsync(ct);
        if (issuances.Any(fact => fact.LeaseId == Guid.Empty || fact.InstanceId == Guid.Empty || !nodeIds.Contains(fact.NodeId) ||
            fact.OrganizationId != ledger.OrganizationId || fact.ProjectId != ledger.ProjectId || fact.SecurityGeneration != ledger.SecurityGeneration ||
            fact.Epoch < 0 || fact.Epoch > ledger.Epoch || !SecuritySignedEncoding.HexHash(fact.CheckpointHash) ||
            !SecuritySignedEncoding.HexHash(fact.PayloadHash) || !SecuritySignedEncoding.KeyId(fact.SigningKeyId) ||
            !UtcInstant(fact.IssuedAt) || !UtcInstant(fact.FreshUntil) || fact.FreshUntil <= fact.IssuedAt ||
            fact.FreshUntil - fact.IssuedAt > TimeSpan.FromSeconds(30) || now - fact.FreshUntil < MaximumClockSkew))
            throw Unverified();

        var byId = issuances.ToDictionary(fact => fact.LeaseId);
        var latest = issuances.GroupBy(fact => (fact.NodeId, fact.InstanceId))
            .ToDictionary(group => group.Key, group => group.Max(fact => fact.FreshUntil));
        if (latest.Count != leases.Length || leases.Any(lease => !byId.TryGetValue(lease.LeaseId, out var fact) ||
            !SameIssuance(lease, fact) || !latest.TryGetValue((lease.NodeId, lease.InstanceId), out var horizon) ||
            lease.LastIssuedLeaseUntil != horizon || now - horizon < MaximumClockSkew)) throw Unverified();
        return new(Digest("node-leases", new { nodes, nodeHistory, leases, issuances }), issuances.Max(fact => (DateTimeOffset?)fact.FreshUntil));
    }

    private static bool SameIssuance(GatewaySecurityLease lease, GatewaySecurityLeaseIssuance fact) =>
        lease.NodeId == fact.NodeId && lease.InstanceId == fact.InstanceId && lease.EnvironmentId == fact.EnvironmentId &&
        lease.OrganizationId == fact.OrganizationId && lease.ProjectId == fact.ProjectId && lease.SecurityGeneration == fact.SecurityGeneration &&
        lease.Epoch == fact.Epoch && lease.CheckpointHash == fact.CheckpointHash && lease.SigningKeyId == fact.SigningKeyId &&
        lease.IssuedAt == fact.IssuedAt && lease.FreshUntil == fact.FreshUntil;

    private static bool KnownVersion(long version, HashSet<long> availableVersions) => version == 0 || availableVersions.Contains(version);

    private static bool UtcInstant(DateTimeOffset value) => value.Offset == TimeSpan.Zero &&
        value > DateTimeOffset.MinValue && value < DateTimeOffset.MaxValue;

    private static string Digest<T>(string part, T value) => Convert.ToHexStringLower(SHA256.HashData(
        SecuritySignedEncoding.Unsigned($"webapi.credential-governance.archived-ingress.{part}.v1", value)));

    private static ApiException Unverified() => new(409, "credential_revocation_archive_unverified", "停用环境的旧入口封存证明尚未核验。");

    private sealed record HistoricalSnapshot(GatewayConfigVersion Version, string Hash, long Size);
    private sealed record LeaseInventory(string Hash, DateTimeOffset? LatestExpiry);
}

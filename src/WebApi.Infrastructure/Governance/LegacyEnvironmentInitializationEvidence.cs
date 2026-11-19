using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;

namespace WebApi.Infrastructure.Governance;

internal static class LegacyEnvironmentInitializationEvidence
{
    internal sealed record NodeInstance(Guid NodeId, string InstanceId);
    internal sealed record PublishedArtifact(long ConfigVersion, long Size, string Hash);
    internal sealed record PublicationReview(LegacyEnvironmentPublication Current, IReadOnlyList<PublishedArtifact> History);

    internal static async Task<LegacyEnvironmentSource?> ReadSourceAsync(WebApiDbContext db,
        EnvironmentSecurityInitialization origin, CancellationToken ct)
    {
        var receipt = await db.Set<EnvironmentSecuritySourceReceipt>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.EnvironmentId == origin.EnvironmentId, ct);
        if (receipt is null || receipt.ProjectId != origin.ProjectId || receipt.OrganizationId != origin.OrganizationId)
            return null;

        var migrations = db.Database.GetMigrations().ToArray();
        if (!migrations.Take(GovernanceMigrationSourceCapture.LastSupportedMainlineIndex).Contains(receipt.SourceMigration, StringComparer.Ordinal) ||
            !GovernanceSchemaFingerprints.Expected.TryGetValue(receipt.SourceMigration, out var sourceSchema) || receipt.SourceSchemaHash != sourceSchema ||
            !(await GovernanceMigrationHistory.ReadAppliedAsync(db, ct)).SequenceEqual(migrations, StringComparer.Ordinal))
            return null;
        try { await GovernanceMigrationPreflight.ValidateAsync(db, ct); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("governance_migration_preflight_failed", StringComparison.Ordinal))
        { return null; }

        var current = await MigrationReceiptIdentityCapture.CaptureAsync(db, ct);
        if (current is null || current != new MigrationDatabaseIdentity(receipt.SourceClusterId, receipt.SourceDatabase, receipt.SourceDatabaseOid))
            return null;
        var historyHash = await db.Database.SqlQuery<string>($"""
            SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(COALESCE(
                pg_catalog.json_agg(pg_catalog.json_build_array("MigrationId","ProductVersion") ORDER BY "MigrationId")::text,'[]'),'UTF8')),'hex') AS "Value"
            FROM public."__EFMigrationsHistory" WHERE "MigrationId"<={receipt.SourceMigration}
            """).SingleAsync(ct);
        if (receipt.SourceHistoryHash != historyHash || receipt.CapturedAt <= DateTimeOffset.MinValue || receipt.CapturedAt >= DateTimeOffset.MaxValue)
            return null;
        return new(receipt.EnvironmentId, receipt.ProjectId, receipt.OrganizationId, current, receipt.SourceMigration,
            sourceSchema, historyHash, receipt.CapturedAt);
    }

    internal static async Task<bool> HasGovernanceAsync(WebApiDbContext db, EnvironmentRecord environment, ScopeRef scope,
        CancellationToken ct)
    {
        if (environment.RequireEnvironmentCredential ||
            await GlobalCredentialInheritance.HasApplicableAsync(db, scope.OrganizationId, environment.ProjectId, ct)) return true;
        var environmentId = environment.Id;
        return await db.Set<EnvironmentSecurityState>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId)
            .Concat(db.Set<SecurityEvent>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId))
            .Concat(db.Set<CredentialRevocation>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId))
            .Concat(db.Set<FormalCredentialRotation>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId))
            .Concat(db.Set<SecurityOperationTarget>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId))
            .Concat(db.Set<SecurityOutboxMessage>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId))
            .Concat(db.Set<GatewaySecurityLease>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId))
            .Concat(db.Set<GatewaySecurityLeaseIssuance>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId))
            .Concat(db.Set<GatewaySecurityCurrentReceipt>().Where(row => row.EnvironmentId == environmentId).Select(row => row.EnvironmentId))
            .AnyAsync(ct);
    }

    internal static Task<NodeInstance[]> ReadKnownInstancesAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct) =>
        db.Database.SqlQuery<NodeInstance>($"""
            SELECT id AS "NodeId", COALESCE(instance_id,'') AS "InstanceId" FROM public.gateway_nodes WHERE environment_id={environmentId}
            UNION SELECT n.id, COALESCE(e.detail->>'instanceId','') FROM public.gateway_node_events e
                JOIN public.gateway_nodes n ON n.id=e.gateway_node_id WHERE n.environment_id={environmentId} AND e.event_type='registered'
            UNION SELECT t.node_id, COALESCE(t.instance_id,'') FROM public.release_targets t
                JOIN public.release_records r ON r.id=t.release_id WHERE r.environment_id={environmentId}
            UNION SELECT a.node_id, COALESCE(a.instance_id,'') FROM public.gateway_acks a
                JOIN public.release_records r ON r.id=a.release_id WHERE r.environment_id={environmentId}
            """).ToArrayAsync(ct);

    internal static async Task<PublicationReview?> ReadPublicationAsync(WebApiDbContext db, HistoricalSnapshotService history,
        EnvironmentRecord environment, GatewayNode[] nodes, CancellationToken ct)
    {
        var configs = db.Set<GatewayConfigVersion>().AsNoTracking().Where(row => row.EnvironmentId == environment.Id);
        var releases = db.Set<ReleaseRecord>().AsNoTracking().Where(row => row.EnvironmentId == environment.Id);
        if (nodes.Any(HasSecurityReceipt) || await releases.AnyAsync(row => row.Status == "Building" || row.Status == "Publishing", ct))
            return null;
        if (environment.DesiredConfigVersion is null or 0 && environment.DeploymentSequence == 0 &&
            nodes.All(row => row.CurrentConfigVersion == 0 && row.CurrentDeploymentSequence == 0) &&
            !await configs.AnyAsync(ct) &&
            !await releases.AnyAsync(row => row.ToConfigVersion > 0 || row.Status == "Succeeded", ct))
            return new(LegacyEnvironmentPublication.NeverPublished, []);
        if (environment.DesiredConfigVersion is not > 0 || environment.DeploymentSequence <= 0 || !nodes.Any(row => row.Enabled) ||
            nodes.Any(row => row.Enabled && (row.CurrentConfigVersion != environment.DesiredConfigVersion ||
                row.CurrentDeploymentSequence != environment.DeploymentSequence))) return null;

        var version = environment.DesiredConfigVersion.Value;
        var releaseIds = await releases.Where(row => row.Status == "Succeeded" && row.ToConfigVersion == version &&
            row.DeploymentSequence == environment.DeploymentSequence).Select(row => row.Id).Take(2).ToArrayAsync(ct);
        if (releaseIds.Length != 1 || !await configs.AnyAsync(row => row.VersionNo == version && row.Status == "Published", ct))
            return null;

        // An unsuccessful deployment can still have issued a governance artifact.
        // Include every materialized version when excluding prior governance.
        var materialized = await configs.Select(row => row.VersionNo).OrderBy(versionNo => versionNo).ToArrayAsync(ct);
        if (await releases.AnyAsync(row => row.ToConfigVersion > 0 && !materialized.Contains(row.ToConfigVersion), ct))
            return null;
        var artifacts = new List<PublishedArtifact>(materialized.Length);
        try
        {
            foreach (var historicalVersion in materialized)
            {
                var artifact = await history.ReadAsync(environment.Id, historicalVersion, ct);
                if (SnapshotDialectClassifier.Classify(artifact.Payload) is not (SnapshotDialect.Legacy or SnapshotDialect.Network23))
                    return null;
                artifacts.Add(new(historicalVersion, artifact.Size, artifact.Hash));
            }
        }
        catch (ApiException error) when (error.Status is 404 or 422) { return null; }
        var current = artifacts.SingleOrDefault(item => item.ConfigVersion == version);
        return current is null ? null : new(new(releaseIds[0], version, environment.DeploymentSequence, current.Size, current.Hash), artifacts);
    }

    internal static bool Matches(LegacyEnvironmentHistoryEvidence evidence, LegacyEnvironmentSource source,
        LegacyEnvironmentPublication publication, GatewayNode[] nodes, NodeInstance[] known)
    {
        if (evidence.Source != source || !evidence.KnownMainlineDeploymentsOnly || !evidence.RecoveryHistoryComplete || !evidence.LegacyWritersStopped ||
            !SecuritySignedEncoding.HexHash(evidence.DeploymentManifestHash) || !SecuritySignedEncoding.HexHash(evidence.RecoveryInventoryHash) ||
            !SecuritySignedEncoding.HexHash(evidence.WriterFenceHash) || evidence.Publication != publication || evidence.Nodes is null ||
            evidence.Nodes.Any(row => row is null || !ValidInstance(row.NodeId, row.InstanceId) || !SecuritySignedEncoding.HexHash(row.StorageIdentityHash) ||
                row.HistoryState != LegacyNodeHistoryState.StoppedWithoutGovernanceHistory) ||
            evidence.Nodes.DistinctBy(row => (row.NodeId, row.InstanceId, row.StorageIdentityHash)).Count() != evidence.Nodes.Count) return false;

        var instances = evidence.Nodes.ToLookup(row => new NodeInstance(row.NodeId, row.InstanceId));
        return known.All(row => ValidInstance(row.NodeId, row.InstanceId) && instances.Contains(row)) &&
            nodes.All(node => instances[new(node.Id, node.InstanceId)].Any(proof => proof.ConfigVersion == node.CurrentConfigVersion &&
                proof.DeploymentSequence == node.CurrentDeploymentSequence && (!node.Enabled || proof.SnapshotHash == publication.SnapshotHash)));
    }

    private static bool ValidInstance(Guid nodeId, string instanceId) =>
        nodeId != Guid.Empty && Guid.TryParseExact(instanceId, "D", out var id) && id != Guid.Empty && id.ToString("D") == instanceId;

    private static bool HasSecurityReceipt(GatewayNode node)
    {
        using var document = JsonDocument.Parse(node.Metadata ?? "{}");
        return document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject()
            .Any(property => property.Name.Equals("securityApplication", StringComparison.OrdinalIgnoreCase));
    }
}

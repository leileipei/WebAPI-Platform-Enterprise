using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Observability;

internal sealed class DestinationMembership(Dictionary<Guid, ObservationDestination> working)
{
    internal Dictionary<Guid, ObservationDestination> Destinations { get; } = new(working);
    internal Dictionary<ExpectedObservationNode, HashSet<(Guid ClusterId, Guid DestinationId)>> Applied { get; } = [];
    internal HashSet<ExpectedObservationNode> UnconfirmedNodes { get; } = [];
    internal HashSet<Guid> UnconfirmedEnvironments { get; } = [];

    // A historical identity may still be visible while its current instance cannot prove membership.
    internal bool? TargetExists(Guid environment, Guid id)
    {
        if (working.ContainsKey(id) || Destinations.TryGetValue(id, out var member) &&
            Applied.Any(node => node.Key.EnvironmentId == environment && node.Value.Contains((member.ClusterId, id))))
            return true;
        return UnconfirmedEnvironments.Contains(environment) ? null : false;
    }
}

internal static class AppliedDestinationQuery
{
    private sealed record NodeEvidence(Guid EnvironmentId, string? NodeName, string? InstanceId, bool HasDeployment,
        Guid? CurrentVersion, long? CurrentSequence, DateTimeOffset? AcknowledgedAt, string? CurrentInstanceAppliedAt,
        Guid? PreviousVersion, long? PreviousSequence, string? PreviousInstance);
    private sealed record NodeVersion(ExpectedObservationNode Node, Guid Version, long Sequence, bool Confirmed);
    private sealed record Member(Guid VersionId, Guid? RuntimeCluster, Guid? ClusterId, Guid? DestinationId);
    private sealed record FrozenName(Guid VersionId, Guid RuntimeCluster, Guid ClusterId, Guid DestinationId, string Name);

    internal static async Task<DestinationMembership> ReadAsync(WebApiDbContext db, Guid organization, Guid project,
        Guid[] environments, DateTimeOffset? asOf, CancellationToken ct, bool includeNames = true)
    {
        var working = await (from d in db.Set<UpstreamDestination>().AsNoTracking()
            join c in db.Set<UpstreamCluster>() on d.ClusterId equals c.Id
            where c.ProjectId == project && environments.Contains(c.EnvironmentId)
            select new ObservationDestination(d.Id, c.Id, c.EnvironmentId, d.Name, d.Enabled)).ToDictionaryAsync(d => d.Id, ct);
        var result = new DestinationMembership(working);
        if (asOf is not { } end || environments.Length == 0) return result;

        // A heartbeat can advance the version before this instance acknowledges it. The last frozen ACK at
        // the window end also identifies a historical member if a newer application or a restart intervened.
        var evidence = await db.Database.SqlQuery<NodeEvidence>($"""
            SELECT e.id AS "EnvironmentId", n.node_name AS "NodeName", n.instance_id AS "InstanceId",
                (e.desired_config_version > 0) IS TRUE AS "HasDeployment",
                v.id AS "CurrentVersion", r.deployment_sequence AS "CurrentSequence", a.applied_at AS "AcknowledgedAt",
                CASE WHEN t.node_id IS NOT NULL AND n.metadata->'runtimeApplication' @> jsonb_build_object(
                    'schemaVersion', 1, 'instanceId', n.instance_id, 'releaseId', r.id,
                    'configVersion', v.version_no, 'deploymentSequence', r.deployment_sequence, 'payloadHash', v.snapshot_hash)
                    THEN n.metadata->'runtimeApplication'->>'appliedAt' END AS "CurrentInstanceAppliedAt",
                prior.version_id AS "PreviousVersion", prior.deployment_sequence AS "PreviousSequence",
                prior.instance_id AS "PreviousInstance"
            FROM environments e
            LEFT JOIN gateway_nodes n ON n.environment_id = e.id AND n.enabled
            LEFT JOIN release_records r ON r.environment_id = e.id AND r.deployment_sequence = n.current_deployment_sequence
                AND r.to_config_version = n.current_config_version AND n.current_deployment_sequence > 0
            LEFT JOIN gateway_config_versions v ON v.environment_id = e.id AND v.version_no = n.current_config_version
                AND v.snapshot_hash IS NOT NULL AND n.current_config_version > 0
            LEFT JOIN release_targets t ON t.release_id = r.id AND t.node_id = n.id
            LEFT JOIN gateway_acks a ON a.release_id = t.release_id AND a.node_id = t.node_id AND a.success
                AND a.instance_id = t.instance_id AND a.instance_id = n.instance_id AND a.config_version = v.version_no
                AND a.deployment_sequence = r.deployment_sequence AND a.payload_hash = v.snapshot_hash
            LEFT JOIN LATERAL (
                SELECT pv.id AS version_id, ack.deployment_sequence, ack.instance_id
                FROM gateway_acks ack JOIN release_targets target ON target.node_id = ack.node_id
                    AND target.release_id = ack.release_id AND target.instance_id = ack.instance_id
                JOIN release_records rel ON rel.id = ack.release_id AND rel.environment_id = e.id
                    AND rel.deployment_sequence = ack.deployment_sequence AND rel.to_config_version = ack.config_version
                JOIN gateway_config_versions pv ON pv.environment_id = e.id AND pv.version_no = ack.config_version
                    AND pv.snapshot_hash = ack.payload_hash
                WHERE ack.node_id = n.id AND ack.success AND ack.applied_at < {end}
                ORDER BY ack.deployment_sequence DESC LIMIT 1
            ) prior ON TRUE
            WHERE e.project_id = {project} AND e.id = ANY({environments})
            """).ToArrayAsync(ct);
        var versions = new List<NodeVersion>();
        foreach (var row in evidence)
        {
            if (row.NodeName is null)
            {
                if (row.HasDeployment) result.UnconfirmedEnvironments.Add(row.EnvironmentId);
                continue;
            }
            var node = new ExpectedObservationNode(row.EnvironmentId, row.NodeName);
            var at = row.AcknowledgedAt;
            if (DateTimeOffset.TryParseExact(row.CurrentInstanceAppliedAt, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var receipt) && (at is null || receipt < at)) at = receipt;

            if (at < end && row.CurrentVersion is Guid current)
                versions.Add(new(node, current, row.CurrentSequence!.Value, true));
            else if (row.PreviousVersion is Guid previous)
                versions.Add(new(node, previous, row.PreviousSequence!.Value, at >= end && row.PreviousInstance == row.InstanceId));
            else if (row.HasDeployment)
            {
                // Before the first recorded application, a sampled fault remains observable. A zero is unproven.
                if (at is null) result.UnconfirmedNodes.Add(node);
                result.UnconfirmedEnvironments.Add(node.EnvironmentId);
            }
        }
        if (versions.Count == 0) return result;

        // Only the hash-verified delivered bytes establish identity. Names live in the original frozen
        // release candidate. A retained cluster may have been compiled by an earlier release.
        var ids = versions.Select(n => n.Version).Distinct().ToArray();
        var members = await db.Database.SqlQuery<Member>($"""
            WITH applied_snapshots AS MATERIALIZED (
                SELECT v.id, v.environment_id, v.version_no, snapshot.clusters FROM gateway_config_versions v
                JOIN gateway_config_snapshots s ON s.config_version_id = v.id
                CROSS JOIN LATERAL jsonb_to_record(CASE WHEN octet_length(s.payload_bytes) = s.size_bytes
                    AND encode(sha256(s.payload_bytes), 'hex') = v.snapshot_hash AND s.payload_bytes IS JSON OBJECT
                    THEN convert_from(s.payload_bytes, 'UTF8')::jsonb END)
                    AS snapshot("environmentId" text, "configVersion" text, clusters jsonb)
                WHERE v.id = ANY({ids}) AND snapshot."environmentId" = v.environment_id::text
                    AND snapshot."configVersion" = v.version_no::text AND jsonb_typeof(snapshot.clusters) = 'array'
            )
            SELECT s.id AS "VersionId", member.runtime_cluster AS "RuntimeCluster",
                member.cluster_id AS "ClusterId", member.destination_id AS "DestinationId"
            FROM applied_snapshots s LEFT JOIN LATERAL (
                SELECT DISTINCT (c->>'id')::uuid AS runtime_cluster, COALESCE(c->>'sourceId', c->>'id')::uuid AS cluster_id,
                    (d->>'id')::uuid AS destination_id
                FROM jsonb_array_elements(s.clusters) c CROSS JOIN LATERAL jsonb_array_elements(c->'destinations') d
                WHERE c->>'environmentId' = s.environment_id::text
            ) member ON TRUE
            """).ToArrayAsync(ct);
        var names = new Dictionary<Member, string>();
        if (includeNames)
        {
            var identified = members.Where(row => row.DestinationId.HasValue).ToArray();
            await ReadNamesAsync(identified, retained: false);
            await ReadNamesAsync(identified.Where(row => !names.ContainsKey(row)).ToArray(), retained: true);
        }
        var snapshots = members.ToLookup(row => row.VersionId);
        var named = new HashSet<Guid>();
        foreach (var version in versions.OrderByDescending(n => n.Confirmed).ThenByDescending(n => n.Sequence))
        {
            var rows = snapshots[version.Version];
            var valid = snapshots.Contains(version.Version);
            if (!version.Confirmed || !valid)
            {
                result.UnconfirmedNodes.Add(version.Node);
                result.UnconfirmedEnvironments.Add(version.Node.EnvironmentId);
            }
            var set = new HashSet<(Guid, Guid)>();
            foreach (var member in rows)
            {
                if (member.ClusterId is not Guid cluster || member.DestinationId is not Guid destination) continue;
                set.Add((cluster, destination));
                if (named.Add(destination))
                    result.Destinations[destination] = new(destination, cluster, version.Node.EnvironmentId, names.GetValueOrDefault(member) ?? "未知后端", true);
            }
            if (version.Confirmed && valid) result.Applied[version.Node] = set;
        }
        return result;

        async Task ReadNamesAsync(Member[] requested, bool retained)
        {
            if (requested.Length == 0) return;
            var versionIds = requested.Select(m => m.VersionId).ToArray();
            var runtimeClusters = requested.Select(m => m.RuntimeCluster!.Value).ToArray();
            var clusters = requested.Select(m => m.ClusterId!.Value).ToArray();
            var destinations = requested.Select(m => m.DestinationId!.Value).ToArray();
            var rows = await db.Database.SqlQuery<FrozenName>($"""
                WITH wanted AS MATERIALIZED (
                    SELECT v.id, v.environment_id, v.version_no, m.runtime_cluster, m.cluster_id, m.destination_id
                    FROM unnest({versionIds}, {runtimeClusters}, {clusters}, {destinations})
                        AS m(version_id, runtime_cluster, cluster_id, destination_id)
                    JOIN gateway_config_versions v ON v.id = m.version_id
                ), sources AS MATERIALIZED (
                    SELECT w.*, v.version_no AS source_version FROM wanted w
                    JOIN gateway_config_versions v ON v.environment_id = w.environment_id
                    WHERE CASE WHEN {retained} THEN v.version_no < w.version_no AND EXISTS (
                        SELECT 1 FROM gateway_config_snapshots s WHERE s.config_version_id = v.id
                            AND s.payload @> jsonb_build_object('clusters', jsonb_build_array(jsonb_build_object('id', w.runtime_cluster))))
                        ELSE v.id = w.id END
                ), origins AS MATERIALIZED (
                    SELECT s.*, frozen.candidate_bytes FROM (SELECT DISTINCT environment_id, source_version AS version_no FROM sources) s
                    CROSS JOIN LATERAL (
                        SELECT r.candidate_bytes FROM release_records r WHERE r.environment_id = s.environment_id
                            AND r.to_config_version = s.version_no AND r.deployment_sequence > 0
                        ORDER BY r.deployment_sequence LIMIT 1
                    ) frozen
                ), names AS MATERIALIZED (
                    SELECT o.environment_id, o.version_no, c->>'id' AS cluster_id, d->>'id' AS destination_id, d->>'name' AS name
                    FROM origins o CROSS JOIN LATERAL jsonb_to_record(CASE WHEN o.candidate_bytes IS JSON OBJECT
                        THEN convert_from(o.candidate_bytes, 'UTF8')::jsonb END)
                        AS f("organizationId" text, "projectId" text, "environmentId" text, clusters jsonb)
                    CROSS JOIN LATERAL jsonb_array_elements(f.clusters) c CROSS JOIN LATERAL jsonb_array_elements(c->'destinations') d
                    WHERE f."organizationId" = {organization}::text AND f."projectId" = {project}::text
                        AND f."environmentId" = o.environment_id::text AND c->>'environmentId' = f."environmentId"
                        AND c->>'projectId' = f."projectId" AND d->>'clusterId' = c->>'id' AND d->>'enabled' = 'true'
                )
                SELECT DISTINCT ON (w.id, w.runtime_cluster, w.cluster_id, w.destination_id) w.id AS "VersionId",
                    w.runtime_cluster AS "RuntimeCluster", w.cluster_id AS "ClusterId", w.destination_id AS "DestinationId", n.name AS "Name"
                FROM sources w JOIN names n ON n.environment_id = w.environment_id AND n.version_no = w.source_version
                    AND n.cluster_id = w.cluster_id::text AND n.destination_id = w.destination_id::text AND n.name IS NOT NULL
                ORDER BY w.id, w.runtime_cluster, w.cluster_id, w.destination_id, w.source_version DESC
                """).ToArrayAsync(ct);
            foreach (var row in rows) names[new(row.VersionId, row.RuntimeCluster, row.ClusterId, row.DestinationId)] = row.Name;
        }
    }
}

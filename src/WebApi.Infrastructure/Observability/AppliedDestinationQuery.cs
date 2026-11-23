using System.Globalization;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;

namespace WebApi.Infrastructure.Observability;

internal static class AppliedDestinationQuery
{
    private sealed record AppliedNode(Guid EnvironmentId, string NodeName, Guid VersionId,
        DateTimeOffset? AcknowledgedAt, string? CurrentInstanceAppliedAt);
    private sealed record Member(Guid VersionId, Guid? ClusterId, Guid? DestinationId);

    internal static async Task<TrustedObservationScope> WithAppliedDestinationsAsync(this TrustedObservationScope scope,
        WebApiDbContext db, DateTimeOffset? asOf, CancellationToken ct)
    {
        if (asOf is not { } end || scope.ExpectedNodes.Count == 0 || scope.Destinations.Count == 0) return scope;
        var environments = scope.EnvironmentIds.ToArray();
        // A heartbeat can update current_config_version before the current instance has acknowledged it.
        var candidates = await db.Database.SqlQuery<AppliedNode>($"""
            SELECT n.environment_id AS "EnvironmentId", n.node_name AS "NodeName", v.id AS "VersionId",
                a.applied_at AS "AcknowledgedAt",
                CASE WHEN n.metadata->'runtimeApplication' @> jsonb_build_object(
                    'schemaVersion', 1, 'instanceId', n.instance_id, 'releaseId', r.id,
                    'configVersion', v.version_no, 'deploymentSequence', r.deployment_sequence, 'payloadHash', v.snapshot_hash)
                    THEN n.metadata->'runtimeApplication'->>'appliedAt' END AS "CurrentInstanceAppliedAt"
            FROM gateway_nodes n
            JOIN release_records r ON r.environment_id = n.environment_id AND r.deployment_sequence = n.current_deployment_sequence
                AND r.to_config_version = n.current_config_version
            JOIN gateway_config_versions v ON v.environment_id = n.environment_id AND v.version_no = n.current_config_version
            JOIN release_targets t ON t.release_id = r.id AND t.node_id = n.id
            LEFT JOIN gateway_acks a ON a.release_id = t.release_id AND a.node_id = t.node_id AND a.success
                AND a.instance_id = t.instance_id AND a.instance_id = n.instance_id AND a.config_version = v.version_no
                AND a.deployment_sequence = r.deployment_sequence AND a.payload_hash = v.snapshot_hash
            WHERE n.enabled AND n.environment_id = ANY({environments}) AND n.current_deployment_sequence > 0
                AND n.current_config_version > 0 AND v.snapshot_hash IS NOT NULL
            """).ToArrayAsync(ct);
        var confirmed = candidates.Where(n => n.AcknowledgedAt < end ||
            DateTimeOffset.TryParseExact(n.CurrentInstanceAppliedAt, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var applied) && applied < end).ToArray();
        if (confirmed.Length == 0) return scope;

        // Verify the bytes acknowledged by these nodes. The JSONB search copy is not the delivered artifact.
        var versions = confirmed.Select(n => n.VersionId).Distinct().ToArray();
        var members = await db.Database.SqlQuery<Member>($"""
            WITH applied_snapshots AS MATERIALIZED (
                SELECT v.id, snapshot.clusters FROM gateway_config_versions v
                JOIN gateway_config_snapshots s ON s.config_version_id = v.id
                CROSS JOIN LATERAL jsonb_to_record(CASE WHEN octet_length(s.payload_bytes) = s.size_bytes
                    AND encode(sha256(s.payload_bytes), 'hex') = v.snapshot_hash
                    THEN convert_from(s.payload_bytes, 'UTF8')::jsonb END)
                    AS snapshot("environmentId" text, "configVersion" text, clusters jsonb)
                WHERE v.id = ANY({versions}) AND snapshot."environmentId" = v.environment_id::text
                    AND snapshot."configVersion" = v.version_no::text AND jsonb_typeof(snapshot.clusters) = 'array'
            )
            SELECT s.id AS "VersionId", member.cluster_id AS "ClusterId", member.destination_id AS "DestinationId"
            FROM applied_snapshots s
            LEFT JOIN LATERAL (
                SELECT DISTINCT COALESCE(c->>'sourceId', c->>'id')::uuid AS cluster_id, (d->>'id')::uuid AS destination_id
                FROM jsonb_array_elements(s.clusters) c
                CROSS JOIN LATERAL jsonb_array_elements(c->'destinations') d
            ) member ON TRUE
            """).ToArrayAsync(ct);
        var sets = members.GroupBy(row => row.VersionId).ToDictionary(group => group.Key, group =>
            group.Where(row => row.ClusterId.HasValue && row.DestinationId.HasValue)
                .Select(row => (row.ClusterId!.Value, row.DestinationId!.Value)).ToHashSet());
        return scope with { AppliedDestinations = confirmed.Where(n => sets.ContainsKey(n.VersionId)).ToDictionary(
            n => new ExpectedObservationNode(n.EnvironmentId, n.NodeName),
            n => (IReadOnlySet<(Guid, Guid)>)sets[n.VersionId]) };
    }
}

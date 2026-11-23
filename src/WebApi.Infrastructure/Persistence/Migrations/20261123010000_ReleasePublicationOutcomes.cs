using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebApi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WebApiDbContext))]
[Migration(ReleasePublicationOutcomes.Id)]
public sealed class ReleasePublicationOutcomes : Migration
{
    internal const string Id = "20261123010000_ReleasePublicationOutcomes";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            -- Submitted releases already index their version membership. Historical retries
            -- bypassed submission, so recover only their own frozen, project-bound version items.
            DO $backfill$
            DECLARE old_retry record; candidate jsonb; snapshot jsonb; route_versions jsonb; matched integer;
            BEGIN
              PERFORM pg_catalog.pg_advisory_xact_lock(8901202);
              CREATE TEMP TABLE verified_release_outcome_retries(id uuid PRIMARY KEY) ON COMMIT DROP;
              FOR old_retry IN
                SELECT r.id, r.environment_id, r.to_config_version, r.artifact_id, r.candidate_bytes,
                       e.project_id, p.organization_id, snap.payload_bytes
                FROM release_records r
                JOIN release_records parent ON parent.id = r.recovery_of AND parent.environment_id = r.environment_id
                  AND parent.status = 'Failed' AND parent.to_config_version = r.to_config_version
                  AND parent.deployment_sequence < r.deployment_sequence
                  AND parent.artifact_id IS NOT DISTINCT FROM r.artifact_id
                  AND parent.source_release_id IS NOT DISTINCT FROM r.source_release_id
                  AND (r.artifact_id IS NULL OR r.candidate_bytes = parent.candidate_bytes)
                JOIN environments e ON e.id = r.environment_id JOIN projects p ON p.id = e.project_id
                JOIN gateway_config_versions c ON c.environment_id = e.id AND c.version_no = r.to_config_version
                JOIN gateway_config_snapshots snap ON snap.config_version_id = c.id
                  AND octet_length(snap.payload_bytes) = snap.size_bytes AND encode(sha256(snap.payload_bytes),'hex') = c.snapshot_hash
                WHERE r.release_type = 'retry' AND r.rollback_of IS NULL AND r.to_config_version > 0
                  AND r.candidate_bytes IS NOT NULL AND r.approval_policy IS NOT NULL
                  AND EXISTS (SELECT 1 FROM release_targets t WHERE t.release_id = r.id)
              LOOP
                BEGIN
                  candidate := convert_from(old_retry.candidate_bytes, 'UTF8')::jsonb;
                  snapshot := convert_from(old_retry.payload_bytes, 'UTF8')::jsonb;
                EXCEPTION WHEN data_exception THEN CONTINUE;
                END;
                IF candidate->>'environmentId' IS DISTINCT FROM old_retry.environment_id::text
                   OR candidate->>'projectId' IS DISTINCT FROM old_retry.project_id::text
                   OR candidate->>'organizationId' IS DISTINCT FROM old_retry.organization_id::text
                   OR jsonb_typeof(candidate->'versions') IS DISTINCT FROM 'array'
                   OR jsonb_typeof(candidate->'versionIds') IS DISTINCT FROM 'array'
                   OR snapshot->>'environmentId' IS DISTINCT FROM old_retry.environment_id::text
                   OR snapshot->>'configVersion' IS DISTINCT FROM old_retry.to_config_version::text
                   OR jsonb_typeof(snapshot->'routes') IS DISTINCT FROM 'array' THEN
                  CONTINUE;
                END IF;
                -- Ordinary retries rebuild the full historical route set. Promotion retries retain
                -- their parent's exact candidate bytes, which may select only part of that set.
                IF old_retry.artifact_id IS NULL THEN
                  SELECT COALESCE(jsonb_agg(DISTINCT route->'apiVersionId'), '[]') INTO route_versions
                    FROM jsonb_array_elements(snapshot->'routes') route;
                  IF NOT (route_versions @> (candidate->'versionIds') AND route_versions <@ (candidate->'versionIds')) THEN CONTINUE; END IF;
                END IF;
                SELECT COUNT(DISTINCT v.id) INTO matched FROM jsonb_array_elements(candidate->'versions') member
                  JOIN api_versions v ON v.id::text = member#>>'{version,id}' AND v.api_id::text = member#>>'{version,apiId}'
                  JOIN apis a ON a.id = v.api_id AND a.id::text = member#>>'{api,id}'
                    AND a.project_id = old_retry.project_id AND a.organization_id = old_retry.organization_id
                  WHERE candidate->'versionIds' ? v.id::text AND length(member#>>'{version,changeType}') BETWEEN 1 AND 24
                    AND jsonb_typeof(member#>'{version,version}') = 'string' AND jsonb_typeof(member#>'{version,revision}') = 'number';
                IF matched <> jsonb_array_length(candidate->'versionIds') OR matched <> jsonb_array_length(candidate->'versions') THEN CONTINUE; END IF;
                INSERT INTO pg_temp.verified_release_outcome_retries VALUES (old_retry.id);
                INSERT INTO release_items(release_id, resource_type, resource_id, change_type, after_json)
                  SELECT old_retry.id, 'version', v.id, member#>>'{version,changeType}',
                    jsonb_build_object('version',member#>'{version,version}','revision',member#>'{version,revision}')
                  FROM jsonb_array_elements(candidate->'versions') member
                  JOIN api_versions v ON v.id::text = member#>>'{version,id}' AND v.api_id::text = member#>>'{version,apiId}'
                  JOIN apis a ON a.id = v.api_id AND a.id::text = member#>>'{api,id}'
                    AND a.project_id = old_retry.project_id AND a.organization_id = old_retry.organization_id
                  WHERE NOT EXISTS (SELECT 1 FROM release_items i WHERE i.release_id = old_retry.id AND i.resource_type = 'version' AND i.resource_id = v.id);
              END LOOP;
            END $backfill$;

            -- Follow only retries of the same failed rollback snapshot. Require all frozen
            -- nodes of the successful attempt to have confirmed its instance, hash and sequence.
            WITH RECURSIVE attempts AS (
              SELECT rb.rollback_of AS original_id, rb.id, rb.environment_id, rb.to_config_version, rb.deployment_sequence, rb.status
              FROM release_records rb
              JOIN release_records original ON original.id = rb.rollback_of AND original.environment_id = rb.environment_id
                AND original.status = 'Succeeded' AND original.deployment_sequence < rb.deployment_sequence
              WHERE rb.release_type = 'rollback' AND rb.recovery_of IS NULL AND rb.status = 'Failed' AND rb.to_config_version > 0
              UNION ALL
              SELECT prior.original_id, retry.id, retry.environment_id, retry.to_config_version, retry.deployment_sequence, retry.status
              FROM attempts prior
              JOIN release_records retry ON retry.recovery_of = prior.id AND retry.environment_id = prior.environment_id
                AND retry.to_config_version = prior.to_config_version AND retry.deployment_sequence > prior.deployment_sequence
              JOIN pg_temp.verified_release_outcome_retries verified ON verified.id = retry.id
              WHERE prior.status = 'Failed' AND retry.release_type = 'retry' AND retry.rollback_of IS NULL
            )
            UPDATE release_records original SET status = 'RolledBack'
            WHERE original.status = 'Succeeded' AND EXISTS (
              SELECT 1 FROM attempts a
              JOIN gateway_config_versions c ON c.environment_id = a.environment_id AND c.version_no = a.to_config_version
              WHERE a.original_id = original.id AND a.status IN ('Succeeded','RolledBack')
                AND (SELECT COUNT(*) FROM release_targets t WHERE t.release_id = a.id) >= 2
                AND NOT EXISTS (
                  SELECT 1 FROM release_targets t WHERE t.release_id = a.id AND NOT EXISTS (
                    SELECT 1 FROM gateway_acks ack WHERE ack.release_id = a.id AND ack.node_id = t.node_id
                      AND ack.instance_id = t.instance_id AND ack.success AND ack.payload_hash = c.snapshot_hash
                      AND ack.deployment_sequence = a.deployment_sequence AND ack.config_version = a.to_config_version)));

            WITH facts AS (
              SELECT i.resource_id, CASE WHEN bool_or(r.status = 'Publishing') THEN 'Publishing'
                WHEN bool_or(r.status IN ('Succeeded','RolledBack')) THEN 'Published' ELSE 'Draft' END AS status
              FROM release_items i JOIN release_records r ON r.id = i.release_id
              JOIN api_versions v ON v.id = i.resource_id
              JOIN apis a ON a.id = v.api_id JOIN environments e ON e.id = r.environment_id AND e.project_id = a.project_id
              WHERE i.resource_type = 'version' AND r.status IN ('Publishing','Succeeded','RolledBack','Failed')
                AND r.to_config_version > 0 AND r.deployment_sequence > 0 AND v.sealed_at IS NOT NULL
                AND v.status IN ('Publishing','Published')
                AND (r.release_type <> 'retry' OR EXISTS (SELECT 1 FROM pg_temp.verified_release_outcome_retries checked WHERE checked.id = r.id))
              GROUP BY i.resource_id
            )
            UPDATE api_versions v SET status = facts.status FROM facts
            WHERE v.id = facts.resource_id AND v.status IS DISTINCT FROM facts.status
              AND (v.status = 'Publishing' OR facts.status = 'Publishing');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // No schema change. The former ambiguous display values cannot reconstruct the historical facts.
    }
}

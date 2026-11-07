-- G0-4: upgrade-guard precheck for the G-series (credential governance) merge.
--
-- Run against the TARGET database (pre-G schema) BEFORE running the
-- Governance24 migrations. Part A blocks (RAISE) when the migration would
-- abort or the maintenance window is not safe; Part B prints the inventory
-- to record as merge evidence.
--
-- Usage:
--   psql "$CONN" -v ON_ERROR_STOP=1 -f scripts/governance-merge/upgrade-guard-precheck.sql
--
-- Part A #1 and #2 are copies of the preflight that
-- 20261103183543_Governance24CredentialCore runs before its DDL — if they
-- raise here, the migration will abort at the same point; fix the data and
-- re-run instead of guessing an organization or project.
-- Part A #3 is an operational gate (deployment window), not a migration guard.

-- ============ Part A: blocking preflight ============

-- A1/A2: legacy scope consistency (verbatim from Governance24CredentialCore)
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM application_credentials c
             LEFT JOIN applications a ON a.id = c.application_id
             WHERE a.id IS NULL OR a.organization_id IS NULL)
  THEN RAISE EXCEPTION 'credential_governance_preflight_failed: credential scope cannot be established'; END IF;

  IF EXISTS (SELECT 1 FROM application_api_permissions g
             JOIN applications a ON a.id = g.application_id
             JOIN apis i ON i.id = g.api_id
             JOIN environments e ON e.id = g.environment_id
             JOIN projects p ON p.id = e.project_id
             WHERE i.project_id <> e.project_id
                OR i.organization_id <> a.organization_id
                OR p.organization_id <> a.organization_id
                OR (a.project_id IS NOT NULL AND a.project_id <> e.project_id))
  THEN RAISE EXCEPTION 'credential_governance_preflight_failed: inconsistent application permission scope'; END IF;
END $$;

-- A3: no in-flight releases (maintenance window gate; PublishCoordinator
-- freezes all enabled nodes while a release is Building/Publishing)
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM release_records WHERE status IN ('Building', 'Publishing'))
  THEN RAISE EXCEPTION 'deployment_window_not_safe: a release is Building or Publishing — wait for it to converge or roll back before upgrading'; END IF;
END $$;

-- A4: no partially-applied G state (re-running precheck on an already-migrated
-- database must be detectable, not silently re-validated against new schema)
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM information_schema.tables
             WHERE table_name = 'credential_revocations' AND table_schema = 'public')
  THEN RAISE EXCEPTION 'already_migrated: credential_revocations exists — this precheck targets the pre-G schema only'; END IF;
END $$;

SELECT 'PART_A_OK: blocking preflight passed' AS result;

-- ============ Part B: inventory (record as evidence) ============

-- B1: legacy (old-style) revocations. These rows become terminal under the
-- new credential_working_revocation_terminal trigger; they stay Revoked and
-- continue to be excluded from runtime snapshots. The new gateway security
-- enforcement only applies to revocations recorded in the ledger AFTER the
-- merge — list these so the operator knows which revocations predate it.
SELECT c.id, c.access_key, c.secret_last4, c.status, c.revoked_at,
       c.application_id, a.organization_id, a.project_id
FROM application_credentials c
JOIN applications a ON a.id = c.application_id
WHERE c.status = 'Revoked'
ORDER BY c.revoked_at NULLS FIRST;

-- B2: credential counts per organization/project
SELECT a.organization_id, a.project_id,
       count(*) FILTER (WHERE c.status = 'Active')  AS active,
       count(*) FILTER (WHERE c.status = 'Revoked') AS revoked,
       count(*) AS total
FROM application_credentials c
JOIN applications a ON a.id = c.application_id
GROUP BY a.organization_id, a.project_id
ORDER BY a.organization_id, a.project_id;

-- B3: desired config and snapshot schema version per environment
-- (governance requires schema floor '2.0' per environment_security_states.guard)
SELECT e.id, e.code, e.status, e.desired_config_version, e.deployment_sequence,
       s.payload ->> 'schemaVersion' AS schema_version
FROM environments e
LEFT JOIN gateway_config_versions v
       ON v.environment_id = e.id AND v.version_no = e.desired_config_version
LEFT JOIN gateway_config_snapshots s ON s.config_version_id = v.id
ORDER BY e.code;

-- B4: in-flight releases (must be empty — A3 already enforced)
SELECT id, environment_id, status, created_at
FROM release_records
WHERE status IN ('Building', 'Publishing');

-- B5: data sanity (non-blocking; investigate any nonzero count before merging)
SELECT count(*) AS revoked_missing_revoked_at
FROM application_credentials
WHERE status = 'Revoked' AND revoked_at IS NULL;

SELECT count(*) AS expired_still_active
FROM application_credentials
WHERE status = 'Active' AND expires_at < now();

-- B6: gateway node readiness per environment (publishing needs >=2 enabled
-- nodes, all online within the 120s heartbeat grace)
SELECT environment_id,
       count(*) FILTER (WHERE enabled) AS enabled_nodes,
       count(*) FILTER (WHERE enabled AND last_heartbeat_at > now() - interval '120 seconds') AS online_nodes
FROM gateway_nodes
GROUP BY environment_id
ORDER BY environment_id;

SELECT 'PART_B_DONE: review the output above and archive it as merge evidence' AS result;

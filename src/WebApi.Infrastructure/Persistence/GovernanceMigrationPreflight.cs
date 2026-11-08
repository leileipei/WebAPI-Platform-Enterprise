using Microsoft.EntityFrameworkCore;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Rejects a migration history that is not a prefix of this build's known history.</summary>
public static class GovernanceMigrationPreflight
{
    public static async Task ValidateAsync(WebApiDbContext db, CancellationToken ct = default)
    {
        // The legacy SQL length check relies on Unicode code points, whose
        // PostgreSQL interpretation has only been verified for UTF-8.
        var encoding = await db.Database.SqlQueryRaw<string>("SELECT pg_catalog.current_setting('server_encoding') AS \"Value\"").SingleAsync(ct);
        if (!string.Equals(encoding, "UTF8", StringComparison.Ordinal))
            throw new InvalidOperationException("governance_migration_preflight_failed: unsupported database encoding");
        // Catalog functions must resolve before writable schemas throughout the
        // preflight and migration. A private temp namespace may contain only the
        // uniquely named source-capture tables and their generated row types;
        // all other temp objects could shadow unqualified migration objects.
        var safeSearchPath = await db.Database.SqlQueryRaw<bool>("""
            SELECT COALESCE(pg_catalog.current_schema()='public'
                AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_class c
                    WHERE c.relnamespace=pg_catalog.pg_my_temp_schema()
                      AND NOT (c.relkind='r' AND c.relname ~ '^webapi_gov24_[0-9a-f]{{32}}$')
                )
                AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_type t
                    WHERE t.typnamespace=pg_catalog.pg_my_temp_schema()
                      AND NOT EXISTS (
                          SELECT 1 FROM pg_catalog.pg_class c
                          JOIN pg_catalog.pg_type row_type ON row_type.oid=c.reltype
                          WHERE c.relnamespace=t.typnamespace AND c.relkind='r'
                            AND c.relname ~ '^webapi_gov24_[0-9a-f]{{32}}$'
                            AND (t.oid=c.reltype OR t.oid=row_type.typarray)
                      )
                )
                AND pg_catalog.array_position(pg_catalog.current_schemas(true),'pg_catalog')
                    < pg_catalog.array_position(pg_catalog.current_schemas(true),'public'), false) AS "Value"
            """).SingleAsync(ct);
        if (!safeSearchPath) throw new InvalidOperationException("governance_migration_preflight_failed: unsupported search path");
        // Keep historical hashes unchanged. Reject uncovered public routine
        // kinds, table policies and table rules until their baselines are known.
        var unverifiedSchemaObjects = await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='public' AND p.prokind IN ('p','a','w')
            ) OR EXISTS (
                SELECT 1 FROM pg_catalog.pg_policy p
                JOIN pg_catalog.pg_class c ON c.oid=p.polrelid
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='public'
            ) OR EXISTS (
                SELECT 1 FROM pg_catalog.pg_rewrite r
                JOIN pg_catalog.pg_class c ON c.oid=r.ev_class
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='public' AND c.relkind IN ('r','p')
            ) OR EXISTS (
                SELECT 1 FROM pg_catalog.pg_inherits i
                JOIN pg_catalog.pg_class child ON child.oid=i.inhrelid
                JOIN pg_catalog.pg_class parent ON parent.oid=i.inhparent
                JOIN pg_catalog.pg_namespace cn ON cn.oid=child.relnamespace
                JOIN pg_catalog.pg_namespace pn ON pn.oid=parent.relnamespace
                WHERE cn.nspname='public' OR pn.nspname='public'
            ) AS "Value"
            """).SingleAsync(ct);
        if (unverifiedSchemaObjects) throw new InvalidOperationException("governance_migration_preflight_failed: unverified schema object");
        var known = db.Database.GetMigrations().ToArray();
        var applied = await GovernanceMigrationHistory.ReadAppliedAsync(db, ct);
        if (applied.Length > known.Length || !applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
            throw new InvalidOperationException("governance_migration_preflight_failed: unsupported migration history");
        if (applied.Length > 0 && applied.Length < known.Length)
        {
            // pg_stat_activity hides fields belonging to other roles without
            // this privilege; an empty result would not prove they are gone.
            var canInspectActivity = await db.Database.SqlQueryRaw<bool>("""
                SELECT pg_catalog.pg_has_role(current_user,'pg_read_all_stats','USAGE') AS "Value"
                """).SingleAsync(ct);
            if (!canInspectActivity) throw new InvalidOperationException("governance_migration_preflight_failed: activity_visibility_unverified");
            var sameIdentitySession = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity
                    WHERE datname=pg_catalog.current_database() AND usename=session_user AND pid<>pg_catalog.pg_backend_pid()
                      AND backend_type='client backend') AS "Value"
                """).SingleAsync(ct);
            if (sameIdentitySession) throw new InvalidOperationException("governance_migration_preflight_failed: same_identity_session");
            // Another session can retain a previously SET ROLE even after the
            // membership was revoked. pg_stat_activity shows its login identity,
            // not the role currently in use, so all other clients must be gone.
            var otherClientSession = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity
                    WHERE datname=pg_catalog.current_database() AND pid<>pg_catalog.pg_backend_pid()
                      AND backend_type='client backend') AS "Value"
                """).SingleAsync(ct);
            if (otherClientSession) throw new InvalidOperationException("governance_migration_preflight_failed: old_writer_unfenced");
            // Follow SET, ADMIN and SECURITY DEFINER edges. ADMIN can regrant
            // SET. A definer can also be invoked via a pre-bound operator even
            // without source-schema USAGE. Conservatively treat executable or
            // owned definers as reaching their owner's complete privileges.
            // This is necessary but not sufficient for external isolation.
            var unfencedWriter = await db.Database.SqlQueryRaw<bool>("""
                WITH RECURSIVE principals AS (
                    SELECT r.oid
                    FROM pg_catalog.pg_roles r
                    WHERE r.rolname <> session_user
                      AND (r.rolcanlogin OR EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity a WHERE a.datname=pg_catalog.current_database() AND a.usename=r.rolname))
                      AND (pg_catalog.has_database_privilege(r.oid,pg_catalog.current_database(),'CONNECT') OR EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity a WHERE a.datname=pg_catalog.current_database() AND a.usename=r.rolname))
                ), role_edges(source_oid, target_oid) AS (
                    SELECT m.member, m.roleid FROM pg_catalog.pg_auth_members m
                    WHERE m.set_option OR m.admin_option
                    UNION
                    SELECT r.oid, p.proowner
                    FROM pg_catalog.pg_roles r CROSS JOIN pg_catalog.pg_proc p
                    WHERE p.prosecdef AND p.prokind IN ('f','p')
                      AND (pg_catalog.has_function_privilege(r.oid,p.oid,'EXECUTE')
                           OR pg_catalog.pg_has_role(r.oid,p.proowner,'USAGE'))
                ), effective_roles(principal_oid, effective_oid) AS (
                    SELECT p.oid, p.oid FROM principals p
                    UNION
                    SELECT e.principal_oid, edge.target_oid
                    FROM effective_roles e JOIN role_edges edge ON edge.source_oid=e.effective_oid
                )
                SELECT EXISTS (
                    SELECT 1 FROM effective_roles r
                    JOIN pg_catalog.pg_class c ON c.relkind IN ('r','p','v','m','f','S')
                    JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace AND n.nspname='public'
                    JOIN pg_catalog.pg_database d ON d.datname=pg_catalog.current_database()
                    WHERE pg_catalog.pg_has_role(r.effective_oid,c.relowner,'USAGE')
                       OR pg_catalog.pg_has_role(r.effective_oid,n.nspowner,'USAGE')
                       OR pg_catalog.pg_has_role(r.effective_oid,d.datdba,'USAGE')
                       OR pg_catalog.has_schema_privilege(r.effective_oid,n.oid,'CREATE')
                       OR pg_catalog.has_database_privilege(r.effective_oid,d.oid,'CREATE')
                       OR CASE WHEN c.relkind='S'
                           THEN pg_catalog.has_sequence_privilege(r.effective_oid,c.oid,'USAGE, UPDATE')
                           ELSE pg_catalog.has_table_privilege(r.effective_oid,c.oid,'INSERT, UPDATE, DELETE, TRUNCATE, TRIGGER, MAINTAIN')
                                OR pg_catalog.has_any_column_privilege(r.effective_oid,c.oid,'INSERT, UPDATE')
                          END
                ) AS "Value"
                """).SingleAsync(ct);
            if (unfencedWriter) throw new InvalidOperationException("governance_migration_preflight_failed: old_writer_unfenced");
        }
        var hasDraftObjects = await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f') AND c.relname = ANY (ARRAY[
                    'application_expiry_reminders','application_route_method_grants','credential_quota_policies',
                    'credential_revocations','credential_rotation_verifications','credential_rotations',
                    'credential_usage_activation_evidence','credential_usage_batch_receipts','credential_usage_buckets',
                    'credential_usage_instances','credential_usage_lease_evidence','credential_usage_lost_windows',
                    'credential_usage_node_buckets','credential_usage_source_states','environment_security_states',
                    'gateway_security_current_receipts','gateway_security_leases','governance_previews',
                    'permission_request_tasks','permission_requests','security_ack_attempts','security_acks',
                    'security_events','security_operation_alert_checks','security_operation_alerts',
                    'security_operation_targets','security_operations','security_outbox_messages'
                ])
            ) AS "Value"
            """).SingleAsync(ct);
        var hasDraftColumns = await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM (VALUES
                    ('environments','authorization_revision'),('environments','require_environment_credential'),
                    ('applications','authorization_revision'),('applications','owner_revision'),('applications','owner_user_id'),
                    ('application_credentials','binding_mode'),('application_credentials','environment_id'),
                    ('application_credentials','expiry_revision'),('application_credentials','organization_id'),('application_credentials','project_id'),
                    ('application_api_permissions','expiry_revision'),('application_api_permissions','mode'),
                    ('application_api_permissions','revision_transaction_id'),('application_api_permissions','organization_id'),('application_api_permissions','project_id'),
                    ('api_routes','api_id'),('api_routes','authentication_mode'),('api_routes','authorization_revision'),
                    ('notification_deliveries','reminder_id')
                ) AS draft(table_name,column_name)
                JOIN pg_catalog.pg_namespace n ON n.nspname='public'
                JOIN pg_catalog.pg_class c ON c.relnamespace=n.oid AND c.relname=draft.table_name AND c.relkind IN ('r','p')
                JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attname=draft.column_name AND a.attnum>0 AND NOT a.attisdropped
            ) AS "Value"
            """).SingleAsync(ct);
        if ((hasDraftObjects || hasDraftColumns) && !applied.Contains("20261103183543_Governance24CredentialCore", StringComparer.Ordinal))
            throw new InvalidOperationException("governance_migration_preflight_failed: unverified governance objects");
        var actual = await db.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync(ct);
        if (applied.Length == 0)
        {
            if (actual is not ("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" or
                "f8430dab2f6d33ce73425a4ebcfd6fb9aa98ba7d50aa2cb0987d4f1ffcc1ed77"))
                throw new InvalidOperationException("governance_migration_preflight_failed: unverified empty schema");
            var hasUnknownObjects = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f','S') AND c.relname <> '__EFMigrationsHistory'
                ) OR EXISTS (
                    SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public'
                ) OR EXISTS (
                    SELECT 1 FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace
                    WHERE n.nspname='public' AND t.typtype IN ('e','d','r','m')
                ) AS "Value"
                """).SingleAsync(ct);
            if (hasUnknownObjects) throw new InvalidOperationException("governance_migration_preflight_failed: unverified existing schema");
        }
        if (applied.Length > 0)
        {
            if (!GovernanceSchemaFingerprints.Expected.TryGetValue(applied[^1], out var expected))
                throw new InvalidOperationException("governance_migration_preflight_failed: unverified schema version");
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("governance_migration_preflight_failed: schema fingerprint mismatch");
            var missingCore = await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.unnest(ARRAY[
                        'audit_logs','organizations','permissions','users','approval_flows','roles','idempotency_records',
                        'projects','approval_steps','role_permissions','user_roles','api_groups','applications','environments',
                        'policies','apis','application_credentials','gateway_config_versions','gateway_nodes','release_records',
                        'upstream_clusters','user_project_scopes','api_versions','application_api_permissions','gateway_config_snapshots',
                        'gateway_node_events','approval_tasks','outbox_messages','release_items','release_targets',
                        'upstream_destinations','api_parameters','api_routes','api_schemas','gateway_acks','route_methods','route_policy_bindings'
                    ]) AS required(name)
                    WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                        WHERE n.nspname='public' AND c.relname=required.name AND c.relkind IN ('r','p'))
                ) AS "Value"
                """).SingleAsync(ct);
            if (missingCore) throw new InvalidOperationException("governance_migration_preflight_failed: incomplete mainline schema");
        }
    }
}

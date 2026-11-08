-- The standalone script owns its transaction. This guard fails inside a
-- caller transaction before the script can commit unrelated caller work.
DO $WEBAPI_OUTER_TRANSACTION_GUARD$
BEGIN
    COMMIT;
END $WEBAPI_OUTER_TRANSACTION_GUARD$;

START TRANSACTION;

-- Standalone execution has no managed notification budget. It must still
-- explicitly bound the transaction and retained success-receipt rows.
DO $WEBAPI_MIGRATION_BUDGET$
DECLARE
    seconds_text text := pg_catalog.current_setting('webapi.migration_transaction_seconds', true);
    rows_text text := pg_catalog.current_setting('webapi.migration_receipt_max_rows', true);
    attempt_text text := pg_catalog.current_setting('webapi.migration_attempt_id', true);
    budget_ms bigint;
    existing_ms bigint;
    setting_name text;
BEGIN
    IF pg_catalog.current_setting('server_version_num')::integer < 170000 THEN
        RAISE EXCEPTION 'migration_budget_unsupported_server';
    END IF;
    IF seconds_text IS NULL OR seconds_text !~ '^[0-9]{1,4}$' OR rows_text IS NULL OR rows_text !~ '^[0-9]{1,7}$' THEN
        RAISE EXCEPTION 'migration_budget_configuration_invalid';
    END IF;
    IF seconds_text::integer NOT BETWEEN 60 AND 3600 OR rows_text::integer NOT BETWEEN 1 AND 1000000 THEN
        RAISE EXCEPTION 'migration_budget_configuration_invalid';
    END IF;
    budget_ms := seconds_text::bigint * 1000;
    FOREACH setting_name IN ARRAY ARRAY['transaction_timeout','statement_timeout'] LOOP
        SELECT setting::bigint INTO existing_ms FROM pg_catalog.pg_settings WHERE name=setting_name;
        IF NOT FOUND THEN RAISE EXCEPTION 'migration_budget_unsupported_server'; END IF;
        IF existing_ms=0 OR existing_ms>budget_ms THEN
            PERFORM pg_catalog.set_config(setting_name,budget_ms::text || 'ms',true);
        END IF;
    END LOOP;
    IF attempt_text IS NULL OR attempt_text='' THEN
        attempt_text := pg_catalog.gen_random_uuid()::text;
    ELSIF attempt_text !~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN
        RAISE EXCEPTION 'migration_attempt_id_invalid';
    END IF;
    IF attempt_text::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'migration_attempt_id_invalid'; END IF;
    PERFORM pg_catalog.set_config('webapi.migration_attempt_id',attempt_text::uuid::text,false);
    PERFORM pg_catalog.set_config('webapi.migration_receipt_limit_validated',rows_text,true);
    PERFORM pg_catalog.set_config('webapi.migration_timeout_ms_validated',budget_ms::text,true);
END
$WEBAPI_MIGRATION_BUDGET$;

-- Cooperating migration entry points serialize on the same database. A
-- controlled SQL run also supplies its checked journal IDs; direct SQL leaves
-- this setting absent and keeps its existing standalone execution contract.
DO $WEBAPI_EVIDENCE_CONTINUITY$
DECLARE
    journal_text text := pg_catalog.current_setting('webapi.migration_journal_history', true);
    expected uuid[];
    total bigint;
    matched bigint;
    identified bigint;
    cluster text;
    current_database_oid bigint;
    timeout_ms integer;
BEGIN
    SELECT setting::integer INTO timeout_ms FROM pg_catalog.pg_settings WHERE name='lock_timeout';
    IF timeout_ms=0 OR timeout_ms>5000 THEN PERFORM pg_catalog.set_config('lock_timeout','5s',true); END IF;
    PERFORM pg_catalog.pg_advisory_xact_lock(1464156498,1196381253);
    IF journal_text IS NULL OR journal_text='' THEN RETURN; END IF;
    IF pg_catalog.octet_length(journal_text)>5242880 THEN RAISE EXCEPTION 'migration_evidence_history_unverified'; END IF;
    BEGIN
        IF pg_catalog.jsonb_typeof(journal_text::jsonb)<>'array' OR pg_catalog.jsonb_array_length(journal_text::jsonb)>131072 THEN
            RAISE EXCEPTION 'migration_evidence_history_unverified';
        END IF;
        SELECT COALESCE(pg_catalog.array_agg(value::uuid),ARRAY[]::uuid[]) INTO expected
          FROM pg_catalog.jsonb_array_elements_text(journal_text::jsonb) AS value;
        IF pg_catalog.cardinality(expected)<>(SELECT pg_catalog.count(DISTINCT id) FROM pg_catalog.unnest(expected) AS id) THEN
            RAISE EXCEPTION 'migration_evidence_history_unverified';
        END IF;
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'migration_evidence_history_unverified';
    END;
    IF pg_catalog.to_regclass('public.migration_validation_receipts') IS NULL THEN
        IF pg_catalog.cardinality(expected)<>0 THEN RAISE EXCEPTION 'migration_evidence_history_unverified'; END IF;
        RETURN;
    END IF;
    LOCK TABLE public.migration_validation_receipts IN SHARE MODE;
    SELECT pg_catalog.count(*),pg_catalog.count(*) FILTER (WHERE attempt_id=ANY(expected)) INTO total,matched
      FROM public.migration_validation_receipts;
    IF total<>pg_catalog.cardinality(expected) OR matched<>total THEN RAISE EXCEPTION 'migration_evidence_history_unverified'; END IF;
    IF total<>0 THEN
        BEGIN
            SELECT system_identifier::text INTO cluster FROM pg_catalog.pg_control_system();
            SELECT oid::bigint INTO current_database_oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database();
        EXCEPTION WHEN insufficient_privilege THEN
            cluster := NULL;
            current_database_oid := NULL;
        END;
        IF cluster IS NULL OR current_database_oid IS NULL THEN RAISE EXCEPTION 'migration_evidence_history_unverified'; END IF;
        SELECT pg_catalog.count(*) INTO identified FROM public.migration_validation_receipts
          WHERE identity_state='Observed' AND cluster_identifier=cluster
            AND database_name=pg_catalog.current_database() AND migration_validation_receipts.database_oid=current_database_oid;
        IF identified<>total THEN RAISE EXCEPTION 'migration_evidence_history_unverified'; END IF;
    END IF;
END
$WEBAPI_EVIDENCE_CONTINUITY$;

DO $WEBAPI_PREFLIGHT$
DECLARE
    known CONSTANT text[] := ARRAY[
        '20261003182012_InitialCore',
        '20261003201632_PublishCoordinatorIdentity',
        '20261003213457_RecoveryReference',
        '20261004030000_ObservabilityAlerts',
        '20261005090000_SystemSettings',
        '20261005120000_SsoOidc',
        '20261005123210_SsoDisplayEmail',
        '20261005152401_SsoSecretVersion',
        '20261006090000_ApiVersionComparisons',
        '20261006165518_ContractManagement',
        '20261008020000_ApprovalInboxIndexes',
        '20261008030000_ExternalNotifications',
        '20261009010000_EnvironmentAccessAddresses',
        '20261009011000_ReleaseAccessContexts',
        '20261009020000_ReleaseDelivery',
        '20261009030000_ReleasePipelines',
        '20261010010000_PipelineProjectionCursor',
        '20261010091247_GatewayQueryIndexes',
        '20261012004548_ReleaseFrozenContracts',
        '20261015010000_AccountPasswordLifecycle',
        '20261015020000_CredentialUsageTracking',
        '20261103183543_Governance24CredentialCore',
        '20261103184504_Governance24EnvironmentTrust',
        '20261103185114_Governance24RouteMethods',
        '20261103185741_Governance24SecurityLedger',
        '20261103190424_Governance24TrustLedgerBinding',
        '20261103191515_Governance24SourceReceipt',
        '20261104010000_Governance24LegacyPermissionMove',
        '20261104020000_Governance24RegistrationHistory',
        '20261104030000_Governance24LeaseIssuance',
        '20261104040000_Governance24AckVerification',
        '20261104050000_Governance24AckRevalidation',
        '20261104060000_Governance24FailureObservation',
        '20261104070000_Governance24LeaseExpiryObservation',
        '20261104080000_Governance24DatabaseClock',
        '20261104090000_Governance24MigrationReceipts'
    ];
    expected_fingerprints CONSTANT text[] := ARRAY[
        'fe52425b83f9af0a549474b72f66d9fa289e21439476cad5c55b3915d186abf7',
        'a617755e26fdb12067dd6841567a630bb270de8c702abd4491c5b94ad7138983',
        '0212ebfc95f13892e5cf31ef8046e57de0db4e5a53a7ae0ff46f791303a2ca51',
        '8f1db7ab46bdf8aee98b7eaa12d94e95d1d155ed08a0079654222955c44b697c',
        '9f26f635180641eecd18c74004f036dade54fab0185115c43f9faf40e6647239',
        '0de24a53b793610521b20a81f1a118162a4c8f3888fe616532c99e8cfe18c95b',
        '2c7aa41de00dd3532a80d6e6d263d0b6bcd7afc27132acdd62a6773feb065949',
        '8837d28545ac4debb271bc6db26adab8ccf53bae232ec664d3b646ffa62d9169',
        '2d6ca65b43e2a2528403e11839caca9b6541d7abd2c0267d05fc513c67628a21',
        'a5c22b728870446573cc809e9922c97ba01120c432accb86a66d3272131b49a0',
        'c940a2a82b167200c6fd7de8ca4c31b5b945993611dc114527ee22116d143370',
        '0a44412db53e587ee76a2fd50bb96ae5847e9cce4e02736eee2b95739ecd33dc',
        '9bec84df3efb17dd12ec6a1ec3aa38174c6e3c7c85b0ad323da1d66967dd323f',
        '8b60204041d0b5b56c156efb6717cda984a47b9cc3043788976c53eadb79f63f',
        '576c4931ea93b3e04946d96180c3e8772ac61d31d01a1ce24d1a6b57fb442a4a',
        'fea6abd90bf547e102d1e0393116778d4386645823ac9d54debec7ee3e9678c6',
        '0ee9b8dfc47768d2ed7a4ffc38f994aa096847358618bd6e99bbe74e3b063af2',
        'c5f0183ba93eeff1531997c7db642e7d3fc4287c072fc3a8240fea08fb9cb864',
        'daf385188ac734468e7b6d0c16dde4600175a0c45a8ab66168c8cadc657ea04f',
        '6987551e4582450323242cc56ac4a461c52a6c8d2f15c7804cd7600784a90714',
        'b9676942e2e462605b1f00d56dd5e27dcf2910c8b9a78c812383a65079a31560',
        '9c96618dcba54dab59ee3a740e91970d48284abfaae6cb29dd8001c00a3fcfbd',
        '26c001b3c0e5cd57ed33af97d1c91a12a32154e75fb00abb8e835ef5d3479419',
        '80083dfb61ce0614cb8e00117d2eb6700373bf021093f3fe436cd14c6fe9b66b',
        '2d7c0ec9707b8fae5654b11b4a7467843973d0a2292da60b3f6d1c923d442ac3',
        '3f2537f9c04b51de748a908ff46e617280e7eb53774e7fdb2709d52eb1ce06ec',
        '64639c7c941f1e7d9383793b4be0c40e5fe1a09fa9c6550d5c98b8de7148913f',
        '21c61fde4ca30060aebeea608252826716138b94dde58054ce3b1f8368ee4e41',
        '9e4a91d7eb05bd48c341cc1629873bfd05a849be3d41d9db0bf32461a6985b2f',
        'db2f4200c0f71b86983a9e83070822262f99039f8ebfcc7d356a6ee7b064e058',
        'd00bf5921f0625a6e05273027728b249219d6cbecb6f15e28171d8b95797fcc1',
        '3c270ca544dddb38008c81326919c55c90b7d9d7f5ecd83bb9979f52c4850d36',
        '7024ec9146dee7db4543876aec46ec14d0ac89dec7833888fc44b7c9b65ddff4',
        '3f978fb52f0009a5ccde4aa35c91e32755913ec1568dec2676c267a366383ea0',
        '2ed7307ab78b49bd8c1cca47ed5af4a1567460bb2a2b4fb9214f6d5011f93aec',
        '22543989e453281197c4f6e4ca568823de70b528e7abfd55efc0cdde83302309'
    ];
    applied text[] := ARRAY[]::text[];
    actual_fingerprint text;
    source_cluster text;
    source_table text;
    source_database_oid bigint;
    source_history_hash text;
    fence_target record;
    fence_timeout_ms integer;
BEGIN
    IF pg_catalog.current_setting('server_encoding') IS DISTINCT FROM 'UTF8' THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: unsupported database encoding';
    END IF;
    IF pg_catalog.current_schema() IS DISTINCT FROM 'public' OR EXISTS (
        SELECT 1 FROM pg_catalog.pg_class c
        WHERE c.relnamespace=pg_catalog.pg_my_temp_schema()
          AND NOT (c.relkind='r' AND c.relname ~ '^webapi_gov24_[0-9a-f]{32}$')
    ) OR EXISTS (
        SELECT 1 FROM pg_catalog.pg_type t
        WHERE t.typnamespace=pg_catalog.pg_my_temp_schema()
          AND NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_class c
              JOIN pg_catalog.pg_type row_type ON row_type.oid=c.reltype
              WHERE c.relnamespace=t.typnamespace AND c.relkind='r'
                AND c.relname ~ '^webapi_gov24_[0-9a-f]{32}$'
                AND (t.oid=c.reltype OR t.oid=row_type.typarray)
          )
    ) OR NOT COALESCE(
        pg_catalog.array_position(pg_catalog.current_schemas(true),'pg_catalog')
            < pg_catalog.array_position(pg_catalog.current_schemas(true),'public'), false) THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: unsupported search path';
    END IF;
    -- Bound every lock wait for this transaction. Hold the history lock even
    -- on no-op runs; business tables need protection only for pending work.
    SELECT setting::integer INTO fence_timeout_ms FROM pg_catalog.pg_settings WHERE name='lock_timeout';
    IF fence_timeout_ms=0 OR fence_timeout_ms>5000 THEN
        PERFORM pg_catalog.set_config('lock_timeout','5s',true);
    END IF;
    IF pg_catalog.to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
        EXECUTE 'LOCK TABLE ONLY public."__EFMigrationsHistory" IN SHARE MODE';
        EXECUTE 'SELECT COALESCE(pg_catalog.array_agg("MigrationId" ORDER BY "MigrationId"), ARRAY[]::text[]) FROM public."__EFMigrationsHistory"' INTO applied;
    END IF;
    IF applied IS DISTINCT FROM known THEN
        -- Existing rows remain stable until this entire script commits.
        -- External old-writer and privileged-role isolation remains required.
        FOR fence_target IN
            SELECT n.nspname, c.relname FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relkind IN ('r','p')
            ORDER BY c.relname COLLATE "C"
        LOOP
            EXECUTE pg_catalog.format('LOCK TABLE ONLY %I.%I IN SHARE MODE',fence_target.nspname,fence_target.relname);
        END LOOP;
    END IF;
    -- These public objects are outside the historical fingerprint.
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
               WHERE n.nspname='public' AND p.prokind IN ('p','a','w'))
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_policy p JOIN pg_catalog.pg_class c ON c.oid=p.polrelid
                  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public')
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_rewrite r JOIN pg_catalog.pg_class c ON c.oid=r.ev_class
                  JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p'))
       OR EXISTS (SELECT 1 FROM pg_catalog.pg_inherits i
                  JOIN pg_catalog.pg_class child ON child.oid=i.inhrelid JOIN pg_catalog.pg_class parent ON parent.oid=i.inhparent
                  JOIN pg_catalog.pg_namespace cn ON cn.oid=child.relnamespace JOIN pg_catalog.pg_namespace pn ON pn.oid=parent.relnamespace
                  WHERE cn.nspname='public' OR pn.nspname='public') THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: unverified schema object';
    END IF;
    IF pg_catalog.to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
        EXECUTE 'SELECT COALESCE(pg_catalog.array_agg("MigrationId" ORDER BY "MigrationId"), ARRAY[]::text[]) FROM public."__EFMigrationsHistory"' INTO applied;
        IF pg_catalog.cardinality(applied) > pg_catalog.cardinality(known) OR EXISTS (
            SELECT 1 FROM pg_catalog.unnest(applied) WITH ORDINALITY AS entry(id, ordinal)
            WHERE known[entry.ordinal::integer] IS DISTINCT FROM entry.id
        ) THEN
            RAISE EXCEPTION 'governance_migration_preflight_failed: unsupported migration history';
        END IF;
        -- Other reachable writers must be fenced before a governance upgrade.
        -- ADMIN can regrant SET; executable SECURITY DEFINER routines are
        -- conservatively treated as reaching their owner's privileges.
        -- This check alone does not prove external or same-account isolation.
        IF pg_catalog.cardinality(applied) > 0 AND pg_catalog.cardinality(applied) < pg_catalog.cardinality(known)
           AND NOT pg_catalog.pg_has_role(current_user,'pg_read_all_stats','USAGE') THEN
            RAISE EXCEPTION 'governance_migration_preflight_failed: activity_visibility_unverified';
        END IF;
        IF pg_catalog.cardinality(applied) > 0 AND pg_catalog.cardinality(applied) < pg_catalog.cardinality(known) AND EXISTS (
            SELECT 1 FROM pg_catalog.pg_stat_activity
            WHERE datname=pg_catalog.current_database() AND usename=session_user AND pid<>pg_catalog.pg_backend_pid()
              AND backend_type='client backend'
        ) THEN
            RAISE EXCEPTION 'governance_migration_preflight_failed: same_identity_session';
        END IF;
        -- Existing clients may retain a role whose membership was revoked;
        -- pg_stat_activity exposes their login, not their current SET ROLE.
        IF pg_catalog.cardinality(applied) > 0 AND pg_catalog.cardinality(applied) < pg_catalog.cardinality(known) AND EXISTS (
            SELECT 1 FROM pg_catalog.pg_stat_activity
            WHERE datname=pg_catalog.current_database() AND pid<>pg_catalog.pg_backend_pid()
              AND backend_type='client backend'
        ) THEN
            RAISE EXCEPTION 'governance_migration_preflight_failed: old_writer_unfenced';
        END IF;
        IF pg_catalog.cardinality(applied) > 0 AND pg_catalog.cardinality(applied) < pg_catalog.cardinality(known) AND EXISTS (
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
        ) THEN
            RAISE EXCEPTION 'governance_migration_preflight_failed: old_writer_unfenced';
        END IF;
        IF pg_catalog.cardinality(applied) > 0 AND EXISTS (
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
        ) THEN
            RAISE EXCEPTION 'governance_migration_preflight_failed: incomplete mainline schema';
        END IF;
    END IF;
    IF EXISTS (
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
    ) AND NOT ('20261103183543_Governance24CredentialCore' = ANY(applied)) THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: unverified governance objects';
    END IF;
    IF EXISTS (
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
    ) AND NOT ('20261103183543_Governance24CredentialCore' = ANY(applied)) THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: unverified governance objects';
    END IF;
    -- Only two exact, reviewed SQL-installer function bodies are mapped to the
    -- equivalent EF indentation; no general function-body normalization is allowed.
    WITH facts(fact) AS (
        SELECT pg_catalog.jsonb_build_array('relation',c.relname,c.relkind::text,c.relpersistence::text,c.relrowsecurity,c.relforcerowsecurity)::text
        FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f')
        UNION ALL
        SELECT pg_catalog.jsonb_build_array('column',c.relname,a.attnum,a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),
            a.attnotnull,a.attidentity::text,a.attgenerated::text,pg_catalog.pg_get_expr(d.adbin,d.adrelid))::text
        FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
        JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
        WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f') AND a.attnum>0 AND NOT a.attisdropped
        UNION ALL
        SELECT pg_catalog.jsonb_build_array('constraint',c.relname,k.conname,k.contype::text,pg_catalog.pg_get_constraintdef(k.oid,true),
            k.convalidated,k.condeferrable,k.condeferred)::text
        FROM pg_catalog.pg_constraint k JOIN pg_catalog.pg_class c ON c.oid=k.conrelid
        JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
        UNION ALL
        SELECT pg_catalog.jsonb_build_array('index',c.relname,i.relname,pg_catalog.pg_get_indexdef(i.oid),x.indisunique,x.indisprimary,x.indisvalid)::text
        FROM pg_catalog.pg_index x JOIN pg_catalog.pg_class c ON c.oid=x.indrelid
        JOIN pg_catalog.pg_class i ON i.oid=x.indexrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='public'
        UNION ALL
        SELECT pg_catalog.jsonb_build_array('trigger',c.relname,t.tgname,pg_catalog.pg_get_triggerdef(t.oid,true),t.tgenabled::text)::text
        FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
        JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND NOT t.tgisinternal
        UNION ALL
        SELECT pg_catalog.jsonb_build_array('function',p.proname,pg_catalog.pg_get_function_identity_arguments(p.oid),CASE WHEN p.proname='forbid_notification_profile_mutation' AND pg_catalog.pg_get_function_identity_arguments(p.oid)='' AND pg_catalog.pg_get_functiondef(p.oid)=pg_catalog.convert_from(pg_catalog.decode('Q1JFQVRFIE9SIFJFUExBQ0UgRlVOQ1RJT04gcHVibGljLmZvcmJpZF9ub3RpZmljYXRpb25fcHJvZmlsZV9tdXRhdGlvbigpCiBSRVRVUk5TIHRyaWdnZXIKIExBTkdVQUdFIHBscGdzcWwKQVMgJGZ1bmN0aW9uJAogICAgQkVHSU4gUkFJU0UgRVhDRVBUSU9OICdOb3RpZmljYXRpb24gcHJvZmlsZXMgYXJlIGltbXV0YWJsZScgVVNJTkcgRVJSQ09ERT0nMjM1MTQnOyBFTkQgJGZ1bmN0aW9uJAo=','base64'),'UTF8') THEN pg_catalog.convert_from(pg_catalog.decode('Q1JFQVRFIE9SIFJFUExBQ0UgRlVOQ1RJT04gcHVibGljLmZvcmJpZF9ub3RpZmljYXRpb25fcHJvZmlsZV9tdXRhdGlvbigpCiBSRVRVUk5TIHRyaWdnZXIKIExBTkdVQUdFIHBscGdzcWwKQVMgJGZ1bmN0aW9uJApCRUdJTiBSQUlTRSBFWENFUFRJT04gJ05vdGlmaWNhdGlvbiBwcm9maWxlcyBhcmUgaW1tdXRhYmxlJyBVU0lORyBFUlJDT0RFPScyMzUxNCc7IEVORCAkZnVuY3Rpb24kCg==','base64'),'UTF8') WHEN p.proname='pipeline_immutable_fact' AND pg_catalog.pg_get_function_identity_arguments(p.oid)='' AND pg_catalog.pg_get_functiondef(p.oid)=pg_catalog.convert_from(pg_catalog.decode('Q1JFQVRFIE9SIFJFUExBQ0UgRlVOQ1RJT04gcHVibGljLnBpcGVsaW5lX2ltbXV0YWJsZV9mYWN0KCkKIFJFVFVSTlMgdHJpZ2dlcgogTEFOR1VBR0UgcGxwZ3NxbApBUyAkZnVuY3Rpb24kCiAgICBCRUdJTgogICAgICAgIFJBSVNFIEVYQ0VQVElPTiAnUGlwZWxpbmUgdmVyc2lvbnMgYW5kIGV2ZW50cyBhcmUgYXBwZW5kLW9ubHknIFVTSU5HIEVSUkNPREU9JzIzNTE0JzsKICAgIEVORDsKICAgICRmdW5jdGlvbiQK','base64'),'UTF8') THEN pg_catalog.convert_from(pg_catalog.decode('Q1JFQVRFIE9SIFJFUExBQ0UgRlVOQ1RJT04gcHVibGljLnBpcGVsaW5lX2ltbXV0YWJsZV9mYWN0KCkKIFJFVFVSTlMgdHJpZ2dlcgogTEFOR1VBR0UgcGxwZ3NxbApBUyAkZnVuY3Rpb24kCkJFR0lOCiAgICBSQUlTRSBFWENFUFRJT04gJ1BpcGVsaW5lIHZlcnNpb25zIGFuZCBldmVudHMgYXJlIGFwcGVuZC1vbmx5JyBVU0lORyBFUlJDT0RFPScyMzUxNCc7CkVORDsKJGZ1bmN0aW9uJAo=','base64'),'UTF8') ELSE pg_catalog.pg_get_functiondef(p.oid) END)::text
        FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND p.prokind='f'
        UNION ALL
        SELECT pg_catalog.jsonb_build_array('sequence',c.relname,s.seqstart,s.seqincrement,s.seqmax,s.seqmin,s.seqcache,s.seqcycle)::text
        FROM pg_catalog.pg_sequence s JOIN pg_catalog.pg_class c ON c.oid=s.seqrelid
        JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
        UNION ALL
        SELECT pg_catalog.jsonb_build_array('type',t.typname,t.typtype::text,pg_catalog.format_type(t.oid,NULL))::text
        FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace
        WHERE n.nspname='public' AND t.typtype IN ('e','d','r','m')
    )
    SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(COALESCE(pg_catalog.string_agg(fact,E'\n' ORDER BY fact COLLATE "C"),''),'UTF8')),'hex') INTO actual_fingerprint FROM facts;
    IF pg_catalog.cardinality(applied) > 0 AND actual_fingerprint IS DISTINCT FROM expected_fingerprints[pg_catalog.cardinality(applied)] THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: schema fingerprint mismatch';
    ELSIF pg_catalog.cardinality(applied) = 0 AND actual_fingerprint NOT IN (
        'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
        'f8430dab2f6d33ce73425a4ebcfd6fb9aa98ba7d50aa2cb0987d4f1ffcc1ed77'
    ) THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: unverified empty schema';
    END IF;
    IF pg_catalog.cardinality(applied) = 0 AND (
        EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f','S') AND c.relname <> '__EFMigrationsHistory')
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public')
        OR EXISTS (SELECT 1 FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace
            WHERE n.nspname='public' AND t.typtype IN ('e','d','r','m'))
    ) THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: unverified existing schema';
    END IF;
    -- Existing notification data must be checked by the managed application
    -- validator on the migration connection. The standalone SQL installer
    -- cannot provide that guarantee and refuses before any migration write.
    IF applied IS DISTINCT FROM known THEN
      DECLARE legacy_rule_id uuid;
      BEGIN
        IF pg_catalog.to_regclass('public.system_settings') IS NOT NULL THEN
          IF EXISTS (SELECT 1 FROM public.system_settings s WHERE s.key='system.notification') THEN
            RAISE EXCEPTION 'governance_migration_preflight_failed: managed_notification_validation_required: system.notification';
          END IF;
        END IF;
        IF pg_catalog.to_regclass('public.alert_rules') IS NOT NULL THEN
          SELECT id INTO legacy_rule_id FROM public.alert_rules ORDER BY id LIMIT 1;
          IF legacy_rule_id IS NOT NULL THEN
            RAISE EXCEPTION 'governance_migration_preflight_failed: managed_notification_validation_required: rule %',legacy_rule_id;
          END IF;
        END IF;
      END;
    END IF;
    PERFORM pg_catalog.set_config('webapi.migration_source_version',COALESCE(applied[pg_catalog.cardinality(applied)],''),true);
    PERFORM pg_catalog.set_config('webapi.migration_source_schema_hash',actual_fingerprint,true);
    PERFORM pg_catalog.set_config('webapi.migration_manifest_hash',
        pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(pg_catalog.array_to_string(known,E'\n'),'UTF8')),'hex'),true);
    PERFORM pg_catalog.set_config('webapi.migration_validation_state',
        CASE WHEN applied IS DISTINCT FROM known THEN 'NotApplicable' ELSE 'Unknown' END,true);
    -- This is a source-shape receipt, not proof of deployment history or node state.
    PERFORM pg_catalog.set_config('webapi.gov24_source','',false);
    IF pg_catalog.cardinality(applied) BETWEEN 1 AND 21 THEN
        BEGIN
            SELECT system_identifier::text INTO source_cluster FROM pg_catalog.pg_control_system();
        EXCEPTION WHEN insufficient_privilege THEN
            source_cluster := NULL;
        END;
        IF source_cluster IS NOT NULL THEN
            SELECT oid::bigint INTO source_database_oid FROM pg_database WHERE datname=pg_catalog.current_database();
            SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(COALESCE(pg_catalog.json_agg(pg_catalog.json_build_array("MigrationId","ProductVersion") ORDER BY "MigrationId")::text,'[]'),'UTF8')),'hex')
              INTO source_history_hash FROM public."__EFMigrationsHistory";
            source_table := 'webapi_gov24_'||pg_catalog.replace(pg_catalog.gen_random_uuid()::text,'-','');
            EXECUTE pg_catalog.format('CREATE TEMP TABLE %I ON COMMIT PRESERVE ROWS AS SELECT e.id AS environment_id,e.project_id,p.organization_id FROM public.environments e JOIN public.projects p ON p.id=e.project_id',source_table);
            PERFORM pg_catalog.set_config('webapi.gov24_source',jsonb_build_object(
                'table',source_table,'database',pg_catalog.current_database(),'databaseOid',source_database_oid,
                'clusterId',source_cluster,'migration',applied[pg_catalog.cardinality(applied)],
                'schemaHash',actual_fingerprint,'historyHash',source_history_hash,'capturedAt',pg_catalog.clock_timestamp()
            )::text,false);
        END IF;
    END IF;
END
$WEBAPI_PREFLIGHT$;

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

-- WEBAPI_MIGRATION_START 01

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE audit_logs (
        id bigint GENERATED BY DEFAULT AS IDENTITY,
        organization_id uuid,
        project_id uuid,
        environment_id uuid,
        user_id uuid,
        action varchar(128) NOT NULL,
        resource_type varchar(64) NOT NULL,
        resource_id varchar(128) NOT NULL,
        before_json jsonb,
        after_json jsonb,
        ip inet,
        trace_id varchar(64),
        created_at timestamptz NOT NULL,
        CONSTRAINT "PK_audit_logs" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE organizations (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        code varchar(64) NOT NULL,
        name varchar(128) NOT NULL,
        status varchar(24) NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_organizations" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE permissions (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        code varchar(128) NOT NULL,
        module varchar(64) NOT NULL,
        name varchar(128) NOT NULL,
        description text NOT NULL,
        CONSTRAINT "PK_permissions" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE users (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        username varchar(128) NOT NULL,
        display_name varchar(128) NOT NULL,
        email varchar(256),
        password_hash varchar(256),
        status varchar(24) NOT NULL,
        auth_source varchar(24) NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        security_stamp varchar(128) NOT NULL DEFAULT (gen_random_uuid()::text),
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_users" PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE approval_flows (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        organization_id uuid NOT NULL,
        name varchar(128) NOT NULL,
        scope_type varchar(32) NOT NULL,
        enabled boolean NOT NULL DEFAULT TRUE,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_approval_flows" PRIMARY KEY (id),
        CONSTRAINT "FK_approval_flows_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE roles (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        organization_id uuid,
        code varchar(64) NOT NULL,
        name varchar(128) NOT NULL,
        is_system boolean NOT NULL,
        created_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_roles" PRIMARY KEY (id),
        CONSTRAINT "FK_roles_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE idempotency_records (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        actor_id uuid NOT NULL,
        scope_key varchar(256) NOT NULL,
        operation varchar(128) NOT NULL,
        key varchar(128) NOT NULL,
        request_hash varchar(128) NOT NULL,
        response_bytes bytea,
        created_at timestamptz NOT NULL,
        CONSTRAINT "PK_idempotency_records" PRIMARY KEY (id),
        CONSTRAINT "FK_idempotency_records_users_actor_id" FOREIGN KEY (actor_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE projects (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        organization_id uuid NOT NULL,
        code varchar(64) NOT NULL,
        name varchar(128) NOT NULL,
        owner_user_id uuid,
        status varchar(24) NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_projects" PRIMARY KEY (id),
        CONSTRAINT "AK_projects_id_organization_id" UNIQUE (id, organization_id),
        CONSTRAINT "FK_projects_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_projects_users_owner_user_id" FOREIGN KEY (owner_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE approval_steps (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        flow_id uuid NOT NULL,
        step_order int NOT NULL,
        role_code varchar(64) NOT NULL,
        required_count int NOT NULL,
        CONSTRAINT "PK_approval_steps" PRIMARY KEY (id),
        CONSTRAINT "FK_approval_steps_approval_flows_flow_id" FOREIGN KEY (flow_id) REFERENCES approval_flows (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE role_permissions (
        role_id uuid NOT NULL,
        permission_id uuid NOT NULL,
        CONSTRAINT "PK_role_permissions" PRIMARY KEY (role_id, permission_id),
        CONSTRAINT "FK_role_permissions_permissions_permission_id" FOREIGN KEY (permission_id) REFERENCES permissions (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_role_permissions_roles_role_id" FOREIGN KEY (role_id) REFERENCES roles (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE user_roles (
        user_id uuid NOT NULL,
        role_id uuid NOT NULL,
        CONSTRAINT "PK_user_roles" PRIMARY KEY (user_id, role_id),
        CONSTRAINT "FK_user_roles_roles_role_id" FOREIGN KEY (role_id) REFERENCES roles (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_user_roles_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE api_groups (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        project_id uuid NOT NULL,
        name varchar(128) NOT NULL,
        parent_id uuid,
        sort_order int NOT NULL,
        created_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_api_groups" PRIMARY KEY (id),
        CONSTRAINT "AK_api_groups_id_project_id" UNIQUE (id, project_id),
        CONSTRAINT "FK_api_groups_projects_project_id" FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE applications (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        organization_id uuid NOT NULL,
        project_id uuid,
        code varchar(64) NOT NULL,
        name varchar(128) NOT NULL,
        owner varchar(128) NOT NULL,
        status varchar(24) NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_applications" PRIMARY KEY (id),
        CONSTRAINT "FK_applications_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_applications_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE environments (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        project_id uuid NOT NULL,
        code varchar(32) NOT NULL,
        name varchar(64) NOT NULL,
        is_production boolean NOT NULL,
        sort_order int NOT NULL,
        status varchar(24) NOT NULL,
        release_policy_id uuid,
        desired_config_version bigint,
        deployment_sequence bigint NOT NULL DEFAULT 0,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_environments" PRIMARY KEY (id),
        CONSTRAINT "AK_environments_id_project_id" UNIQUE (id, project_id),
        CONSTRAINT ck_deployment_sequence CHECK (deployment_sequence >= 0),
        CONSTRAINT "FK_environments_projects_project_id" FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE policies (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        organization_id uuid NOT NULL,
        project_id uuid,
        name varchar(128) NOT NULL,
        type varchar(48) NOT NULL,
        config jsonb NOT NULL,
        enabled boolean NOT NULL DEFAULT TRUE,
        version_no bigint NOT NULL DEFAULT 1,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        CONSTRAINT "PK_policies" PRIMARY KEY (id),
        CONSTRAINT "FK_policies_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_policies_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE apis (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        group_id uuid,
        code varchar(64) NOT NULL,
        name varchar(128) NOT NULL,
        description text,
        lifecycle_status varchar(24) NOT NULL,
        owner_user_id uuid NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        version_no bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_apis" PRIMARY KEY (id),
        CONSTRAINT "FK_apis_api_groups_group_id_project_id" FOREIGN KEY (group_id, project_id) REFERENCES api_groups (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_apis_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_apis_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_apis_users_owner_user_id" FOREIGN KEY (owner_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE application_credentials (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        application_id uuid NOT NULL,
        access_key varchar(128) NOT NULL,
        secret_hash varchar(256) NOT NULL,
        secret_last4 varchar(8) NOT NULL,
        status varchar(24) NOT NULL,
        valid_from timestamptz NOT NULL,
        expires_at timestamptz NOT NULL,
        last_used_at timestamptz,
        revoked_at timestamptz,
        created_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_application_credentials" PRIMARY KEY (id),
        CONSTRAINT ck_credential_window CHECK (expires_at > valid_from),
        CONSTRAINT "FK_application_credentials_applications_application_id" FOREIGN KEY (application_id) REFERENCES applications (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE gateway_config_versions (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        environment_id uuid NOT NULL,
        version_no bigint NOT NULL,
        status varchar(24) NOT NULL,
        snapshot_key varchar(256),
        snapshot_hash varchar(128),
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL,
        published_at timestamptz,
        CONSTRAINT "PK_gateway_config_versions" PRIMARY KEY (id),
        CONSTRAINT ck_config_version CHECK (version_no > 0),
        CONSTRAINT "FK_gateway_config_versions_environments_environment_id" FOREIGN KEY (environment_id) REFERENCES environments (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_config_versions_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE gateway_nodes (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        environment_id uuid NOT NULL,
        node_name varchar(128) NOT NULL,
        instance_id varchar(128) NOT NULL,
        app_version varchar(64) NOT NULL,
        current_config_version bigint NOT NULL,
        target_config_version bigint,
        status varchar(24) NOT NULL,
        last_heartbeat_at timestamptz,
        metadata jsonb,
        current_deployment_sequence bigint NOT NULL DEFAULT 0,
        identity_hash varchar(256) NOT NULL,
        enabled boolean NOT NULL DEFAULT TRUE,
        CONSTRAINT "PK_gateway_nodes" PRIMARY KEY (id),
        CONSTRAINT "FK_gateway_nodes_environments_environment_id" FOREIGN KEY (environment_id) REFERENCES environments (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE release_records (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        environment_id uuid NOT NULL,
        release_no varchar(64) NOT NULL,
        from_config_version bigint NOT NULL,
        to_config_version bigint NOT NULL,
        release_type varchar(24) NOT NULL,
        status varchar(24) NOT NULL,
        requested_by uuid NOT NULL,
        approved_by uuid,
        created_at timestamptz NOT NULL,
        completed_at timestamptz,
        rollback_of uuid,
        deployment_sequence bigint DEFAULT 0,
        baseline_config_version bigint NOT NULL DEFAULT 0,
        candidate_bytes bytea,
        approval_policy jsonb,
        deadline_at timestamptz,
        failure_code varchar(128),
        CONSTRAINT "PK_release_records" PRIMARY KEY (id),
        CONSTRAINT "FK_release_records_environments_environment_id" FOREIGN KEY (environment_id) REFERENCES environments (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_records_release_records_rollback_of" FOREIGN KEY (rollback_of) REFERENCES release_records (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_records_users_approved_by" FOREIGN KEY (approved_by) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_records_users_requested_by" FOREIGN KEY (requested_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE upstream_clusters (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        project_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        name varchar(128) NOT NULL,
        load_balancing_policy varchar(32) NOT NULL,
        health_check_enabled boolean NOT NULL,
        health_check_path varchar(256) NOT NULL,
        health_check_interval_sec int NOT NULL,
        status varchar(24) NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_upstream_clusters" PRIMARY KEY (id),
        CONSTRAINT "AK_upstream_clusters_id_environment_id" UNIQUE (id, environment_id),
        CONSTRAINT "FK_upstream_clusters_environments_environment_id_project_id" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_upstream_clusters_projects_project_id" FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE user_project_scopes (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        user_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid,
        environment_id uuid,
        access_mode varchar(16) NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_user_project_scopes" PRIMARY KEY (id),
        CONSTRAINT ck_scope_environment_requires_project CHECK (environment_id IS NULL OR project_id IS NOT NULL),
        CONSTRAINT "FK_user_project_scopes_environments_environment_id_project_id" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_user_project_scopes_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_user_project_scopes_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_user_project_scopes_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE api_versions (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        api_id uuid NOT NULL,
        version varchar(32) NOT NULL,
        status varchar(24) NOT NULL,
        openapi_document jsonb,
        schema_hash varchar(128),
        change_type varchar(24) NOT NULL,
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        openapi_source text,
        source_format varchar(16),
        sealed_at timestamptz,
        CONSTRAINT "PK_api_versions" PRIMARY KEY (id),
        CONSTRAINT "FK_api_versions_apis_api_id" FOREIGN KEY (api_id) REFERENCES apis (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_api_versions_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE application_api_permissions (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        application_id uuid NOT NULL,
        api_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        valid_from timestamptz NOT NULL,
        expires_at timestamptz,
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_application_api_permissions" PRIMARY KEY (id),
        CONSTRAINT ck_grant_window CHECK (expires_at IS NULL OR expires_at > valid_from),
        CONSTRAINT "FK_application_api_permissions_apis_api_id" FOREIGN KEY (api_id) REFERENCES apis (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_application_api_permissions_applications_application_id" FOREIGN KEY (application_id) REFERENCES applications (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_application_api_permissions_environments_environment_id" FOREIGN KEY (environment_id) REFERENCES environments (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_application_api_permissions_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE gateway_config_snapshots (
        config_version_id uuid NOT NULL,
        payload jsonb NOT NULL,
        size_bytes bigint NOT NULL,
        created_at timestamptz NOT NULL,
        payload_bytes bytea NOT NULL,
        CONSTRAINT "PK_gateway_config_snapshots" PRIMARY KEY (config_version_id),
        CONSTRAINT "FK_gateway_config_snapshots_gateway_config_versions_config_ver~" FOREIGN KEY (config_version_id) REFERENCES gateway_config_versions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE gateway_node_events (
        id bigint GENERATED BY DEFAULT AS IDENTITY,
        gateway_node_id uuid NOT NULL,
        event_type varchar(64) NOT NULL,
        message text NOT NULL,
        detail jsonb NOT NULL,
        created_at timestamptz NOT NULL,
        CONSTRAINT "PK_gateway_node_events" PRIMARY KEY (id),
        CONSTRAINT "FK_gateway_node_events_gateway_nodes_gateway_node_id" FOREIGN KEY (gateway_node_id) REFERENCES gateway_nodes (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE approval_tasks (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        flow_id uuid NOT NULL,
        release_id uuid NOT NULL,
        step_order int NOT NULL,
        assignee_user_id uuid,
        status varchar(24) NOT NULL,
        comment text,
        acted_at timestamptz,
        CONSTRAINT "PK_approval_tasks" PRIMARY KEY (id),
        CONSTRAINT "FK_approval_tasks_approval_flows_flow_id" FOREIGN KEY (flow_id) REFERENCES approval_flows (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_approval_tasks_release_records_release_id" FOREIGN KEY (release_id) REFERENCES release_records (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_approval_tasks_users_assignee_user_id" FOREIGN KEY (assignee_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE outbox_messages (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        environment_id uuid NOT NULL,
        release_id uuid NOT NULL,
        deployment_sequence bigint NOT NULL DEFAULT 0,
        event_type varchar(128) NOT NULL,
        payload jsonb NOT NULL,
        created_at timestamptz NOT NULL,
        processed_at timestamptz,
        lease_until timestamptz,
        attempts int NOT NULL DEFAULT 0,
        CONSTRAINT "PK_outbox_messages" PRIMARY KEY (id),
        CONSTRAINT "FK_outbox_messages_environments_environment_id" FOREIGN KEY (environment_id) REFERENCES environments (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_outbox_messages_release_records_release_id" FOREIGN KEY (release_id) REFERENCES release_records (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE release_items (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        release_id uuid NOT NULL,
        resource_type varchar(32) NOT NULL,
        resource_id uuid NOT NULL,
        change_type varchar(24) NOT NULL,
        before_json jsonb,
        after_json jsonb,
        CONSTRAINT "PK_release_items" PRIMARY KEY (id),
        CONSTRAINT "FK_release_items_release_records_release_id" FOREIGN KEY (release_id) REFERENCES release_records (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE release_targets (
        release_id uuid NOT NULL,
        node_id uuid NOT NULL,
        instance_id varchar(128) NOT NULL,
        created_at timestamptz NOT NULL,
        CONSTRAINT "PK_release_targets" PRIMARY KEY (release_id, node_id),
        CONSTRAINT "FK_release_targets_gateway_nodes_node_id" FOREIGN KEY (node_id) REFERENCES gateway_nodes (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_targets_release_records_release_id" FOREIGN KEY (release_id) REFERENCES release_records (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE upstream_destinations (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        cluster_id uuid NOT NULL,
        name varchar(128) NOT NULL,
        address varchar(1024) NOT NULL,
        weight int NOT NULL,
        enabled boolean NOT NULL DEFAULT TRUE,
        metadata jsonb,
        created_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_upstream_destinations" PRIMARY KEY (id),
        CONSTRAINT "FK_upstream_destinations_upstream_clusters_cluster_id" FOREIGN KEY (cluster_id) REFERENCES upstream_clusters (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE api_parameters (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        api_version_id uuid NOT NULL,
        location varchar(16) NOT NULL,
        name varchar(128) NOT NULL,
        data_type varchar(32) NOT NULL,
        required boolean NOT NULL,
        schema jsonb,
        description text,
        example_json jsonb,
        CONSTRAINT "PK_api_parameters" PRIMARY KEY (id),
        CONSTRAINT "FK_api_parameters_api_versions_api_version_id" FOREIGN KEY (api_version_id) REFERENCES api_versions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE api_routes (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        api_version_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        route_name varchar(128) NOT NULL,
        path varchar(512) NOT NULL,
        normalized_path varchar(512) NOT NULL,
        methods varchar[] NOT NULL,
        cluster_id uuid NOT NULL,
        priority int NOT NULL,
        enabled boolean NOT NULL DEFAULT TRUE,
        timeout_ms int NOT NULL,
        created_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_api_routes" PRIMARY KEY (id),
        CONSTRAINT "AK_api_routes_id_environment_id" UNIQUE (id, environment_id),
        CONSTRAINT ck_route_methods_timeout CHECK (cardinality(methods) > 0 AND timeout_ms > 0),
        CONSTRAINT "FK_api_routes_api_versions_api_version_id" FOREIGN KEY (api_version_id) REFERENCES api_versions (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_api_routes_environments_environment_id" FOREIGN KEY (environment_id) REFERENCES environments (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_api_routes_upstream_clusters_cluster_id_environment_id" FOREIGN KEY (cluster_id, environment_id) REFERENCES upstream_clusters (id, environment_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE api_schemas (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        api_version_id uuid NOT NULL,
        schema_type varchar(32) NOT NULL,
        name varchar(128) NOT NULL,
        status_code int,
        content_type varchar(128) NOT NULL,
        schema_json jsonb NOT NULL,
        schema_hash varchar(128),
        example_json jsonb,
        CONSTRAINT "PK_api_schemas" PRIMARY KEY (id),
        CONSTRAINT "FK_api_schemas_api_versions_api_version_id" FOREIGN KEY (api_version_id) REFERENCES api_versions (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE gateway_acks (
        release_id uuid NOT NULL,
        node_id uuid NOT NULL,
        instance_id varchar(128) NOT NULL,
        config_version bigint NOT NULL,
        deployment_sequence bigint NOT NULL DEFAULT 0,
        payload_hash varchar(128) NOT NULL,
        applied_at timestamptz NOT NULL,
        success boolean NOT NULL,
        error_code varchar(128),
        CONSTRAINT "PK_gateway_acks" PRIMARY KEY (release_id, node_id),
        CONSTRAINT "FK_gateway_acks_gateway_nodes_node_id" FOREIGN KEY (node_id) REFERENCES gateway_nodes (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_acks_release_records_release_id" FOREIGN KEY (release_id) REFERENCES release_records (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_acks_release_targets_release_id_node_id" FOREIGN KEY (release_id, node_id) REFERENCES release_targets (release_id, node_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE route_methods (
        route_id uuid NOT NULL,
        method varchar(16) NOT NULL,
        environment_id uuid NOT NULL,
        normalized_path varchar(512) NOT NULL,
        CONSTRAINT "PK_route_methods" PRIMARY KEY (route_id, method),
        CONSTRAINT "FK_route_methods_api_routes_route_id_environment_id" FOREIGN KEY (route_id, environment_id) REFERENCES api_routes (id, environment_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE TABLE route_policy_bindings (
        route_id uuid NOT NULL,
        policy_id uuid NOT NULL,
        priority int NOT NULL,
        CONSTRAINT "PK_route_policy_bindings" PRIMARY KEY (route_id, policy_id),
        CONSTRAINT "FK_route_policy_bindings_api_routes_route_id" FOREIGN KEY (route_id) REFERENCES api_routes (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_route_policy_bindings_policies_policy_id" FOREIGN KEY (policy_id) REFERENCES policies (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_api_groups_project_id" ON api_groups (project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_api_parameters_api_version_id" ON api_parameters (api_version_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_api_routes_api_version_id" ON api_routes (api_version_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_api_routes_cluster_id_environment_id" ON api_routes (cluster_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_api_routes_environment_id" ON api_routes (environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_api_schemas_api_version_id" ON api_schemas (api_version_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_api_versions_api_id_version" ON api_versions (api_id, version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_api_versions_created_by" ON api_versions (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_apis_group_id_project_id" ON apis (group_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_apis_organization_id" ON apis (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_apis_owner_user_id" ON apis (owner_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_apis_project_id_code" ON apis (project_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_apis_project_id_organization_id" ON apis (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_application_api_permissions_api_id" ON application_api_permissions (api_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_application_api_permissions_application_id_api_id_environme~" ON application_api_permissions (application_id, api_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_application_api_permissions_created_by" ON application_api_permissions (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_application_api_permissions_environment_id" ON application_api_permissions (environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_application_credentials_access_key" ON application_credentials (access_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_application_credentials_application_id" ON application_credentials (application_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_applications_organization_id_code" ON applications (organization_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_applications_project_id_organization_id" ON applications (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_approval_flows_organization_id" ON approval_flows (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_approval_steps_flow_id_step_order" ON approval_steps (flow_id, step_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_approval_tasks_assignee_user_id" ON approval_tasks (assignee_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_approval_tasks_flow_id" ON approval_tasks (flow_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_approval_tasks_release_id" ON approval_tasks (release_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_audit_logs_environment_id_created_at" ON audit_logs (environment_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_environments_project_id_code" ON environments (project_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_gateway_acks_node_id" ON gateway_acks (node_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_gateway_config_versions_created_by" ON gateway_config_versions (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_gateway_config_versions_environment_id_version_no" ON gateway_config_versions (environment_id, version_no);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_gateway_node_events_gateway_node_id" ON gateway_node_events (gateway_node_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_gateway_nodes_environment_id_enabled" ON gateway_nodes (environment_id, enabled);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_gateway_nodes_node_name" ON gateway_nodes (node_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_idempotency_records_actor_id_scope_key_operation_key" ON idempotency_records (actor_id, scope_key, operation, key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_organizations_code" ON organizations (code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_outbox_messages_environment_id" ON outbox_messages (environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_outbox_messages_processed_at_lease_until" ON outbox_messages (processed_at, lease_until);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_outbox_messages_release_id" ON outbox_messages (release_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_permissions_code" ON permissions (code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_policies_organization_id" ON policies (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_policies_project_id_organization_id" ON policies (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_projects_organization_id_code" ON projects (organization_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_projects_owner_user_id" ON projects (owner_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_release_items_release_id" ON release_items (release_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_release_records_approved_by" ON release_records (approved_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_release_records_environment_id_status" ON release_records (environment_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_release_records_release_no" ON release_records (release_no);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_release_records_requested_by" ON release_records (requested_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_release_records_rollback_of" ON release_records (rollback_of);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_release_targets_node_id" ON release_targets (node_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_role_permissions_permission_id" ON role_permissions (permission_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_roles_code" ON roles (code) WHERE organization_id IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_roles_organization_id_code" ON roles (organization_id, code) WHERE organization_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_route_methods_environment_id_method_normalized_path" ON route_methods (environment_id, method, normalized_path);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_route_methods_route_id_environment_id" ON route_methods (route_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_route_policy_bindings_policy_id" ON route_policy_bindings (policy_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_upstream_clusters_environment_id_name" ON upstream_clusters (environment_id, name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_upstream_clusters_environment_id_project_id" ON upstream_clusters (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_upstream_clusters_project_id" ON upstream_clusters (project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_upstream_destinations_cluster_id" ON upstream_destinations (cluster_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_user_project_scopes_environment_id_project_id" ON user_project_scopes (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_user_project_scopes_organization_id" ON user_project_scopes (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_user_project_scopes_project_id_organization_id" ON user_project_scopes (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_user_project_scopes_user_id" ON user_project_scopes (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE INDEX "IX_user_roles_role_id" ON user_roles (role_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_users_email" ON users (email);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    CREATE UNIQUE INDEX "IX_users_username" ON users (username);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003182012_InitialCore') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261003182012_InitialCore', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 01

-- WEBAPI_MIGRATION_START 02

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003201632_PublishCoordinatorIdentity') THEN
    ALTER TABLE release_records ADD publish_requested_by uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003201632_PublishCoordinatorIdentity') THEN
    ALTER TABLE release_records ADD publish_trace_id varchar(128);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003201632_PublishCoordinatorIdentity') THEN
    CREATE UNIQUE INDEX "IX_release_records_environment_id_deployment_sequence" ON release_records (environment_id, deployment_sequence) WHERE deployment_sequence > 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003201632_PublishCoordinatorIdentity') THEN
    CREATE INDEX "IX_release_records_publish_requested_by" ON release_records (publish_requested_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003201632_PublishCoordinatorIdentity') THEN
    ALTER TABLE release_records ADD CONSTRAINT "FK_release_records_users_publish_requested_by" FOREIGN KEY (publish_requested_by) REFERENCES users (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003201632_PublishCoordinatorIdentity') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261003201632_PublishCoordinatorIdentity', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 02

-- WEBAPI_MIGRATION_START 03

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003213457_RecoveryReference') THEN
    ALTER TABLE release_records ADD recovery_of uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003213457_RecoveryReference') THEN
    CREATE INDEX "IX_release_records_recovery_of" ON release_records (recovery_of);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003213457_RecoveryReference') THEN
    ALTER TABLE release_records ADD CONSTRAINT "FK_release_records_release_records_recovery_of" FOREIGN KEY (recovery_of) REFERENCES release_records (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261003213457_RecoveryReference') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261003213457_RecoveryReference', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 03

-- WEBAPI_MIGRATION_START 04

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE TABLE alert_rules (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid,
        environment_id uuid,
        name text NOT NULL,
        normalized_name text NOT NULL,
        metric text NOT NULL,
        expression text NOT NULL,
        severity text NOT NULL,
        enabled boolean NOT NULL,
        for_seconds integer NOT NULL,
        target_type text NOT NULL,
        target_id uuid,
        window_seconds integer NOT NULL,
        notification jsonb NOT NULL,
        revision bigint NOT NULL,
        logic_revision bigint NOT NULL,
        created_by uuid NOT NULL,
        updated_by uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_alert_rules" PRIMARY KEY (id),
        CONSTRAINT "AK_alert_rules_id_organization_id" UNIQUE (id, organization_id),
        CONSTRAINT ck_alert_rule_limits CHECK (for_seconds BETWEEN 0 AND 86400 AND window_seconds BETWEEN 60 AND 3600 AND revision > 0 AND logic_revision > 0 AND length(normalized_name) BETWEEN 1 AND 128),
        CONSTRAINT ck_alert_rule_metric CHECK (metric IN ('request_rps','error_5xx_ratio','latency_p95_ms','unhealthy_destinations')),
        CONSTRAINT ck_alert_rule_scope CHECK (environment_id IS NULL OR project_id IS NOT NULL),
        CONSTRAINT ck_alert_rule_severity CHECK (severity IN ('Info','Warning','Critical')),
        CONSTRAINT ck_alert_rule_target CHECK ((target_type = 'Environment' AND target_id IS NULL) OR (target_type = 'Api' AND target_id IS NOT NULL AND project_id IS NOT NULL) OR (target_type = 'Destination' AND target_id IS NOT NULL AND project_id IS NOT NULL AND environment_id IS NOT NULL)),
        CONSTRAINT "FK_alert_rules_environments_environment_id_project_id" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_rules_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_rules_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_rules_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_rules_users_updated_by" FOREIGN KEY (updated_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE TABLE alert_events (
        id uuid NOT NULL,
        rule_id uuid NOT NULL,
        rule_revision bigint NOT NULL,
        logic_revision bigint NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        resource_key text NOT NULL,
        resource_type text NOT NULL,
        resource_id uuid,
        occurrence_no bigint NOT NULL,
        status text NOT NULL,
        severity text NOT NULL,
        message text NOT NULL,
        rule_summary text NOT NULL,
        started_at timestamp with time zone NOT NULL,
        condition_started_at timestamp with time zone NOT NULL,
        resolved_at timestamp with time zone,
        acked_by uuid,
        acked_at timestamp with time zone,
        silenced_by uuid,
        silenced_until timestamp with time zone,
        silence_reason text,
        resolved_by uuid,
        resolve_reason text,
        last_observed_at timestamp with time zone,
        last_value double precision,
        last_condition boolean,
        evaluation_state text NOT NULL,
        revision bigint NOT NULL,
        CONSTRAINT "PK_alert_events" PRIMARY KEY (id),
        CONSTRAINT "AK_alert_events_id_rule_id_environment_id_resource_key" UNIQUE (id, rule_id, environment_id, resource_key),
        CONSTRAINT ck_alert_event_resource CHECK (length(resource_key) BETWEEN 1 AND 128 AND ((resource_type='Environment' AND resource_id IS NULL) OR (resource_type IN ('Api','Destination') AND resource_id IS NOT NULL))),
        CONSTRAINT ck_alert_event_revision CHECK (revision > 0 AND rule_revision > 0 AND logic_revision > 0 AND occurrence_no > 0),
        CONSTRAINT ck_alert_event_severity CHECK (severity IN ('Info','Warning','Critical')),
        CONSTRAINT ck_alert_event_status CHECK (status IN ('Open','Ack','Silenced','Resolved') AND evaluation_state IN ('Known','Unknown','ScopeInactive')),
        CONSTRAINT "FK_alert_events_alert_rules_rule_id_organization_id" FOREIGN KEY (rule_id, organization_id) REFERENCES alert_rules (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_events_environments_environment_id_project_id" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_events_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_events_users_acked_by" FOREIGN KEY (acked_by) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_events_users_resolved_by" FOREIGN KEY (resolved_by) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_events_users_silenced_by" FOREIGN KEY (silenced_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE TABLE alert_evaluation_states (
        id uuid NOT NULL,
        rule_id uuid NOT NULL,
        logic_revision bigint NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        resource_key text NOT NULL,
        resource_type text NOT NULL,
        resource_id uuid,
        phase text NOT NULL,
        pending_since timestamp with time zone,
        last_evaluated_slot timestamp with time zone,
        last_success_at timestamp with time zone,
        last_condition boolean,
        last_event_id uuid,
        next_occurrence_no bigint NOT NULL,
        evaluation_state text NOT NULL,
        suppressed_at timestamp with time zone,
        lease_owner text,
        lease_until timestamp with time zone,
        lease_token bigint NOT NULL,
        revision bigint NOT NULL,
        CONSTRAINT "PK_alert_evaluation_states" PRIMARY KEY (id),
        CONSTRAINT ck_alert_evaluation_phase CHECK (phase IN ('Inactive','Pending','Firing','SuppressedUntilRecovery') AND evaluation_state IN ('Known','Unknown','ScopeInactive')),
        CONSTRAINT ck_alert_evaluation_resource CHECK (length(resource_key) BETWEEN 1 AND 128 AND ((resource_type='Environment' AND resource_id IS NULL) OR (resource_type IN ('Api','Destination') AND resource_id IS NOT NULL))),
        CONSTRAINT ck_alert_evaluation_revision CHECK (logic_revision > 0 AND revision > 0 AND lease_token >= 0 AND next_occurrence_no > 0),
        CONSTRAINT "FK_alert_evaluation_states_alert_events_last_event_id_rule_id_~" FOREIGN KEY (last_event_id, rule_id, environment_id, resource_key) REFERENCES alert_events (id, rule_id, environment_id, resource_key) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_evaluation_states_alert_rules_rule_id_organization_id" FOREIGN KEY (rule_id, organization_id) REFERENCES alert_rules (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_evaluation_states_environments_environment_id_project~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_evaluation_states_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE TABLE alert_event_transitions (
        id uuid NOT NULL,
        event_id uuid NOT NULL,
        from_status text,
        to_status text NOT NULL,
        actor_id uuid,
        reason text NOT NULL,
        occurred_at timestamp with time zone NOT NULL,
        correlation_id text NOT NULL,
        CONSTRAINT "PK_alert_event_transitions" PRIMARY KEY (id),
        CONSTRAINT ck_alert_transition_status CHECK ((from_status IS NULL OR from_status IN ('Open','Ack','Silenced','Resolved')) AND to_status IN ('Open','Ack','Silenced','Resolved')),
        CONSTRAINT "FK_alert_event_transitions_alert_events_event_id" FOREIGN KEY (event_id) REFERENCES alert_events (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_alert_event_transitions_users_actor_id" FOREIGN KEY (actor_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_evaluation_states_environment_id_project_id" ON alert_evaluation_states (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_evaluation_states_last_event_id_rule_id_environment_i~" ON alert_evaluation_states (last_event_id, rule_id, environment_id, resource_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_evaluation_states_lease_until_last_evaluated_slot" ON alert_evaluation_states (lease_until, last_evaluated_slot);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_evaluation_states_project_id_organization_id" ON alert_evaluation_states (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE UNIQUE INDEX "IX_alert_evaluation_states_rule_id_logic_revision_environment_~" ON alert_evaluation_states (rule_id, logic_revision, environment_id, resource_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_evaluation_states_rule_id_organization_id" ON alert_evaluation_states (rule_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_event_transitions_actor_id" ON alert_event_transitions (actor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_event_transitions_event_id_occurred_at" ON alert_event_transitions (event_id, occurred_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_events_acked_by" ON alert_events (acked_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_events_environment_id_project_id" ON alert_events (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_events_environment_id_status_started_at" ON alert_events (environment_id, status, started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_events_project_id_organization_id" ON alert_events (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_events_resolved_by" ON alert_events (resolved_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE UNIQUE INDEX "IX_alert_events_rule_id_environment_id_resource_key" ON alert_events (rule_id, environment_id, resource_key) WHERE status <> 'Resolved';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE UNIQUE INDEX "IX_alert_events_rule_id_logic_revision_environment_id_resource~" ON alert_events (rule_id, logic_revision, environment_id, resource_key, occurrence_no);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_events_rule_id_organization_id" ON alert_events (rule_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_events_silenced_by" ON alert_events (silenced_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_rules_created_by" ON alert_rules (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_rules_environment_id_project_id" ON alert_rules (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE UNIQUE INDEX "IX_alert_rules_organization_id_project_id_environment_id_norma~" ON alert_rules (organization_id, project_id, environment_id, normalized_name) NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_rules_project_id_organization_id" ON alert_rules (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    CREATE INDEX "IX_alert_rules_updated_by" ON alert_rules (updated_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261004030000_ObservabilityAlerts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261004030000_ObservabilityAlerts', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 04

-- WEBAPI_MIGRATION_START 05

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005090000_SystemSettings') THEN
    CREATE TABLE system_settings (
        key varchar(128) NOT NULL,
        scope_type varchar(24) NOT NULL,
        scope_id uuid,
        value jsonb NOT NULL,
        updated_by uuid NOT NULL,
        updated_at timestamptz NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_system_settings" PRIMARY KEY (key),
        CONSTRAINT ck_system_setting_scope CHECK (scope_type = 'system' AND scope_id IS NULL),
        CONSTRAINT ck_system_setting_key CHECK (key IN ('system.security','system.release','system.gateway','system.audit','system.notification')),
        CONSTRAINT ck_system_setting_revision CHECK (revision > 0),
        CONSTRAINT "FK_system_settings_users_updated_by" FOREIGN KEY (updated_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005090000_SystemSettings') THEN
    CREATE INDEX "IX_system_settings_updated_by" ON system_settings (updated_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005090000_SystemSettings') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261005090000_SystemSettings', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 05

-- WEBAPI_MIGRATION_START 06

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE TABLE sso_providers (
        id uuid NOT NULL,
        organization_id uuid,
        name varchar(128) NOT NULL,
        provider_type varchar(24) NOT NULL DEFAULT 'oidc',
        issuer varchar(1024) NOT NULL,
        client_id varchar(256) NOT NULL,
        secret_ref varchar(512) NOT NULL,
        scopes jsonb NOT NULL,
        claim_mapping jsonb NOT NULL,
        enabled boolean NOT NULL DEFAULT FALSE,
        is_default boolean NOT NULL DEFAULT FALSE,
        revision bigint NOT NULL DEFAULT 1,
        auth_revision bigint NOT NULL DEFAULT 1,
        created_at timestamptz NOT NULL DEFAULT (now()),
        updated_at timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_sso_providers" PRIMARY KEY (id),
        CONSTRAINT ck_sso_provider_default CHECK (NOT is_default OR enabled),
        CONSTRAINT ck_sso_provider_json CHECK (jsonb_typeof(scopes) = 'array' AND jsonb_typeof(claim_mapping) = 'object'),
        CONSTRAINT ck_sso_provider_revision CHECK (revision > 0 AND auth_revision > 0),
        CONSTRAINT ck_sso_provider_type CHECK (provider_type = 'oidc'),
        CONSTRAINT "FK_sso_providers_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE TABLE sso_login_attempts (
        id uuid NOT NULL,
        provider_id uuid NOT NULL,
        provider_revision bigint NOT NULL,
        auth_revision bigint NOT NULL DEFAULT 1,
        return_path varchar(2048) NOT NULL,
        state varchar(24) NOT NULL,
        created_at timestamptz NOT NULL DEFAULT (now()),
        expires_at timestamptz NOT NULL,
        failure_code varchar(64),
        CONSTRAINT "PK_sso_login_attempts" PRIMARY KEY (id),
        CONSTRAINT ck_sso_attempt_expiry CHECK (expires_at > created_at),
        CONSTRAINT ck_sso_attempt_revision CHECK (provider_revision > 0 AND auth_revision > 0),
        CONSTRAINT ck_sso_attempt_state CHECK (state IN ('Pending','Processing','Succeeded','Failed')),
        CONSTRAINT "FK_sso_login_attempts_sso_providers_provider_id" FOREIGN KEY (provider_id) REFERENCES sso_providers (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE TABLE sso_provider_tests (
        id uuid NOT NULL,
        provider_id uuid NOT NULL,
        provider_revision bigint NOT NULL,
        tested_at timestamptz NOT NULL DEFAULT (now()),
        status varchar(24) NOT NULL,
        stages jsonb NOT NULL,
        CONSTRAINT "PK_sso_provider_tests" PRIMARY KEY (id),
        CONSTRAINT ck_sso_test_revision CHECK (provider_revision > 0),
        CONSTRAINT "FK_sso_provider_tests_sso_providers_provider_id" FOREIGN KEY (provider_id) REFERENCES sso_providers (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE TABLE user_external_identities (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        provider_id uuid NOT NULL,
        issuer varchar(1024) NOT NULL,
        subject varchar(255) NOT NULL,
        enabled boolean NOT NULL DEFAULT TRUE,
        revision bigint NOT NULL DEFAULT 1,
        created_at timestamptz NOT NULL DEFAULT (now()),
        updated_at timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_user_external_identities" PRIMARY KEY (id),
        CONSTRAINT ck_external_identity_revision CHECK (revision > 0),
        CONSTRAINT "FK_user_external_identities_sso_providers_provider_id" FOREIGN KEY (provider_id) REFERENCES sso_providers (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_user_external_identities_users_user_id" FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE INDEX "IX_sso_login_attempts_provider_id" ON sso_login_attempts (provider_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE INDEX "IX_sso_login_attempts_state_expires_at" ON sso_login_attempts (state, expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE INDEX "IX_sso_provider_tests_provider_id_tested_at" ON sso_provider_tests (provider_id, tested_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE UNIQUE INDEX ux_sso_default_organization ON sso_providers (organization_id) WHERE organization_id IS NOT NULL AND is_default AND enabled;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE UNIQUE INDEX ux_sso_default_platform ON sso_providers (is_default) WHERE organization_id IS NULL AND is_default AND enabled;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE UNIQUE INDEX "IX_user_external_identities_provider_id_issuer_subject" ON user_external_identities (provider_id, issuer, subject);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    CREATE UNIQUE INDEX "IX_user_external_identities_user_id" ON user_external_identities (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005120000_SsoOidc') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261005120000_SsoOidc', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 06

-- WEBAPI_MIGRATION_START 07

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005123210_SsoDisplayEmail') THEN
    DROP INDEX "IX_users_email";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005123210_SsoDisplayEmail') THEN
    CREATE UNIQUE INDEX "IX_users_email" ON users (email) WHERE auth_source = 'local' AND email IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005123210_SsoDisplayEmail') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261005123210_SsoDisplayEmail', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 07

-- WEBAPI_MIGRATION_START 08

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005152401_SsoSecretVersion') THEN
    ALTER TABLE sso_providers ADD protected_secret_fingerprint text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261005152401_SsoSecretVersion') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261005152401_SsoSecretVersion', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 08

-- WEBAPI_MIGRATION_START 09

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006090000_ApiVersionComparisons') THEN
    CREATE TABLE api_version_comparisons (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        api_id uuid NOT NULL,
        from_version_id uuid NOT NULL,
        to_version_id uuid NOT NULL,
        from_revision bigint NOT NULL,
        to_revision bigint NOT NULL,
        from_version character varying(32) NOT NULL,
        to_version character varying(32) NOT NULL,
        engine_version character varying(64) NOT NULL,
        input_fingerprint character varying(64) NOT NULL,
        report_hash character varying(64) NOT NULL,
        coverage character varying(16) NOT NULL,
        counts_json jsonb NOT NULL,
        input_bytes bytea NOT NULL,
        report_bytes bytea NOT NULL,
        created_by uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_api_version_comparisons" PRIMARY KEY (id),
        CONSTRAINT ck_api_version_comparisons_coverage CHECK (coverage IN ('Complete','Limited','Invalid')),
        CONSTRAINT ck_api_version_comparisons_hashes CHECK (input_fingerprint ~ '^[0-9a-f]{64}$' AND report_hash ~ '^[0-9a-f]{64}$'),
        CONSTRAINT ck_api_version_comparisons_versions CHECK (from_version_id <> to_version_id AND from_revision >= 1 AND to_revision >= 1)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006090000_ApiVersionComparisons') THEN
    CREATE TABLE api_version_risk_reviews (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        comparison_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        api_id uuid NOT NULL,
        input_fingerprint character varying(64) NOT NULL,
        report_hash character varying(64) NOT NULL,
        decision character varying(24) NOT NULL,
        comment character varying(2000),
        actor_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_api_version_risk_reviews" PRIMARY KEY (id),
        CONSTRAINT ck_api_version_risk_reviews_decision CHECK (decision IN ('Reviewed','AcceptedRisk') AND (decision <> 'AcceptedRisk' OR (comment IS NOT NULL AND length(btrim(comment)) BETWEEN 10 AND 2000))),
        CONSTRAINT ck_api_version_risk_reviews_hashes CHECK (input_fingerprint ~ '^[0-9a-f]{64}$' AND report_hash ~ '^[0-9a-f]{64}$'),
        CONSTRAINT "FK_api_version_risk_reviews_api_version_comparisons_comparison~" FOREIGN KEY (comparison_id) REFERENCES api_version_comparisons (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006090000_ApiVersionComparisons') THEN
    CREATE INDEX "IX_api_version_comparisons_api_id_created_at_id" ON api_version_comparisons (api_id, created_at, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006090000_ApiVersionComparisons') THEN
    CREATE INDEX "IX_api_version_risk_reviews_comparison_id_created_at_id" ON api_version_risk_reviews (comparison_id, created_at, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006090000_ApiVersionComparisons') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261006090000_ApiVersionComparisons', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 09

-- WEBAPI_MIGRATION_START 10

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE TABLE api_import_previews (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        cluster_id uuid NOT NULL,
        actor_id uuid NOT NULL,
        source_policy_revision bigint NOT NULL,
        bundle_json jsonb,
        preview_json jsonb,
        source_hash varchar(64) NOT NULL,
        bundle_hash varchar(64) NOT NULL,
        source_format varchar(16) NOT NULL,
        dialect varchar(16) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        status varchar(16) NOT NULL,
        import_id uuid,
        committed_targets_json jsonb,
        receipt_expires_at timestamp with time zone,
        revision bigint NOT NULL,
        CONSTRAINT "PK_api_import_previews" PRIMARY KEY (id),
        CONSTRAINT ck_import_preview_dialect CHECK (dialect IN ('Oas30','Oas31') AND source_format IN ('json','yaml')),
        CONSTRAINT ck_import_preview_expiry CHECK (expires_at > created_at),
        CONSTRAINT ck_import_preview_revision CHECK (revision >= 1 AND source_policy_revision >= 0),
        CONSTRAINT ck_import_preview_status CHECK (status IN ('Active','Committed','Revoked','Expired')),
        CONSTRAINT "FK_api_import_previews_environments_environment_id_project_id" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_api_import_previews_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_api_import_previews_upstream_clusters_cluster_id_environmen~" FOREIGN KEY (cluster_id, environment_id) REFERENCES upstream_clusters (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_api_import_previews_users_actor_id" FOREIGN KEY (actor_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE TABLE api_version_contract_sources (
        api_version_id uuid NOT NULL,
        bundle_json jsonb NOT NULL,
        bundle_hash varchar(64) NOT NULL,
        sources_json jsonb NOT NULL,
        dialect varchar(16) NOT NULL,
        source_policy_revision bigint NOT NULL,
        CONSTRAINT "PK_api_version_contract_sources" PRIMARY KEY (api_version_id),
        CONSTRAINT ck_contract_source_dialect CHECK (dialect IN ('Oas30','Oas31')),
        CONSTRAINT ck_contract_source_policy_revision CHECK (source_policy_revision >= 0),
        CONSTRAINT "FK_api_version_contract_sources_api_versions_api_version_id" FOREIGN KEY (api_version_id) REFERENCES api_versions (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE TABLE project_import_source_policies (
        project_id uuid NOT NULL,
        rules_json jsonb NOT NULL,
        revision bigint NOT NULL,
        updated_by uuid NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_project_import_source_policies" PRIMARY KEY (project_id),
        CONSTRAINT ck_import_policy_revision CHECK (revision >= 1),
        CONSTRAINT "FK_project_import_source_policies_projects_project_id" FOREIGN KEY (project_id) REFERENCES projects (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_project_import_source_policies_users_updated_by" FOREIGN KEY (updated_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE INDEX "IX_api_import_previews_actor_id" ON api_import_previews (actor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE INDEX "IX_api_import_previews_cluster_id_environment_id" ON api_import_previews (cluster_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE INDEX "IX_api_import_previews_environment_id_project_id" ON api_import_previews (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE INDEX "IX_api_import_previews_project_id_actor_id_status_expires_at" ON api_import_previews (project_id, actor_id, status, expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE INDEX "IX_api_import_previews_project_id_organization_id" ON api_import_previews (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE INDEX "IX_api_import_previews_receipt_expires_at" ON api_import_previews (receipt_expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE INDEX "IX_api_import_previews_status_expires_at" ON api_import_previews (status, expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    CREATE INDEX "IX_project_import_source_policies_updated_by" ON project_import_source_policies (updated_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261006165518_ContractManagement') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261006165518_ContractManagement', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 10

-- WEBAPI_MIGRATION_START 11

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008020000_ApprovalInboxIndexes') THEN
    CREATE INDEX "IX_release_records_environment_id_created_at_id" ON release_records (environment_id, created_at DESC, id DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008020000_ApprovalInboxIndexes') THEN
    CREATE INDEX "IX_approval_tasks_assignee_user_id_status_release_id" ON approval_tasks (assignee_user_id, status, release_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008020000_ApprovalInboxIndexes') THEN
    CREATE INDEX "IX_approval_tasks_release_id_status_step_order_id" ON approval_tasks (release_id, status, step_order, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008020000_ApprovalInboxIndexes') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261008020000_ApprovalInboxIndexes', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 11

-- WEBAPI_MIGRATION_START 12

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    DO $notification_upgrade$
    DECLARE legacy_rule_id uuid;
    BEGIN
      -- Only the controlled managed migrator may validate legacy data.
      -- Check again in this transaction before applying the notification step.
      IF EXISTS (SELECT 1 FROM public.system_settings s WHERE s.key='system.notification') THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: managed_notification_validation_required: system.notification';
      END IF;
      SELECT id INTO legacy_rule_id FROM public.alert_rules ORDER BY id LIMIT 1;
      IF legacy_rule_id IS NOT NULL THEN
        RAISE EXCEPTION 'governance_migration_preflight_failed: managed_notification_validation_required: rule %',legacy_rule_id;
      END IF;
    END $notification_upgrade$;
    UPDATE system_settings SET value=value || '{"smtpEnabled":false,"webhookEnabled":false,"smtpSecurity":"StartTlsRequired"}'::jsonb WHERE key='system.notification';
    UPDATE alert_rules SET notification=notification || '{"externalEnabled":false,"emailRecipients":[],"notifyRecovery":true,"retryPolicy":{"maxAttempts":5,"baseDelaySeconds":30,"maxDelaySeconds":900,"expiresAfterMinutes":1440}}'::jsonb;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    ALTER TABLE alert_events ADD frozen_notification jsonb NOT NULL DEFAULT '{"policy":{"inConsole":true,"requestedChannels":[],"externalEnabled":false,"emailRecipients":[],"notifyRecovery":true,"retryPolicy":{"maxAttempts":5,"baseDelaySeconds":30,"maxDelaySeconds":900,"expiresAfterMinutes":1440}},"emailProfileId":null,"webhookProfileId":null}';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    ALTER TABLE alert_events ADD CONSTRAINT "AK_alert_events_id_organization_id_project_id_environment_id" UNIQUE (id, organization_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    ALTER TABLE alert_event_transitions ADD CONSTRAINT "AK_alert_event_transitions_id_event_id" UNIQUE (id, event_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE TABLE notification_channel_profiles (
        id uuid NOT NULL,
        channel text NOT NULL,
        configuration_hash text NOT NULL,
        private_configuration jsonb NOT NULL,
        protected_secret_fingerprint text NOT NULL,
        created_by uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        settings_revision bigint NOT NULL,
        CONSTRAINT "PK_notification_channel_profiles" PRIMARY KEY (id),
        CONSTRAINT "AK_notification_channel_profiles_id_channel" UNIQUE (id, channel),
        CONSTRAINT ck_notification_profile_channel CHECK (channel IN ('Email','Webhook')),
        CONSTRAINT ck_notification_profile_config CHECK (configuration_hash ~ '^[a-f0-9]{64}$' AND jsonb_typeof(private_configuration)='object' AND octet_length(private_configuration::text) BETWEEN 2 AND 16384 AND length(protected_secret_fingerprint) BETWEEN 1 AND 4096 AND settings_revision > 0),
        CONSTRAINT "FK_notification_channel_profiles_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE TABLE notification_channel_states (
        channel text NOT NULL,
        profile_id uuid,
        enabled boolean NOT NULL,
        revision bigint NOT NULL,
        CONSTRAINT "PK_notification_channel_states" PRIMARY KEY (channel),
        CONSTRAINT ck_notification_channel_state CHECK (channel IN ('Email','Webhook') AND revision > 0 AND (NOT enabled OR profile_id IS NOT NULL)),
        CONSTRAINT "FK_notification_channel_states_notification_channel_profiles_p~" FOREIGN KEY (profile_id, channel) REFERENCES notification_channel_profiles (id, channel) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE TABLE notification_deliveries (
        id uuid NOT NULL,
        kind text NOT NULL,
        event_id uuid,
        transition_id uuid,
        triggered_delivery_id uuid,
        organization_id uuid,
        project_id uuid,
        environment_id uuid,
        created_by uuid,
        channel text NOT NULL,
        target text NOT NULL,
        target_hash text NOT NULL,
        profile_id uuid,
        payload bytea NOT NULL,
        max_attempts integer NOT NULL,
        base_delay_seconds integer NOT NULL,
        max_delay_seconds integer NOT NULL,
        expires_after_minutes integer NOT NULL,
        status text NOT NULL,
        reason text,
        attempt_count integer NOT NULL,
        next_attempt_at timestamp with time zone,
        lease_owner text,
        lease_token bigint NOT NULL,
        lease_until timestamp with time zone,
        revision bigint NOT NULL,
        created_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        completed_at timestamp with time zone,
        CONSTRAINT "PK_notification_deliveries" PRIMARY KEY (id),
        CONSTRAINT "AK_notification_deliveries_id_channel_target_hash" UNIQUE (id, channel, target_hash),
        CONSTRAINT ck_notification_delivery_budget CHECK (max_attempts BETWEEN 1 AND 5 AND base_delay_seconds BETWEEN 1 AND 300 AND max_delay_seconds BETWEEN base_delay_seconds AND 3600 AND expires_after_minutes BETWEEN 5 AND 1440 AND attempt_count BETWEEN 0 AND max_attempts AND expires_at > created_at AND expires_at <= created_at + make_interval(mins => expires_after_minutes) AND revision > 0 AND lease_token >= 0 AND (kind <> 'Test' OR (max_attempts=1 AND expires_after_minutes=5))),
        CONSTRAINT ck_notification_delivery_lease CHECK ((status='Sending' AND lease_owner IS NOT NULL AND length(lease_owner) BETWEEN 1 AND 128 AND lease_until IS NOT NULL AND lease_token > 0 AND attempt_count > 0) OR (status<>'Sending' AND lease_owner IS NULL AND lease_until IS NULL)),
        CONSTRAINT ck_notification_delivery_scope CHECK ((kind='Alert' AND event_id IS NOT NULL AND transition_id IS NOT NULL AND organization_id IS NOT NULL AND project_id IS NOT NULL AND environment_id IS NOT NULL) OR (kind='Test' AND event_id IS NULL AND transition_id IS NULL AND triggered_delivery_id IS NULL AND organization_id IS NULL AND project_id IS NULL AND environment_id IS NULL AND created_by IS NOT NULL)),
        CONSTRAINT ck_notification_delivery_status CHECK (status IN ('Queued','Sending','RetryScheduled','Paused','Accepted','Failed','Suppressed','Expired') AND (profile_id IS NOT NULL OR (status IN ('Suppressed','Expired') AND reason IS NOT NULL)) AND (reason IS NULL OR reason ~ '^[A-Za-z][A-Za-z0-9]{0,63}$')),
        CONSTRAINT ck_notification_delivery_target CHECK (channel IN ('Email','Webhook') AND length(target) BETWEEN 1 AND 2048 AND (channel <> 'Email' OR length(target) <= 254) AND target_hash ~ '^[a-f0-9]{64}$' AND octet_length(payload) BETWEEN 1 AND 16384),
        CONSTRAINT "FK_notification_deliveries_alert_event_transitions_transition_~" FOREIGN KEY (transition_id, event_id) REFERENCES alert_event_transitions (id, event_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_notification_deliveries_alert_events_event_id_organization_~" FOREIGN KEY (event_id, organization_id, project_id, environment_id) REFERENCES alert_events (id, organization_id, project_id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_notification_deliveries_notification_channel_profiles_profi~" FOREIGN KEY (profile_id, channel) REFERENCES notification_channel_profiles (id, channel) ON DELETE RESTRICT,
        CONSTRAINT "FK_notification_deliveries_notification_deliveries_triggered_d~" FOREIGN KEY (triggered_delivery_id, channel, target_hash) REFERENCES notification_deliveries (id, channel, target_hash) ON DELETE RESTRICT,
        CONSTRAINT "FK_notification_deliveries_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE TABLE notification_delivery_attempts (
        id uuid NOT NULL,
        delivery_id uuid NOT NULL,
        attempt_no integer NOT NULL,
        lease_token bigint NOT NULL,
        started_at timestamp with time zone NOT NULL,
        completed_at timestamp with time zone,
        outcome text,
        code text,
        protocol_status integer,
        CONSTRAINT "PK_notification_delivery_attempts" PRIMARY KEY (id),
        CONSTRAINT ck_notification_attempt_budget CHECK (attempt_no BETWEEN 1 AND 5 AND lease_token > 0 AND (completed_at IS NULL OR completed_at >= started_at)),
        CONSTRAINT ck_notification_attempt_outcome CHECK ((completed_at IS NULL AND outcome IS NULL AND code IS NULL AND protocol_status IS NULL) OR (completed_at IS NOT NULL AND outcome IS NOT NULL AND outcome IN ('Accepted','TransientFailure','PermanentFailure','OutcomeUnknown') AND code IS NOT NULL AND code ~ '^[A-Za-z][A-Za-z0-9]{0,63}$' AND (protocol_status IS NULL OR protocol_status BETWEEN 100 AND 599))),
        CONSTRAINT "FK_notification_delivery_attempts_notification_deliveries_deli~" FOREIGN KEY (delivery_id) REFERENCES notification_deliveries (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    ALTER TABLE alert_events ADD CONSTRAINT ck_alert_event_notification CHECK (jsonb_typeof(frozen_notification)='object' AND octet_length(frozen_notification::text) BETWEEN 2 AND 16384);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_channel_profiles_channel_created_at" ON notification_channel_profiles (channel, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_channel_profiles_created_by" ON notification_channel_profiles (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_channel_states_profile_id_channel" ON notification_channel_states (profile_id, channel);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_deliveries_created_by" ON notification_deliveries (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_deliveries_event_id_created_at_id" ON notification_deliveries (event_id, created_at, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_deliveries_event_id_organization_id_project_id~" ON notification_deliveries (event_id, organization_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE UNIQUE INDEX "IX_notification_deliveries_event_id_transition_id_channel_targ~" ON notification_deliveries (event_id, transition_id, channel, target_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_deliveries_kind_created_by_channel_created_at" ON notification_deliveries (kind, created_by, channel, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_deliveries_lease_until" ON notification_deliveries (lease_until) WHERE status = 'Sending';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_deliveries_profile_id_channel" ON notification_deliveries (profile_id, channel);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_deliveries_status_next_attempt_at_created_at" ON notification_deliveries (status, next_attempt_at, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_deliveries_transition_id_event_id" ON notification_deliveries (transition_id, event_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE INDEX "IX_notification_deliveries_triggered_delivery_id_channel_targe~" ON notification_deliveries (triggered_delivery_id, channel, target_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE UNIQUE INDEX "IX_notification_delivery_attempts_delivery_id_attempt_no" ON notification_delivery_attempts (delivery_id, attempt_no);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    CREATE UNIQUE INDEX "IX_notification_delivery_attempts_delivery_id_lease_token" ON notification_delivery_attempts (delivery_id, lease_token);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    INSERT INTO notification_channel_states(channel,enabled,revision) VALUES('Email',false,1),('Webhook',false,1);
    CREATE FUNCTION forbid_notification_profile_mutation() RETURNS trigger LANGUAGE plpgsql AS $immutable$
    BEGIN RAISE EXCEPTION 'Notification profiles are immutable' USING ERRCODE='23514'; END $immutable$;
    CREATE TRIGGER immutable_notification_profile BEFORE UPDATE OR DELETE ON notification_channel_profiles FOR EACH ROW EXECUTE FUNCTION forbid_notification_profile_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261008030000_ExternalNotifications') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261008030000_ExternalNotifications', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 12

-- WEBAPI_MIGRATION_START 13

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009010000_EnvironmentAccessAddresses') THEN
    ALTER TABLE environments ADD access_address_revision bigint NOT NULL DEFAULT 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009010000_EnvironmentAccessAddresses') THEN
    ALTER TABLE environments ADD base_path varchar(512) NOT NULL DEFAULT '/';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009010000_EnvironmentAccessAddresses') THEN
    ALTER TABLE environments ADD gateway_internal_url varchar(2048);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009010000_EnvironmentAccessAddresses') THEN
    ALTER TABLE environments ADD gateway_public_url varchar(2048);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009010000_EnvironmentAccessAddresses') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261009010000_EnvironmentAccessAddresses', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 13

-- WEBAPI_MIGRATION_START 14

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009011000_ReleaseAccessContexts') THEN
    ALTER TABLE release_records ADD CONSTRAINT "AK_release_records_id_environment_id" UNIQUE (id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009011000_ReleaseAccessContexts') THEN
    CREATE TABLE release_access_contexts (
        release_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        access_address_revision bigint NOT NULL,
        public_origin varchar(2048),
        internal_origin varchar(2048),
        base_path varchar(512) NOT NULL,
        captured_at timestamptz NOT NULL,
        CONSTRAINT "PK_release_access_contexts" PRIMARY KEY (release_id),
        CONSTRAINT "FK_release_access_contexts_release_records_release_id_environm~" FOREIGN KEY (release_id, environment_id) REFERENCES release_records (id, environment_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009011000_ReleaseAccessContexts') THEN
    CREATE INDEX "IX_release_access_contexts_release_id_environment_id" ON release_access_contexts (release_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009011000_ReleaseAccessContexts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261009011000_ReleaseAccessContexts', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 14

-- WEBAPI_MIGRATION_START 15

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    ALTER TABLE release_records ADD artifact_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    ALTER TABLE release_records ADD promotion_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    ALTER TABLE release_records ADD source_release_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    ALTER TABLE applications ADD CONSTRAINT "AK_applications_id_organization_id" UNIQUE (id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE TABLE project_delivery_policies (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        source_environment_id uuid NOT NULL,
        target_environment_id uuid NOT NULL,
        mode varchar(32) NOT NULL DEFAULT 'Legacy',
        required_test_types text[] NOT NULL DEFAULT (ARRAY['InterfaceFunction','Integration','ContractCompatibility']::text[]),
        verification_validity_minutes integer NOT NULL DEFAULT 1440,
        revision bigint NOT NULL DEFAULT 1,
        updated_by uuid NOT NULL,
        updated_at timestamptz NOT NULL,
        CONSTRAINT "PK_project_delivery_policies" PRIMARY KEY (id),
        CONSTRAINT ck_delivery_policy CHECK (mode IN ('Legacy','PromotionRequired') AND source_environment_id <> target_environment_id AND verification_validity_minutes BETWEEN 1 AND 10080 AND revision >= 1 AND cardinality(required_test_types) BETWEEN 1 AND 3 AND required_test_types <@ ARRAY['InterfaceFunction','Integration','ContractCompatibility']::text[]),
        CONSTRAINT "FK_project_delivery_policies_environments_source_environment_i~" FOREIGN KEY (source_environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_project_delivery_policies_environments_target_environment_i~" FOREIGN KEY (target_environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_project_delivery_policies_projects_project_id_organization_~" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_project_delivery_policies_users_updated_by" FOREIGN KEY (updated_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE TABLE release_artifacts (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        source_environment_id uuid NOT NULL,
        source_release_id uuid NOT NULL,
        canonical_content jsonb NOT NULL DEFAULT '{}',
        artifact_hash varchar(64) NOT NULL DEFAULT '',
        source_snapshot_hash varchar(64) NOT NULL DEFAULT '',
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL,
        CONSTRAINT "PK_release_artifacts" PRIMARY KEY (id),
        CONSTRAINT "AK_release_artifacts_id_project_id" UNIQUE (id, project_id),
        CONSTRAINT "AK_release_artifacts_id_project_id_source_environment_id" UNIQUE (id, project_id, source_environment_id),
        CONSTRAINT "AK_release_artifacts_id_project_id_source_environment_id_sourc~" UNIQUE (id, project_id, source_environment_id, source_release_id),
        CONSTRAINT "FK_release_artifacts_environments_source_environment_id_projec~" FOREIGN KEY (source_environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_artifacts_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_artifacts_release_records_source_release_id_source_~" FOREIGN KEY (source_release_id, source_environment_id) REFERENCES release_records (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_artifacts_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE TABLE release_test_acceptances (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        artifact_id uuid NOT NULL,
        source_environment_id uuid NOT NULL,
        verification_ids uuid[] NOT NULL DEFAULT (ARRAY[]::uuid[]),
        evidence_hash varchar(64) NOT NULL DEFAULT '',
        policy_revision bigint NOT NULL,
        status varchar(32) NOT NULL DEFAULT 'Requested',
        revision bigint NOT NULL DEFAULT 1,
        requested_by uuid NOT NULL,
        acted_by uuid,
        comment text NOT NULL DEFAULT '',
        created_at timestamptz NOT NULL,
        acted_at timestamptz,
        CONSTRAINT "PK_release_test_acceptances" PRIMARY KEY (id),
        CONSTRAINT "AK_release_test_acceptances_id_project_id_source_environment_id" UNIQUE (id, project_id, source_environment_id),
        CONSTRAINT ck_test_acceptance CHECK (status IN ('Requested','Accepted','Rejected','Revoked') AND revision >= 1 AND (acted_by IS NULL OR acted_by <> requested_by)),
        CONSTRAINT "FK_release_test_acceptances_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_test_acceptances_release_artifacts_artifact_id_proj~" FOREIGN KEY (artifact_id, project_id, source_environment_id) REFERENCES release_artifacts (id, project_id, source_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_test_acceptances_users_acted_by" FOREIGN KEY (acted_by) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_test_acceptances_users_requested_by" FOREIGN KEY (requested_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE TABLE release_promotions (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        artifact_id uuid NOT NULL,
        source_environment_id uuid NOT NULL,
        target_environment_id uuid NOT NULL,
        source_release_id uuid NOT NULL,
        target_release_id uuid,
        acceptance_id uuid,
        status varchar(32) NOT NULL DEFAULT 'Draft',
        baseline_config_version bigint NOT NULL DEFAULT 0,
        mapping_revision bigint NOT NULL DEFAULT 0,
        candidate_hash varchar(64),
        resource_revisions_json jsonb NOT NULL DEFAULT '[]',
        frozen_policy_json jsonb NOT NULL DEFAULT '{}',
        precheck_json jsonb,
        target_access_address_revision bigint NOT NULL DEFAULT 0,
        verification_context_json jsonb,
        revision bigint NOT NULL DEFAULT 1,
        requested_by uuid NOT NULL,
        created_at timestamptz NOT NULL,
        completed_at timestamptz,
        CONSTRAINT "PK_release_promotions" PRIMARY KEY (id),
        CONSTRAINT "AK_release_promotions_id_artifact_id_source_release_id_target_~" UNIQUE (id, artifact_id, source_release_id, target_environment_id),
        CONSTRAINT "AK_release_promotions_id_project_id_target_environment_id" UNIQUE (id, project_id, target_environment_id),
        CONSTRAINT ck_promotion_state CHECK (source_environment_id <> target_environment_id AND revision >= 1 AND mapping_revision >= 0 AND status IN ('Draft','WaitingApproval','Ready','Deploying','Verifying','Completed','Rejected','Cancelled','Invalidated','DeploymentFailed','VerificationFailed','RolledBack')),
        CONSTRAINT "FK_release_promotions_environments_target_environment_id_proje~" FOREIGN KEY (target_environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotions_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotions_release_artifacts_artifact_id_project_id~" FOREIGN KEY (artifact_id, project_id, source_environment_id, source_release_id) REFERENCES release_artifacts (id, project_id, source_environment_id, source_release_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotions_release_records_target_release_id_target~" FOREIGN KEY (target_release_id, target_environment_id) REFERENCES release_records (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotions_release_test_acceptances_acceptance_id_p~" FOREIGN KEY (acceptance_id, project_id, source_environment_id) REFERENCES release_test_acceptances (id, project_id, source_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotions_users_requested_by" FOREIGN KEY (requested_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE TABLE release_promotion_mappings (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        promotion_id uuid NOT NULL,
        target_environment_id uuid NOT NULL,
        resource_key varchar(512) NOT NULL DEFAULT '',
        kind varchar(32) NOT NULL DEFAULT '',
        target_route_id uuid,
        cluster_id uuid,
        application_id uuid,
        parameters_json jsonb NOT NULL DEFAULT '{}',
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_release_promotion_mappings" PRIMARY KEY (id),
        CONSTRAINT "FK_release_promotion_mappings_api_routes_target_route_id_targe~" FOREIGN KEY (target_route_id, target_environment_id) REFERENCES api_routes (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotion_mappings_applications_application_id_orga~" FOREIGN KEY (application_id, organization_id) REFERENCES applications (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotion_mappings_projects_project_id_organization~" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotion_mappings_release_promotions_promotion_id_~" FOREIGN KEY (promotion_id, project_id, target_environment_id) REFERENCES release_promotions (id, project_id, target_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotion_mappings_upstream_clusters_cluster_id_tar~" FOREIGN KEY (cluster_id, target_environment_id) REFERENCES upstream_clusters (id, environment_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE TABLE verification_reports (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        artifact_id uuid,
        promotion_id uuid,
        storage_key varchar(128) NOT NULL DEFAULT '',
        content_type varchar(64) NOT NULL DEFAULT '',
        size_bytes bigint NOT NULL,
        sha256 varchar(64) NOT NULL DEFAULT '',
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL,
        CONSTRAINT "PK_verification_reports" PRIMARY KEY (id),
        CONSTRAINT "AK_verification_reports_id_project_id_environment_id" UNIQUE (id, project_id, environment_id),
        CONSTRAINT ck_report CHECK (size_bytes BETWEEN 1 AND 10485760 AND content_type IN ('application/pdf','text/plain') AND (artifact_id IS NOT NULL)::int + (promotion_id IS NOT NULL)::int = 1),
        CONSTRAINT "FK_verification_reports_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_verification_reports_release_artifacts_artifact_id_project_~" FOREIGN KEY (artifact_id, project_id, environment_id) REFERENCES release_artifacts (id, project_id, source_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_verification_reports_release_promotions_promotion_id_projec~" FOREIGN KEY (promotion_id, project_id, environment_id) REFERENCES release_promotions (id, project_id, target_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_verification_reports_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE TABLE release_verifications (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        artifact_id uuid NOT NULL,
        promotion_id uuid,
        release_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        config_version bigint NOT NULL,
        deployment_sequence bigint NOT NULL,
        snapshot_hash varchar(64) NOT NULL DEFAULT '',
        access_address_revision bigint NOT NULL,
        access_context_json jsonb NOT NULL DEFAULT '{}',
        policy_revision bigint NOT NULL,
        phase varchar(32) NOT NULL DEFAULT 'SourceTest',
        type varchar(32) NOT NULL DEFAULT '',
        result varchar(16) NOT NULL DEFAULT '',
        is_manual boolean NOT NULL DEFAULT TRUE,
        report_id uuid,
        report_hash varchar(64),
        comment text NOT NULL DEFAULT '',
        started_at timestamptz NOT NULL,
        finished_at timestamptz NOT NULL,
        expires_at timestamptz NOT NULL,
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL,
        CONSTRAINT "PK_release_verifications" PRIMARY KEY (id),
        CONSTRAINT "AK_release_verifications_id_project_id_environment_id" UNIQUE (id, project_id, environment_id),
        CONSTRAINT ck_verification_fact CHECK (phase IN ('SourceTest','Production') AND result IN ('Passed','Failed') AND is_manual AND config_version > 0 AND deployment_sequence > 0 AND access_address_revision >= 1 AND started_at <= finished_at AND finished_at <= created_at AND expires_at > finished_at AND ((phase='SourceTest' AND promotion_id IS NULL AND type IN ('InterfaceFunction','Integration','ContractCompatibility')) OR (phase='Production' AND promotion_id IS NOT NULL AND type IN ('EntryConnectivity','AuthenticationAuthorization','CriticalBusinessCall')))),
        CONSTRAINT "FK_release_verifications_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_verifications_release_artifacts_artifact_id_project~" FOREIGN KEY (artifact_id, project_id) REFERENCES release_artifacts (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_verifications_release_promotions_promotion_id_proje~" FOREIGN KEY (promotion_id, project_id, environment_id) REFERENCES release_promotions (id, project_id, target_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_verifications_release_records_release_id_environmen~" FOREIGN KEY (release_id, environment_id) REFERENCES release_records (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_verifications_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_verifications_verification_reports_report_id_projec~" FOREIGN KEY (report_id, project_id, environment_id) REFERENCES verification_reports (id, project_id, environment_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE TABLE release_promotion_events (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        promotion_id uuid,
        acceptance_id uuid,
        environment_id uuid NOT NULL,
        phase varchar(32) NOT NULL DEFAULT '',
        from_status varchar(32),
        to_status varchar(32) NOT NULL DEFAULT '',
        reason_code varchar(128) NOT NULL DEFAULT '',
        actor_id uuid,
        release_id uuid,
        verification_id uuid,
        occurred_at timestamptz NOT NULL,
        CONSTRAINT "PK_release_promotion_events" PRIMARY KEY (id),
        CONSTRAINT ck_delivery_event_owner CHECK ((promotion_id IS NOT NULL)::int + (acceptance_id IS NOT NULL)::int = 1),
        CONSTRAINT "FK_release_promotion_events_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotion_events_release_promotions_promotion_id_pr~" FOREIGN KEY (promotion_id, project_id, environment_id) REFERENCES release_promotions (id, project_id, target_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotion_events_release_records_release_id_environ~" FOREIGN KEY (release_id, environment_id) REFERENCES release_records (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotion_events_release_test_acceptances_acceptanc~" FOREIGN KEY (acceptance_id, project_id, environment_id) REFERENCES release_test_acceptances (id, project_id, source_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotion_events_release_verifications_verification~" FOREIGN KEY (verification_id, project_id, environment_id) REFERENCES release_verifications (id, project_id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_promotion_events_users_actor_id" FOREIGN KEY (actor_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_records_artifact_id" ON release_records (artifact_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE UNIQUE INDEX "IX_release_records_promotion_id" ON release_records (promotion_id) WHERE promotion_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_records_promotion_id_artifact_id_source_release_id_~" ON release_records (promotion_id, artifact_id, source_release_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_records_source_release_id" ON release_records (source_release_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    ALTER TABLE release_records ADD CONSTRAINT ck_formal_promotion_link CHECK (promotion_id IS NULL OR (artifact_id IS NOT NULL AND source_release_id IS NOT NULL AND recovery_of IS NULL AND rollback_of IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE UNIQUE INDEX "IX_project_delivery_policies_project_id" ON project_delivery_policies (project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_project_delivery_policies_project_id_organization_id" ON project_delivery_policies (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_project_delivery_policies_source_environment_id_project_id" ON project_delivery_policies (source_environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_project_delivery_policies_target_environment_id_project_id" ON project_delivery_policies (target_environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_project_delivery_policies_updated_by" ON project_delivery_policies (updated_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_artifacts_created_by" ON release_artifacts (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_artifacts_project_id_organization_id" ON release_artifacts (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_artifacts_source_environment_id_project_id" ON release_artifacts (source_environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE UNIQUE INDEX "IX_release_artifacts_source_release_id_artifact_hash" ON release_artifacts (source_release_id, artifact_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_artifacts_source_release_id_source_environment_id" ON release_artifacts (source_release_id, source_environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_events_acceptance_id_project_id_environme~" ON release_promotion_events (acceptance_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_events_actor_id" ON release_promotion_events (actor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_events_project_id_organization_id" ON release_promotion_events (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_events_promotion_id_project_id_environmen~" ON release_promotion_events (promotion_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_events_release_id_environment_id" ON release_promotion_events (release_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_events_verification_id_project_id_environ~" ON release_promotion_events (verification_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_mappings_application_id_organization_id" ON release_promotion_mappings (application_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_mappings_cluster_id_target_environment_id" ON release_promotion_mappings (cluster_id, target_environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_mappings_project_id_organization_id" ON release_promotion_mappings (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE UNIQUE INDEX "IX_release_promotion_mappings_promotion_id_kind_resource_key" ON release_promotion_mappings (promotion_id, kind, resource_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_mappings_promotion_id_project_id_target_e~" ON release_promotion_mappings (promotion_id, project_id, target_environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotion_mappings_target_route_id_target_environme~" ON release_promotion_mappings (target_route_id, target_environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotions_acceptance_id_project_id_source_environm~" ON release_promotions (acceptance_id, project_id, source_environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotions_artifact_id_project_id_source_environmen~" ON release_promotions (artifact_id, project_id, source_environment_id, source_release_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotions_project_id_created_at_id" ON release_promotions (project_id, created_at DESC, id DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotions_project_id_organization_id" ON release_promotions (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotions_requested_by" ON release_promotions (requested_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotions_target_environment_id_project_id" ON release_promotions (target_environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_promotions_target_release_id_target_environment_id" ON release_promotions (target_release_id, target_environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_test_acceptances_acted_by" ON release_test_acceptances (acted_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_test_acceptances_artifact_id_project_id_source_envi~" ON release_test_acceptances (artifact_id, project_id, source_environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_test_acceptances_project_id_organization_id" ON release_test_acceptances (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_test_acceptances_requested_by" ON release_test_acceptances (requested_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_verifications_artifact_id_phase_type_created_at" ON release_verifications (artifact_id, phase, type, created_at DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_verifications_artifact_id_project_id" ON release_verifications (artifact_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_verifications_created_by" ON release_verifications (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_verifications_project_id_organization_id" ON release_verifications (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_verifications_promotion_id_project_id_environment_id" ON release_verifications (promotion_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_verifications_release_id_environment_id" ON release_verifications (release_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_release_verifications_report_id_project_id_environment_id" ON release_verifications (report_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_verification_reports_artifact_id_project_id_environment_id" ON verification_reports (artifact_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_verification_reports_created_by" ON verification_reports (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_verification_reports_project_id_organization_id" ON verification_reports (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE INDEX "IX_verification_reports_promotion_id_project_id_environment_id" ON verification_reports (promotion_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    CREATE UNIQUE INDEX "IX_verification_reports_storage_key" ON verification_reports (storage_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    ALTER TABLE release_records ADD CONSTRAINT "FK_release_records_release_artifacts_artifact_id" FOREIGN KEY (artifact_id) REFERENCES release_artifacts (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    ALTER TABLE release_records ADD CONSTRAINT "FK_release_records_release_promotions_promotion_id_artifact_id~" FOREIGN KEY (promotion_id, artifact_id, source_release_id, environment_id) REFERENCES release_promotions (id, artifact_id, source_release_id, target_environment_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    ALTER TABLE release_records ADD CONSTRAINT "FK_release_records_release_records_source_release_id" FOREIGN KEY (source_release_id) REFERENCES release_records (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009020000_ReleaseDelivery') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261009020000_ReleaseDelivery', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 15

-- WEBAPI_MIGRATION_START 16

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE project_delivery_policies DROP CONSTRAINT ck_delivery_policy;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_verifications ADD pipeline_run_stage_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_verifications ADD profile_hash varchar(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_verifications ADD stage_attempt_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_test_acceptances ADD pipeline_run_stage_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_test_acceptances ADD profile_hash varchar(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_test_acceptances ADD stage_attempt_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_promotions ADD gate_origin varchar(32) NOT NULL DEFAULT 'ProjectConnection';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_promotions ADD pipeline_run_stage_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_promotions ADD stage_attempt_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE project_delivery_policies ADD active_pipeline_version_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE TABLE release_pipelines (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        name varchar(100) NOT NULL DEFAULT '',
        description text NOT NULL DEFAULT '',
        draft_json jsonb NOT NULL DEFAULT '{}',
        status varchar(32) NOT NULL DEFAULT 'Active',
        revision bigint NOT NULL DEFAULT 1,
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL DEFAULT (now()),
        updated_by uuid NOT NULL,
        updated_at timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_release_pipelines" PRIMARY KEY (id),
        CONSTRAINT "AK_release_pipelines_id_project_id" UNIQUE (id, project_id),
        CONSTRAINT ck_pipeline_draft CHECK (length(btrim(name)) BETWEEN 1 AND 100 AND revision>=1 AND status IN ('Active','Archived')),
        CONSTRAINT "FK_release_pipelines_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipelines_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipelines_users_updated_by" FOREIGN KEY (updated_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE TABLE release_pipeline_versions (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        pipeline_id uuid NOT NULL,
        project_id uuid NOT NULL,
        version_no integer NOT NULL,
        content_json jsonb NOT NULL DEFAULT '{}',
        definition_hash varchar(64) NOT NULL DEFAULT '',
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_release_pipeline_versions" PRIMARY KEY (id),
        CONSTRAINT "AK_release_pipeline_versions_id_project_id" UNIQUE (id, project_id),
        CONSTRAINT ck_pipeline_version CHECK (version_no>=1 AND length(definition_hash)=64),
        CONSTRAINT "FK_release_pipeline_versions_release_pipelines_pipeline_id_pro~" FOREIGN KEY (pipeline_id, project_id) REFERENCES release_pipelines (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_versions_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE TABLE release_pipeline_runs (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        pipeline_version_id uuid NOT NULL,
        definition_hash varchar(64) NOT NULL DEFAULT '',
        root_artifact_id uuid NOT NULL,
        root_artifact_hash varchar(64) NOT NULL DEFAULT '',
        source_environment_id uuid NOT NULL,
        source_release_id uuid NOT NULL,
        source_config_version bigint NOT NULL,
        source_deployment_sequence bigint NOT NULL,
        policy_revision bigint NOT NULL,
        status varchar(32) NOT NULL DEFAULT 'Active',
        current_stage_order integer NOT NULL DEFAULT 1,
        revision bigint NOT NULL DEFAULT 1,
        created_by uuid NOT NULL,
        created_at timestamptz NOT NULL DEFAULT (now()),
        completed_at timestamptz,
        CONSTRAINT "PK_release_pipeline_runs" PRIMARY KEY (id),
        CONSTRAINT "AK_release_pipeline_runs_id_project_id" UNIQUE (id, project_id),
        CONSTRAINT ck_pipeline_run CHECK (status IN ('Active','Paused','TimedOut','Invalidated','Cancelled','Completed') AND current_stage_order BETWEEN 1 AND 8 AND revision>=1 AND policy_revision>=1 AND source_config_version>0 AND source_deployment_sequence>0 AND length(definition_hash)=64 AND length(root_artifact_hash)=64),
        CONSTRAINT "FK_release_pipeline_runs_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_runs_release_artifacts_root_artifact_id_pr~" FOREIGN KEY (root_artifact_id, project_id, source_environment_id, source_release_id) REFERENCES release_artifacts (id, project_id, source_environment_id, source_release_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_runs_release_pipeline_versions_pipeline_ve~" FOREIGN KEY (pipeline_version_id, project_id) REFERENCES release_pipeline_versions (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_runs_users_created_by" FOREIGN KEY (created_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE TABLE release_pipeline_events (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        run_id uuid NOT NULL,
        project_id uuid NOT NULL,
        stage_id uuid,
        attempt_id uuid,
        from_status varchar(32) NOT NULL DEFAULT '',
        to_status varchar(32) NOT NULL DEFAULT '',
        reason_code varchar(128) NOT NULL DEFAULT '',
        actor_id uuid,
        related_id uuid,
        created_at timestamptz NOT NULL DEFAULT (now()),
        CONSTRAINT "PK_release_pipeline_events" PRIMARY KEY (id),
        CONSTRAINT ck_pipeline_event CHECK (attempt_id IS NULL OR stage_id IS NOT NULL),
        CONSTRAINT "FK_release_pipeline_events_release_pipeline_runs_run_id_projec~" FOREIGN KEY (run_id, project_id) REFERENCES release_pipeline_runs (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_events_users_actor_id" FOREIGN KEY (actor_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE TABLE release_pipeline_run_stages (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        run_id uuid NOT NULL,
        project_id uuid NOT NULL,
        stage_order integer NOT NULL,
        environment_id uuid NOT NULL,
        source_stage_id uuid,
        stage_artifact_id uuid,
        current_attempt_id uuid,
        profile_json jsonb NOT NULL DEFAULT '{}',
        profile_hash varchar(64) NOT NULL DEFAULT '',
        status varchar(32) NOT NULL DEFAULT 'Pending',
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_release_pipeline_run_stages" PRIMARY KEY (id),
        CONSTRAINT "AK_release_pipeline_run_stages_id_project_id_environment_id" UNIQUE (id, project_id, environment_id),
        CONSTRAINT "AK_release_pipeline_run_stages_id_run_id" UNIQUE (id, run_id),
        CONSTRAINT "AK_release_pipeline_run_stages_id_run_id_project_id_environmen~" UNIQUE (id, run_id, project_id, environment_id),
        CONSTRAINT ck_pipeline_stage CHECK (stage_order BETWEEN 1 AND 8 AND revision>=1 AND length(profile_hash)=64 AND (source_stage_id IS NULL OR source_stage_id<>id) AND status IN ('Pending','AwaitingEvidence','AwaitingAcceptance','AwaitingMapping','AwaitingPrecheck','AwaitingApproval','ReadyToDeploy','Deploying','AwaitingVerification','Passed','Rejected','DeploymentFailed','VerificationFailed','TimedOut','Invalidated','Cancelled')),
        CONSTRAINT "FK_release_pipeline_run_stages_environments_environment_id_pro~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_run_stages_release_artifacts_stage_artifac~" FOREIGN KEY (stage_artifact_id, project_id, environment_id) REFERENCES release_artifacts (id, project_id, source_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_run_stages_release_pipeline_run_stages_sou~" FOREIGN KEY (source_stage_id, run_id) REFERENCES release_pipeline_run_stages (id, run_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_run_stages_release_pipeline_runs_run_id_pr~" FOREIGN KEY (run_id, project_id) REFERENCES release_pipeline_runs (id, project_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE TABLE release_pipeline_stage_attempts (
        id uuid NOT NULL DEFAULT (gen_random_uuid()),
        run_stage_id uuid NOT NULL,
        run_id uuid NOT NULL,
        project_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        attempt_no integer NOT NULL,
        origin_attempt_id uuid,
        activated_at timestamptz NOT NULL,
        deadline_at timestamptz NOT NULL,
        status varchar(32) NOT NULL DEFAULT 'Active',
        context_json jsonb NOT NULL DEFAULT '{}',
        artifact_id uuid,
        acceptance_id uuid,
        promotion_id uuid,
        actual_release_id uuid,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_release_pipeline_stage_attempts" PRIMARY KEY (id),
        CONSTRAINT "AK_release_pipeline_stage_attempts_id_run_stage_id" UNIQUE (id, run_stage_id),
        CONSTRAINT ck_pipeline_attempt CHECK (attempt_no>=1 AND revision>=1 AND deadline_at>activated_at AND (origin_attempt_id IS NULL OR origin_attempt_id<>id) AND status IN ('Active','Passed','Rejected','DeploymentFailed','VerificationFailed','TimedOut','Invalidated','Cancelled')),
        CONSTRAINT "FK_release_pipeline_stage_attempts_release_artifacts_artifact_~" FOREIGN KEY (artifact_id, project_id, environment_id) REFERENCES release_artifacts (id, project_id, source_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_stage_attempts_release_pipeline_run_stages~" FOREIGN KEY (run_stage_id, run_id, project_id, environment_id) REFERENCES release_pipeline_run_stages (id, run_id, project_id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_stage_attempts_release_pipeline_stage_atte~" FOREIGN KEY (origin_attempt_id, run_stage_id) REFERENCES release_pipeline_stage_attempts (id, run_stage_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_stage_attempts_release_promotions_promotio~" FOREIGN KEY (promotion_id, project_id, environment_id) REFERENCES release_promotions (id, project_id, target_environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_stage_attempts_release_records_actual_rele~" FOREIGN KEY (actual_release_id, environment_id) REFERENCES release_records (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_release_pipeline_stage_attempts_release_test_acceptances_ac~" FOREIGN KEY (acceptance_id, project_id, environment_id) REFERENCES release_test_acceptances (id, project_id, source_environment_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_verifications_pipeline_run_stage_id_project_id_envi~" ON release_verifications (pipeline_run_stage_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_verifications_stage_attempt_id_pipeline_run_stage_id" ON release_verifications (stage_attempt_id, pipeline_run_stage_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_verifications ADD CONSTRAINT ck_release_verification_pipeline_context CHECK (((pipeline_run_stage_id IS NULL AND stage_attempt_id IS NULL AND profile_hash IS NULL) OR (pipeline_run_stage_id IS NOT NULL AND stage_attempt_id IS NOT NULL AND profile_hash IS NOT NULL AND length(profile_hash)=64)));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_test_acceptances_pipeline_run_stage_id_project_id_s~" ON release_test_acceptances (pipeline_run_stage_id, project_id, source_environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_test_acceptances_stage_attempt_id_pipeline_run_stag~" ON release_test_acceptances (stage_attempt_id, pipeline_run_stage_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_test_acceptances ADD CONSTRAINT ck_release_test_acceptance_pipeline_context CHECK (((pipeline_run_stage_id IS NULL AND stage_attempt_id IS NULL AND profile_hash IS NULL) OR (pipeline_run_stage_id IS NOT NULL AND stage_attempt_id IS NOT NULL AND profile_hash IS NOT NULL AND length(profile_hash)=64)));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_promotions_pipeline_run_stage_id_project_id_target_~" ON release_promotions (pipeline_run_stage_id, project_id, target_environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_promotions_stage_attempt_id_pipeline_run_stage_id" ON release_promotions (stage_attempt_id, pipeline_run_stage_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_promotions ADD CONSTRAINT ck_release_promotion_pipeline_context CHECK (((gate_origin='ProjectConnection' AND pipeline_run_stage_id IS NULL AND stage_attempt_id IS NULL) OR (gate_origin='PipelineRunStage' AND pipeline_run_stage_id IS NOT NULL AND stage_attempt_id IS NOT NULL)));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_project_delivery_policies_active_pipeline_version_id_projec~" ON project_delivery_policies (active_pipeline_version_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE project_delivery_policies ADD CONSTRAINT ck_delivery_policy CHECK (((mode IN ('Legacy','PromotionRequired') AND active_pipeline_version_id IS NULL) OR (mode='PipelineRequired' AND active_pipeline_version_id IS NOT NULL)) AND source_environment_id <> target_environment_id AND verification_validity_minutes BETWEEN 1 AND 10080 AND revision >= 1 AND cardinality(required_test_types) BETWEEN 1 AND 3 AND required_test_types <@ ARRAY['InterfaceFunction','Integration','ContractCompatibility']::text[]);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_events_actor_id" ON release_pipeline_events (actor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_events_attempt_id_stage_id" ON release_pipeline_events (attempt_id, stage_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_events_run_id_created_at_id" ON release_pipeline_events (run_id, created_at, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_events_run_id_project_id" ON release_pipeline_events (run_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_events_stage_id_run_id" ON release_pipeline_events (stage_id, run_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_run_stages_current_attempt_id_id" ON release_pipeline_run_stages (current_attempt_id, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_run_stages_environment_id_project_id" ON release_pipeline_run_stages (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE UNIQUE INDEX "IX_release_pipeline_run_stages_run_id_environment_id" ON release_pipeline_run_stages (run_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_run_stages_run_id_project_id" ON release_pipeline_run_stages (run_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE UNIQUE INDEX "IX_release_pipeline_run_stages_run_id_stage_order" ON release_pipeline_run_stages (run_id, stage_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_run_stages_source_stage_id_run_id" ON release_pipeline_run_stages (source_stage_id, run_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_run_stages_stage_artifact_id_project_id_en~" ON release_pipeline_run_stages (stage_artifact_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_runs_created_by" ON release_pipeline_runs (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_runs_pipeline_version_id_project_id" ON release_pipeline_runs (pipeline_version_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE UNIQUE INDEX "IX_release_pipeline_runs_project_id" ON release_pipeline_runs (project_id) WHERE status IN ('Active','Paused','TimedOut');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_runs_project_id_created_at_id" ON release_pipeline_runs (project_id, created_at DESC, id DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_runs_project_id_organization_id" ON release_pipeline_runs (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_runs_root_artifact_id_project_id_source_en~" ON release_pipeline_runs (root_artifact_id, project_id, source_environment_id, source_release_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_stage_attempts_acceptance_id_project_id_en~" ON release_pipeline_stage_attempts (acceptance_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_stage_attempts_actual_release_id_environme~" ON release_pipeline_stage_attempts (actual_release_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_stage_attempts_artifact_id_project_id_envi~" ON release_pipeline_stage_attempts (artifact_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_stage_attempts_origin_attempt_id_run_stage~" ON release_pipeline_stage_attempts (origin_attempt_id, run_stage_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE UNIQUE INDEX "IX_release_pipeline_stage_attempts_promotion_id" ON release_pipeline_stage_attempts (promotion_id) WHERE promotion_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_stage_attempts_promotion_id_project_id_env~" ON release_pipeline_stage_attempts (promotion_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE UNIQUE INDEX "IX_release_pipeline_stage_attempts_run_stage_id" ON release_pipeline_stage_attempts (run_stage_id) WHERE status='Active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE UNIQUE INDEX "IX_release_pipeline_stage_attempts_run_stage_id_attempt_no" ON release_pipeline_stage_attempts (run_stage_id, attempt_no);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_stage_attempts_run_stage_id_run_id_project~" ON release_pipeline_stage_attempts (run_stage_id, run_id, project_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_versions_created_by" ON release_pipeline_versions (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipeline_versions_pipeline_id_project_id" ON release_pipeline_versions (pipeline_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE UNIQUE INDEX "IX_release_pipeline_versions_pipeline_id_version_no" ON release_pipeline_versions (pipeline_id, version_no);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipelines_created_by" ON release_pipelines (created_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipelines_project_id_created_at_id" ON release_pipelines (project_id, created_at DESC, id DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipelines_project_id_organization_id" ON release_pipelines (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE INDEX "IX_release_pipelines_updated_by" ON release_pipelines (updated_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE project_delivery_policies ADD CONSTRAINT "FK_project_delivery_policies_release_pipeline_versions_active_~" FOREIGN KEY (active_pipeline_version_id, project_id) REFERENCES release_pipeline_versions (id, project_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_promotions ADD CONSTRAINT "FK_release_promotions_release_pipeline_run_stages_pipeline_run~" FOREIGN KEY (pipeline_run_stage_id, project_id, target_environment_id) REFERENCES release_pipeline_run_stages (id, project_id, environment_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_promotions ADD CONSTRAINT "FK_release_promotions_release_pipeline_stage_attempts_stage_at~" FOREIGN KEY (stage_attempt_id, pipeline_run_stage_id) REFERENCES release_pipeline_stage_attempts (id, run_stage_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_test_acceptances ADD CONSTRAINT "FK_release_test_acceptances_release_pipeline_run_stages_pipeli~" FOREIGN KEY (pipeline_run_stage_id, project_id, source_environment_id) REFERENCES release_pipeline_run_stages (id, project_id, environment_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_test_acceptances ADD CONSTRAINT "FK_release_test_acceptances_release_pipeline_stage_attempts_st~" FOREIGN KEY (stage_attempt_id, pipeline_run_stage_id) REFERENCES release_pipeline_stage_attempts (id, run_stage_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_verifications ADD CONSTRAINT "FK_release_verifications_release_pipeline_run_stages_pipeline_~" FOREIGN KEY (pipeline_run_stage_id, project_id, environment_id) REFERENCES release_pipeline_run_stages (id, project_id, environment_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_verifications ADD CONSTRAINT "FK_release_verifications_release_pipeline_stage_attempts_stage~" FOREIGN KEY (stage_attempt_id, pipeline_run_stage_id) REFERENCES release_pipeline_stage_attempts (id, run_stage_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_pipeline_events ADD CONSTRAINT "FK_release_pipeline_events_release_pipeline_run_stages_stage_i~" FOREIGN KEY (stage_id, run_id) REFERENCES release_pipeline_run_stages (id, run_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_pipeline_events ADD CONSTRAINT "FK_release_pipeline_events_release_pipeline_stage_attempts_att~" FOREIGN KEY (attempt_id, stage_id) REFERENCES release_pipeline_stage_attempts (id, run_stage_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    ALTER TABLE release_pipeline_run_stages ADD CONSTRAINT "FK_release_pipeline_run_stages_release_pipeline_stage_attempts~" FOREIGN KEY (current_attempt_id, id) REFERENCES release_pipeline_stage_attempts (id, run_stage_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    CREATE FUNCTION pipeline_immutable_fact() RETURNS trigger LANGUAGE plpgsql AS $$
    BEGIN
        RAISE EXCEPTION 'Pipeline versions and events are append-only' USING ERRCODE='23514';
    END;
    $$;
    CREATE TRIGGER immutable_pipeline_version BEFORE UPDATE OR DELETE ON release_pipeline_versions
        FOR EACH ROW EXECUTE FUNCTION pipeline_immutable_fact();
    CREATE TRIGGER immutable_pipeline_event BEFORE UPDATE OR DELETE ON release_pipeline_events
        FOR EACH ROW EXECUTE FUNCTION pipeline_immutable_fact();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261009030000_ReleasePipelines') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261009030000_ReleasePipelines', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 16

-- WEBAPI_MIGRATION_START 17

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261010010000_PipelineProjectionCursor') THEN
    ALTER TABLE release_pipeline_runs ADD projection_checked_at timestamptz;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261010010000_PipelineProjectionCursor') THEN
    CREATE INDEX ix_pipeline_projection_due ON release_pipeline_runs (status, projection_checked_at, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261010010000_PipelineProjectionCursor') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261010010000_PipelineProjectionCursor', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 17

-- WEBAPI_MIGRATION_START 18

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261010091247_GatewayQueryIndexes') THEN
    CREATE INDEX ix_api_routes_environment_order_path_id ON api_routes (environment_id, ((CASE WHEN normalized_path LIKE '%{*}%' THEN 2 WHEN normalized_path LIKE '%{}%' THEN 1 ELSE 0 END) * 2000000 - priority), path, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261010091247_GatewayQueryIndexes') THEN
    CREATE INDEX ix_policies_organization_name_id ON policies (organization_id, name, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261010091247_GatewayQueryIndexes') THEN
    CREATE INDEX ix_alert_rules_organization_name_id ON alert_rules (organization_id, normalized_name, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261010091247_GatewayQueryIndexes') THEN
    CREATE INDEX ix_alert_events_organization_project_started_id ON alert_events (organization_id, project_id, started_at DESC, id DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261010091247_GatewayQueryIndexes') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261010091247_GatewayQueryIndexes', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 18

-- WEBAPI_MIGRATION_START 19

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012004548_ReleaseFrozenContracts') THEN
    CREATE TABLE release_frozen_contracts (
        release_id uuid NOT NULL,
        ordinal integer NOT NULL,
        environment_id uuid NOT NULL,
        config_version bigint NOT NULL,
        api_id uuid NOT NULL,
        api_version_id uuid NOT NULL,
        frozen_json jsonb NOT NULL,
        CONSTRAINT "PK_release_frozen_contracts" PRIMARY KEY (release_id, ordinal),
        CONSTRAINT ck_frozen_contract_identity CHECK (COALESCE(jsonb_typeof(frozen_json->'api')='object' AND jsonb_typeof(frozen_json->'version')='object' AND jsonb_typeof(frozen_json->'parameters')='array' AND jsonb_typeof(frozen_json->'schemas')='array' AND frozen_json->'api'->>'id'=api_id::text AND frozen_json->'version'->>'id'=api_version_id::text AND frozen_json->'version'->>'apiId'=api_id::text, false)),
        CONSTRAINT ck_frozen_contract_version CHECK (config_version > 0 AND ordinal > 0),
        CONSTRAINT "FK_release_frozen_contracts_release_records_release_id_environ~" FOREIGN KEY (release_id, environment_id) REFERENCES release_records (id, environment_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012004548_ReleaseFrozenContracts') THEN
    CREATE INDEX ix_frozen_contract_lookup ON release_frozen_contracts (environment_id, api_id, api_version_id, config_version DESC, release_id, ordinal);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012004548_ReleaseFrozenContracts') THEN
    CREATE INDEX "IX_release_frozen_contracts_release_id_environment_id" ON release_frozen_contracts (release_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012004548_ReleaseFrozenContracts') THEN
    -- Frozen snapshot of the DTO type shapes: '?' allows null/missing;
    -- '~' allows missing optional constructor arguments with defaults only.
    -- The migration helper is created and dropped in this same transaction.
    CREATE FUNCTION public.webapi_frozen_shape_20261012004548(doc jsonb, kind text, shapes jsonb)
    RETURNS boolean LANGUAGE plpgsql AS $shape$
    DECLARE nullable boolean; optional boolean; field record; item jsonb; text_value text;
    BEGIN
      nullable := right(kind,1)='?'; optional := right(kind,1)='~';
      IF nullable OR optional THEN kind:=left(kind,length(kind)-1); END IF;
      IF doc IS NULL THEN RETURN nullable OR optional; END IF;
      IF jsonb_typeof(doc)='null' THEN RETURN nullable; END IF;
      IF right(kind,2)='[]' THEN
        IF jsonb_typeof(doc)<>'array' THEN RETURN false; END IF;
        FOR item IN SELECT value FROM jsonb_array_elements(doc) LOOP
          IF NOT public.webapi_frozen_shape_20261012004548(item,left(kind,length(kind)-2),shapes)
            THEN RETURN false; END IF;
        END LOOP;
        RETURN true;
      END IF;
      IF shapes ? kind THEN
        IF jsonb_typeof(doc)<>'object' THEN RETURN false; END IF;
        FOR field IN SELECT key,value FROM jsonb_each_text(shapes->kind) LOOP
          IF NOT public.webapi_frozen_shape_20261012004548(doc->field.key,field.value,shapes)
            THEN RETURN false; END IF;
        END LOOP;
        RETURN true;
      END IF;
      text_value:=doc #>> '{}';
      CASE kind
        WHEN 'string' THEN RETURN jsonb_typeof(doc)='string';
        WHEN 'boolean' THEN RETURN jsonb_typeof(doc)='boolean';
        WHEN 'uuid' THEN
          RETURN jsonb_typeof(doc)='string' AND text_value ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';
        WHEN 'int32' THEN
          IF jsonb_typeof(doc)<>'number' OR text_value !~ '^-?[0-9]+$' THEN RETURN false; END IF;
          PERFORM text_value::integer; RETURN true;
        WHEN 'int64' THEN
          IF jsonb_typeof(doc)<>'number' OR text_value !~ '^-?[0-9]+$' THEN RETURN false; END IF;
          PERFORM text_value::bigint; RETURN true;
        WHEN 'timestamp' THEN
          IF jsonb_typeof(doc)<>'string' OR text_value !~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,16})?(Z|[+-]((0[0-9]|1[0-3]):[0-5][0-9]|14:00))$' THEN RETURN false; END IF;
          PERFORM text_value::timestamptz; RETURN true;
        ELSE RETURN false;
      END CASE;
    EXCEPTION WHEN OTHERS THEN RETURN false;
    END $shape$;
    DO $backfill$
    DECLARE source record; candidate jsonb;
      shapes jsonb := $types${"ApiDto":{"code":"string","description":"string?","groupId":"uuid?","id":"uuid","lifecycleStatus":"string","name":"string","organizationId":"uuid","ownerUserId":"uuid","projectId":"uuid","workingRevision":"int64"},"ApplicationDto":{"activation":"string~","code":"string","id":"uuid","name":"string","organizationId":"uuid","owner":"string","projectId":"uuid?","revision":"int64","status":"string"},"ApplicationPermissionDto":{"activation":"string~","apiId":"uuid","applicationId":"uuid","environmentId":"uuid","expiresAt":"timestamp?","id":"uuid","revision":"int64","validFrom":"timestamp"},"ClusterDto":{"destinations":"DestinationDto[]","environmentId":"uuid","healthCheckEnabled":"boolean","healthCheckIntervalSec":"int32","healthCheckPath":"string","id":"uuid","loadBalancingPolicy":"string","name":"string","projectId":"uuid","revision":"int64","status":"string"},"ComparisonCounts":{"added":"int32","breaking":"int32","changed":"int32","compatible":"int32","removed":"int32","unknown":"int32"},"ContractExampleOption":{"externalValue":"string?","json":"string?","name":"string","unverifiedReason":"string?"},"DestinationDto":{"address":"string","clusterId":"uuid","enabled":"boolean","id":"uuid","metadata":"string?","name":"string","revision":"int64","weight":"int32"},"FrozenApiVersion":{"api":"ApiDto","parameters":"ParameterDto[]","schemas":"SchemaDto[]","version":"VersionDto"},"FrozenApplication":{"application":"ApplicationDto","credentials":"FrozenCredential[]","permissions":"ApplicationPermissionDto[]"},"FrozenBinding":{"policyId":"uuid","priority":"int32","routeId":"uuid"},"FrozenCredential":{"accessKey":"string","expiresAt":"timestamp","id":"uuid","revision":"int64","secretHash":"string","secretLast4":"string","status":"string","validFrom":"timestamp"},"FrozenPolicy":{"config":"string","enabled":"boolean","id":"uuid","revision":"int64","sourcePolicyId":"uuid?","type":"string"},"FrozenReleaseCandidate":{"applications":"FrozenApplication[]","baselineConfigVersion":"int64","bindings":"FrozenBinding[]","clusters":"ClusterDto[]","environmentId":"uuid","organizationId":"uuid","policies":"FrozenPolicy[]","projectId":"uuid","resourceRevisions":"ResourceRevision[]","riskReviewReferences":"FrozenRiskReviewReference[]?","routes":"RouteDto[]","sharedCredentialImpact":"SharedCredentialImpact[]?","versionIds":"uuid[]","versions":"FrozenApiVersion[]"},"FrozenRiskReviewReference":{"apiId":"uuid","comparisonId":"uuid","engineVersion":"string","inputFingerprint":"string","reportHash":"string","reviewId":"uuid","summary":"RiskReviewSummaryDto","targetVersionId":"uuid"},"ParameterDto":{"apiVersionId":"uuid","dataType":"string","description":"string?","exampleJson":"string?","examples":"ContractExampleOption[]?","id":"uuid","location":"string","name":"string","required":"boolean","schema":"string?"},"ResourceRevision":{"id":"uuid","revision":"int64","type":"string"},"RiskReviewSummaryDto":{"actorId":"uuid","apiId":"uuid","comment":"string?","comparisonId":"uuid","counts":"ComparisonCounts","coverage":"string","createdAt":"timestamp","decision":"string","engineVersion":"string","fromVersion":"string","fromVersionId":"uuid","inputFingerprint":"string","reportHash":"string","reviewId":"uuid","toVersion":"string","toVersionId":"uuid"},"RouteDto":{"apiVersionId":"uuid","clusterId":"uuid","effectiveAuthenticationMode":"string?","effectiveTimeoutMs":"int32?","enabled":"boolean","environmentId":"uuid","id":"uuid","matchOrder":"int32","methods":"string[]","normalizedPath":"string","path":"string","priority":"int32","requireApiKey":"boolean","revision":"int64","routeName":"string","timeoutMs":"int32"},"SchemaDto":{"apiVersionId":"uuid","contentType":"string","exampleJson":"string?","examples":"ContractExampleOption[]?","id":"uuid","name":"string","schemaHash":"string?","schemaJson":"string","schemaType":"string","statusCode":"int32?"},"SharedCredentialImpact":{"acknowledged":"boolean","applicationId":"uuid","requiredCredentialIds":"uuid[]","retainedApiIds":"uuid[]","sharedAcrossEnvironments":"boolean~"},"VersionDto":{"apiId":"uuid","changeType":"string","createdAt":"timestamp","createdBy":"uuid","dialect":"string?","id":"uuid","openapiDocument":"string?","openapiSource":"string?","revision":"int64","schemaHash":"string?","sealedAt":"timestamp?","sourceFormat":"string?","status":"string","version":"string"}}$types$::jsonb;
    BEGIN
      FOR source IN
        SELECT id, environment_id, to_config_version, candidate_bytes
        FROM release_records
        WHERE release_type='publish' AND to_config_version>0
          AND candidate_bytes IS NOT NULL AND approval_policy IS NOT NULL
      LOOP
        BEGIN
          candidate := convert_from(source.candidate_bytes,'UTF8')::jsonb;
          IF NOT public.webapi_frozen_shape_20261012004548(candidate,'FrozenReleaseCandidate',shapes)
            OR jsonb_typeof(candidate) IS DISTINCT FROM 'object'
            OR jsonb_typeof(candidate->'versions') IS DISTINCT FROM 'array'
            OR (candidate->>'environmentId')::uuid IS DISTINCT FROM source.environment_id
          THEN RAISE EXCEPTION 'Invalid frozen candidate'; END IF;
          INSERT INTO release_frozen_contracts
            (release_id, ordinal, environment_id, config_version, api_id, api_version_id, frozen_json)
          SELECT source.id, v.ordinal::integer, source.environment_id, source.to_config_version,
            (v.value->'api'->>'id')::uuid, (v.value->'version'->>'id')::uuid, v.value
          FROM jsonb_array_elements(candidate->'versions') WITH ORDINALITY AS v(value,ordinal);
        EXCEPTION WHEN OTHERS THEN
          -- Never echo a candidate containing consumer or credential data.
          RAISE EXCEPTION 'Invalid legacy frozen contract for release %',source.id
            USING ERRCODE='22023';
        END;
      END LOOP;
    END $backfill$;
    DROP FUNCTION public.webapi_frozen_shape_20261012004548(jsonb,text,jsonb);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261012004548_ReleaseFrozenContracts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261012004548_ReleaseFrozenContracts', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 19

-- WEBAPI_MIGRATION_START 20

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015010000_AccountPasswordLifecycle') THEN
    ALTER TABLE users ADD must_change_password boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015010000_AccountPasswordLifecycle') THEN
    ALTER TABLE users ADD password_changed_at timestamptz;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015010000_AccountPasswordLifecycle') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261015010000_AccountPasswordLifecycle', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 20

-- WEBAPI_MIGRATION_START 21

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE TABLE credential_usage_collectors (
        node_id uuid NOT NULL,
        collector_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        protocol varchar(8) NOT NULL DEFAULT '1.0',
        accepted_sequence bigint NOT NULL DEFAULT 0,
        accepted_through timestamptz,
        created_at timestamptz NOT NULL DEFAULT (NOW()),
        closed_at timestamptz,
        CONSTRAINT "PK_credential_usage_collectors" PRIMARY KEY (node_id, collector_id),
        CONSTRAINT ck_usage_collector_protocol CHECK (protocol = '1.0'),
        CONSTRAINT ck_usage_collector_sequence CHECK (accepted_sequence >= 0),
        CONSTRAINT "FK_credential_usage_collectors_gateway_nodes_node_id" FOREIGN KEY (node_id) REFERENCES gateway_nodes (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE TABLE credential_usage_coverage (
        environment_id uuid NOT NULL,
        node_id uuid NOT NULL,
        hour_start timestamptz NOT NULL,
        intervals jsonb NOT NULL DEFAULT ('[]'::jsonb),
        gaps jsonb NOT NULL DEFAULT ('[]'::jsonb),
        durable_through timestamptz,
        accepted_through timestamptz,
        state varchar(16) NOT NULL DEFAULT 'Partial',
        updated_at timestamptz NOT NULL DEFAULT (NOW()),
        CONSTRAINT "PK_credential_usage_coverage" PRIMARY KEY (environment_id, node_id, hour_start),
        CONSTRAINT ck_usage_coverage_arrays CHECK (jsonb_typeof(intervals)='array' AND jsonb_typeof(gaps)='array'),
        CONSTRAINT ck_usage_coverage_budget CHECK (octet_length(intervals::text)+octet_length(gaps::text)<=1048576),
        CONSTRAINT ck_usage_coverage_hour CHECK (hour_start = date_trunc('hour', hour_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'),
        CONSTRAINT ck_usage_coverage_state CHECK (state IN ('Available','NoData','Partial','Unknown','Unavailable'))
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE TABLE credential_usage_hourly (
        environment_id uuid NOT NULL,
        credential_id uuid NOT NULL,
        hour_start timestamptz NOT NULL,
        application_id uuid NOT NULL,
        count bigint NOT NULL,
        last_used_at timestamptz NOT NULL,
        CONSTRAINT "PK_credential_usage_hourly" PRIMARY KEY (environment_id, credential_id, hour_start),
        CONSTRAINT ck_usage_hourly_count CHECK (count > 0),
        CONSTRAINT ck_usage_hourly_hour CHECK (hour_start = date_trunc('hour', hour_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'),
        CONSTRAINT ck_usage_hourly_last_used CHECK (last_used_at >= hour_start AND last_used_at < hour_start + INTERVAL '1 hour')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE TABLE credential_usage_batches (
        node_id uuid NOT NULL,
        collector_id uuid NOT NULL,
        sequence bigint NOT NULL,
        core_hash char(64) NOT NULL,
        applied_at timestamptz NOT NULL DEFAULT (NOW()),
        accepted_through timestamptz,
        CONSTRAINT "PK_credential_usage_batches" PRIMARY KEY (node_id, collector_id, sequence),
        CONSTRAINT ck_usage_batch_hash CHECK (core_hash ~ '^[0-9a-f]{64}$'),
        CONSTRAINT ck_usage_batch_sequence CHECK (sequence > 0),
        CONSTRAINT "FK_credential_usage_batches_credential_usage_collectors_node_i~" FOREIGN KEY (node_id, collector_id) REFERENCES credential_usage_collectors (node_id, collector_id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE TABLE credential_usage_collector_instances (
        node_id uuid NOT NULL,
        collector_id uuid NOT NULL,
        instance_id uuid NOT NULL,
        registered_at timestamptz NOT NULL DEFAULT (NOW()),
        ended_at timestamptz,
        identity_proof varchar(32) NOT NULL DEFAULT 'current',
        CONSTRAINT "PK_credential_usage_collector_instances" PRIMARY KEY (node_id, collector_id, instance_id),
        CONSTRAINT "FK_credential_usage_collector_instances_credential_usage_colle~" FOREIGN KEY (node_id, collector_id) REFERENCES credential_usage_collectors (node_id, collector_id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE INDEX ix_usage_batches_applied ON credential_usage_batches (applied_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE INDEX ix_usage_instances_ended ON credential_usage_collector_instances (ended_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE INDEX ix_usage_collectors_closed ON credential_usage_collectors (closed_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE INDEX ix_usage_coverage_retention ON credential_usage_coverage (hour_start);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE INDEX ix_usage_hourly_application_window ON credential_usage_hourly (application_id, hour_start, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    CREATE INDEX ix_usage_hourly_retention ON credential_usage_hourly (hour_start);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015020000_CredentialUsageTracking') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261015020000_CredentialUsageTracking', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 21

-- Integrated governance credential core; generated by the locked EF tool.
-- WEBAPI_MIGRATION_START 22

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    DO $$ BEGIN
      IF EXISTS (SELECT 1 FROM application_credentials c LEFT JOIN applications a ON a.id=c.application_id
        WHERE a.id IS NULL OR a.organization_id IS NULL)
      THEN RAISE EXCEPTION 'credential_governance_preflight_failed: credential scope cannot be established'; END IF;
      IF EXISTS (SELECT 1 FROM application_api_permissions g
        JOIN applications a ON a.id=g.application_id JOIN apis i ON i.id=g.api_id
        JOIN environments e ON e.id=g.environment_id JOIN projects p ON p.id=e.project_id
        WHERE i.project_id<>e.project_id OR i.organization_id<>a.organization_id
          OR p.organization_id<>a.organization_id OR (a.project_id IS NOT NULL AND a.project_id<>e.project_id))
      THEN RAISE EXCEPTION 'credential_governance_preflight_failed: inconsistent application permission scope'; END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    DROP INDEX "IX_application_credentials_application_id";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE environments ADD authorization_revision bigint NOT NULL DEFAULT 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE environments ADD require_environment_credential boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE applications ADD authorization_revision bigint NOT NULL DEFAULT 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE applications ADD owner_revision bigint NOT NULL DEFAULT 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE applications ADD owner_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD binding_mode varchar(24) NOT NULL DEFAULT 'LegacyGlobal';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD environment_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD expiry_revision bigint NOT NULL DEFAULT 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD organization_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    UPDATE application_credentials c SET organization_id=a.organization_id
    FROM applications a WHERE a.id=c.application_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ALTER COLUMN organization_id SET NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD project_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD CONSTRAINT "AK_application_credentials_id_application_id_organization_id" UNIQUE (id, application_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    CREATE INDEX "IX_applications_owner_user_id" ON applications (owner_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    CREATE INDEX "IX_application_credentials_application_id_environment_id_status" ON application_credentials (application_id, environment_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    CREATE INDEX "IX_application_credentials_application_id_organization_id_id" ON application_credentials (application_id, organization_id, id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    CREATE INDEX "IX_application_credentials_environment_id_expires_at" ON application_credentials (environment_id, expires_at) WHERE status = 'Active';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    CREATE INDEX "IX_application_credentials_environment_id_project_id" ON application_credentials (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    CREATE INDEX "IX_application_credentials_last_used_at" ON application_credentials (last_used_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    CREATE INDEX "IX_application_credentials_project_id_organization_id" ON application_credentials (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD CONSTRAINT ck_credential_binding CHECK ((binding_mode = 'LegacyGlobal' AND environment_id IS NULL AND project_id IS NULL) OR (binding_mode = 'EnvironmentBound' AND environment_id IS NOT NULL AND project_id IS NOT NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD CONSTRAINT ck_credential_expiry_revision CHECK (expiry_revision > 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD CONSTRAINT "FK_application_credentials_applications_application_id_organiz~" FOREIGN KEY (application_id, organization_id) REFERENCES applications (id, organization_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD CONSTRAINT "FK_application_credentials_environments_environment_id_project~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE application_credentials ADD CONSTRAINT "FK_application_credentials_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    ALTER TABLE applications ADD CONSTRAINT "FK_applications_users_owner_user_id" FOREIGN KEY (owner_user_id) REFERENCES users (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    CREATE FUNCTION credential_governance_scope_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='UPDATE' AND (NEW.application_id,NEW.organization_id,NEW.project_id,NEW.environment_id,NEW.binding_mode)
     IS DISTINCT FROM (OLD.application_id,OLD.organization_id,OLD.project_id,OLD.environment_id,OLD.binding_mode)
  THEN RAISE EXCEPTION 'credential_binding_immutable' USING ERRCODE='23514'; END IF;
  IF NEW.binding_mode='EnvironmentBound' AND EXISTS(SELECT 1 FROM applications a WHERE a.id=NEW.application_id
     AND (a.organization_id<>NEW.organization_id OR (a.project_id IS NOT NULL AND a.project_id<>NEW.project_id)))
  THEN RAISE EXCEPTION 'credential_application_project_mismatch' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
    CREATE CONSTRAINT TRIGGER credential_governance_scope_check AFTER INSERT OR UPDATE ON application_credentials
      DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW EXECUTE FUNCTION credential_governance_scope_guard();
    CREATE FUNCTION application_credential_scope_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF NEW.project_id IS NOT NULL AND EXISTS(SELECT 1 FROM application_credentials c WHERE c.application_id=NEW.id
    AND c.binding_mode='EnvironmentBound' AND c.project_id<>NEW.project_id)
  THEN RAISE EXCEPTION 'credential_application_project_mismatch' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
    CREATE CONSTRAINT TRIGGER application_credential_scope_check AFTER UPDATE ON applications
      DEFERRABLE INITIALLY IMMEDIATE FOR EACH ROW EXECUTE FUNCTION application_credential_scope_guard();
    ALTER TABLE applications ADD CONSTRAINT ck_application_governance_revisions CHECK(owner_revision>0 AND authorization_revision>0);
    ALTER TABLE environments ADD CONSTRAINT ck_environment_authorization_revision CHECK(authorization_revision>0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103183543_Governance24CredentialCore') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261103183543_Governance24CredentialCore', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 22

-- Integrated environment trust; generated by the locked EF tool.
-- WEBAPI_MIGRATION_START 23

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103184504_Governance24EnvironmentTrust') THEN
    CREATE TABLE environment_security_initializations (
        environment_id uuid NOT NULL,
        project_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        state varchar(32) NOT NULL DEFAULT 'Unknown',
        evidence_hash varchar(64),
        fresh_proof_id uuid,
        confirmed_by uuid,
        confirmed_at timestamp with time zone,
        confirmation_reason varchar(1000),
        updated_at timestamp with time zone NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_environment_security_initializations" PRIMARY KEY (environment_id),
        CONSTRAINT ck_environment_security_initialization CHECK (revision > 0 AND (
      (state = 'Unknown' AND evidence_hash IS NULL AND fresh_proof_id IS NULL AND confirmed_by IS NULL AND confirmed_at IS NULL AND confirmation_reason IS NULL)
      OR (state = 'FreshNonStrict' AND fresh_proof_id IS NOT NULL AND evidence_hash IS NULL AND confirmed_by IS NULL AND confirmed_at IS NULL AND confirmation_reason IS NULL)
      OR (state = 'VerifiedLegacyNetwork' AND evidence_hash IS NOT NULL AND evidence_hash ~ '^[a-f0-9]{64}$' AND fresh_proof_id IS NULL AND confirmed_by IS NOT NULL AND confirmed_at IS NOT NULL AND confirmation_reason IS NOT NULL AND length(trim(confirmation_reason)) BETWEEN 1 AND 1000)
      OR (state = 'Governed' AND (evidence_hash IS NULL OR evidence_hash ~ '^[a-f0-9]{64}$') AND ((confirmed_by IS NULL AND confirmed_at IS NULL AND confirmation_reason IS NULL) OR (confirmed_by IS NOT NULL AND confirmed_at IS NOT NULL AND confirmation_reason IS NOT NULL AND length(trim(confirmation_reason)) BETWEEN 1 AND 1000)))
    )),
        CONSTRAINT "FK_environment_security_initializations_environments_environme~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_environment_security_initializations_projects_project_id_or~" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_environment_security_initializations_users_confirmed_by" FOREIGN KEY (confirmed_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103184504_Governance24EnvironmentTrust') THEN
    CREATE INDEX "IX_environment_security_initializations_confirmed_by" ON environment_security_initializations (confirmed_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103184504_Governance24EnvironmentTrust') THEN
    CREATE INDEX "IX_environment_security_initializations_environment_id_project~" ON environment_security_initializations (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103184504_Governance24EnvironmentTrust') THEN
    CREATE INDEX "IX_environment_security_initializations_project_id_organizatio~" ON environment_security_initializations (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103184504_Governance24EnvironmentTrust') THEN
    INSERT INTO environment_security_initializations
      (environment_id,project_id,organization_id,state,updated_at,revision)
    SELECT e.id,e.project_id,p.organization_id,'Unknown',now(),1
    FROM environments e JOIN projects p ON p.id=e.project_id;

    CREATE FUNCTION environment_initialization_transition_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='DELETE' THEN
    RAISE EXCEPTION 'environment_initialization_deletion_blocked' USING ERRCODE='23514';
  END IF;
  IF (NEW.environment_id,NEW.project_id,NEW.organization_id) IS DISTINCT FROM
     (OLD.environment_id,OLD.project_id,OLD.organization_id) OR NEW.revision<>OLD.revision+1 THEN
    RAISE EXCEPTION 'environment_initialization_identity_or_revision_invalid' USING ERRCODE='23514';
  END IF;
  IF (OLD.state='Unknown' AND NEW.state='FreshNonStrict') OR
     (OLD.state='Governed' AND NEW.state<>'Governed') OR
     (OLD.state IN ('FreshNonStrict','VerifiedLegacyNetwork') AND NEW.state NOT IN (OLD.state,'Governed')) THEN
    RAISE EXCEPTION 'environment_initialization_regression' USING ERRCODE='23514';
  END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER environment_initialization_transition_check
      BEFORE UPDATE OR DELETE ON environment_security_initializations
      FOR EACH ROW EXECUTE FUNCTION environment_initialization_transition_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103184504_Governance24EnvironmentTrust') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261103184504_Governance24EnvironmentTrust', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 23

-- Integrated route authorization; generated by the locked EF tool.
-- WEBAPI_MIGRATION_START 24

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    DO $$ BEGIN
      IF EXISTS (SELECT 1 FROM application_api_permissions g
        LEFT JOIN applications a ON a.id=g.application_id LEFT JOIN apis i ON i.id=g.api_id
        LEFT JOIN environments e ON e.id=g.environment_id LEFT JOIN projects p ON p.id=e.project_id
        WHERE a.id IS NULL OR i.id IS NULL OR e.id IS NULL OR p.id IS NULL
          OR i.project_id<>e.project_id OR i.organization_id<>a.organization_id
          OR p.organization_id<>a.organization_id OR (a.project_id IS NOT NULL AND a.project_id<>e.project_id))
      THEN RAISE EXCEPTION 'route_governance_preflight_failed: inconsistent legacy grant scope'; END IF;
      IF EXISTS (SELECT 1 FROM route_policy_bindings b LEFT JOIN policies p ON p.id=b.policy_id WHERE p.id IS NULL)
      THEN RAISE EXCEPTION 'route_governance_preflight_failed: missing policy'; END IF;
      IF EXISTS (SELECT 1 FROM route_policy_bindings b JOIN policies p ON p.id=b.policy_id
        GROUP BY b.route_id,p.type HAVING count(*)>1)
      THEN RAISE EXCEPTION 'route_governance_preflight_failed: ambiguous policy bindings'; END IF;
      IF EXISTS (SELECT 1 FROM route_policy_bindings b JOIN policies p ON p.id=b.policy_id
        WHERE p.type='authentication' AND (jsonb_typeof(p.config)<>'object'
          OR jsonb_typeof(p.config->'mode') IS DISTINCT FROM 'string'
          OR p.config->>'mode' NOT IN ('ApiKey','Anonymous','JWT')))
      THEN RAISE EXCEPTION 'route_governance_preflight_failed: invalid authentication mode'; END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    DROP INDEX "IX_application_api_permissions_api_id";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    DROP INDEX "IX_application_api_permissions_environment_id";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    DROP INDEX "IX_api_routes_api_version_id";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD expiry_revision bigint NOT NULL DEFAULT 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD mode character varying(24) NOT NULL DEFAULT 'RouteMethod';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD organization_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD project_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD revision_transaction_id bigint NOT NULL DEFAULT (txid_current());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE api_routes ADD api_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE api_routes ADD authentication_mode character varying(16) NOT NULL DEFAULT 'ApiKey';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE api_routes ADD authorization_revision bigint NOT NULL DEFAULT 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    UPDATE application_api_permissions g SET mode='LegacyApi',project_id=e.project_id,organization_id=p.organization_id
    FROM environments e JOIN projects p ON p.id=e.project_id WHERE e.id=g.environment_id;
    UPDATE api_routes r SET api_id=v.api_id FROM api_versions v WHERE v.id=r.api_version_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    UPDATE application_api_permissions SET organization_id = '00000000-0000-0000-0000-000000000000'::uuid WHERE organization_id IS NULL;
    ALTER TABLE application_api_permissions ALTER COLUMN organization_id SET NOT NULL;
    ALTER TABLE application_api_permissions ALTER COLUMN organization_id SET DEFAULT ('00000000-0000-0000-0000-000000000000'::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    UPDATE application_api_permissions SET project_id = '00000000-0000-0000-0000-000000000000'::uuid WHERE project_id IS NULL;
    ALTER TABLE application_api_permissions ALTER COLUMN project_id SET NOT NULL;
    ALTER TABLE application_api_permissions ALTER COLUMN project_id SET DEFAULT ('00000000-0000-0000-0000-000000000000'::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    UPDATE api_routes SET api_id = '00000000-0000-0000-0000-000000000000'::uuid WHERE api_id IS NULL;
    ALTER TABLE api_routes ALTER COLUMN api_id SET NOT NULL;
    ALTER TABLE api_routes ALTER COLUMN api_id SET DEFAULT ('00000000-0000-0000-0000-000000000000'::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD CONSTRAINT "AK_application_api_permissions_id_environment_id_api_id" UNIQUE (id, environment_id, api_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE apis ADD CONSTRAINT "AK_apis_id_project_id_organization_id" UNIQUE (id, project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE api_versions ADD CONSTRAINT "AK_api_versions_id_api_id" UNIQUE (id, api_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE api_routes ADD CONSTRAINT "AK_api_routes_id_environment_id_api_id" UNIQUE (id, environment_id, api_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE TABLE application_route_method_grants (
        permission_id uuid NOT NULL,
        route_id uuid NOT NULL,
        method character varying(8) NOT NULL,
        environment_id uuid NOT NULL,
        api_id uuid NOT NULL,
        authorization_revision bigint NOT NULL,
        CONSTRAINT "PK_application_route_method_grants" PRIMARY KEY (permission_id, route_id, method),
        CONSTRAINT ck_route_method_grant CHECK (authorization_revision>0 AND method IN ('GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS')),
        CONSTRAINT "FK_application_route_method_grants_api_routes_route_id_environ~" FOREIGN KEY (route_id, environment_id, api_id) REFERENCES api_routes (id, environment_id, api_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_application_route_method_grants_application_api_permissions~" FOREIGN KEY (permission_id, environment_id, api_id) REFERENCES application_api_permissions (id, environment_id, api_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE INDEX "IX_application_api_permissions_api_id_project_id_organization_~" ON application_api_permissions (api_id, project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE INDEX "IX_application_api_permissions_application_id_organization_id" ON application_api_permissions (application_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE INDEX "IX_application_api_permissions_environment_id_project_id" ON application_api_permissions (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE INDEX "IX_application_api_permissions_project_id_organization_id" ON application_api_permissions (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD CONSTRAINT ck_permission_mode CHECK (mode IN ('LegacyApi','RouteMethod'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE INDEX "IX_api_routes_api_version_id_api_id" ON api_routes (api_version_id, api_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE api_routes ADD CONSTRAINT ck_route_authorization CHECK (authorization_revision>0 AND authentication_mode IN ('ApiKey','Anonymous','JWT'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE INDEX "IX_application_route_method_grants_permission_id_environment_i~" ON application_route_method_grants (permission_id, environment_id, api_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE INDEX "IX_application_route_method_grants_route_id" ON application_route_method_grants (route_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE INDEX "IX_application_route_method_grants_route_id_environment_id_api~" ON application_route_method_grants (route_id, environment_id, api_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE api_routes ADD CONSTRAINT "FK_api_routes_api_versions_api_version_id_api_id" FOREIGN KEY (api_version_id, api_id) REFERENCES api_versions (id, api_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD CONSTRAINT "FK_application_api_permissions_apis_api_id_project_id_organiza~" FOREIGN KEY (api_id, project_id, organization_id) REFERENCES apis (id, project_id, organization_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD CONSTRAINT "FK_application_api_permissions_applications_application_id_org~" FOREIGN KEY (application_id, organization_id) REFERENCES applications (id, organization_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD CONSTRAINT "FK_application_api_permissions_environments_environment_id_pro~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    ALTER TABLE application_api_permissions ADD CONSTRAINT "FK_application_api_permissions_projects_project_id_organizatio~" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    CREATE FUNCTION route_effective_auth_mode(route uuid) RETURNS text LANGUAGE sql STABLE AS $$
  SELECT COALESCE((SELECT p.config::jsonb->>'mode' FROM route_policy_bindings b JOIN policies p ON p.id=b.policy_id
    WHERE b.route_id=route AND p.type='authentication' AND p.enabled ORDER BY b.priority,p.id LIMIT 1),'ApiKey')
$$;
    UPDATE api_routes SET authentication_mode=route_effective_auth_mode(id);
    CREATE FUNCTION route_authorization_guard() RETURNS trigger LANGUAGE plpgsql AS $$ DECLARE actual uuid; BEGIN
  SELECT api_id INTO actual FROM api_versions WHERE id=NEW.api_version_id;
  IF TG_OP='INSERT' AND NEW.api_id='00000000-0000-0000-0000-000000000000'::uuid THEN NEW.api_id=actual; END IF;
  IF NEW.api_id IS DISTINCT FROM actual THEN RAISE EXCEPTION 'route_api_mismatch' USING ERRCODE='23514'; END IF;
  IF TG_OP='UPDATE' THEN
    IF NEW.environment_id<>OLD.environment_id THEN RAISE EXCEPTION 'route_environment_immutable' USING ERRCODE='23514'; END IF;
    IF (NEW.path,NEW.normalized_path,NEW.methods,NEW.api_version_id,NEW.authentication_mode)
       IS DISTINCT FROM (OLD.path,OLD.normalized_path,OLD.methods,OLD.api_version_id,OLD.authentication_mode)
    THEN NEW.authorization_revision=OLD.authorization_revision+1;
    ELSIF NEW.authorization_revision<>OLD.authorization_revision THEN RAISE EXCEPTION 'route_authorization_revision_immutable' USING ERRCODE='23514'; END IF;
  END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER route_authorization_check BEFORE INSERT OR UPDATE ON api_routes FOR EACH ROW EXECUTE FUNCTION route_authorization_guard();
    CREATE FUNCTION route_auth_binding_guard() RETURNS trigger LANGUAGE plpgsql AS $$ DECLARE route uuid; actual text; BEGIN
  IF TG_TABLE_NAME='policies' THEN
    FOR route IN SELECT b.route_id FROM route_policy_bindings b WHERE b.policy_id=NEW.id LOOP
      actual=route_effective_auth_mode(route);
      UPDATE api_routes SET authentication_mode=actual,revision=revision+1 WHERE id=route AND authentication_mode<>actual;
    END LOOP;
  ELSE
    FOR route IN SELECT DISTINCT x FROM unnest(CASE WHEN TG_OP='INSERT' THEN ARRAY[NEW.route_id] WHEN TG_OP='DELETE' THEN ARRAY[OLD.route_id] ELSE ARRAY[NEW.route_id,OLD.route_id] END) x LOOP
      actual=route_effective_auth_mode(route);
      UPDATE api_routes SET authentication_mode=actual,revision=revision+1 WHERE id=route AND authentication_mode<>actual;
    END LOOP;
  END IF;
  RETURN NULL;
END $$;
    CREATE CONSTRAINT TRIGGER policy_route_auth_check AFTER UPDATE ON policies DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION route_auth_binding_guard();
    CREATE CONSTRAINT TRIGGER binding_route_auth_check AFTER INSERT OR UPDATE OR DELETE ON route_policy_bindings DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION route_auth_binding_guard();
    CREATE FUNCTION permission_parent_guard() RETURNS trigger LANGUAGE plpgsql AS $$ DECLARE actualproject uuid; actualorg uuid; BEGIN
  NEW.revision_transaction_id=txid_current();
  SELECT e.project_id,p.organization_id INTO actualproject,actualorg FROM environments e JOIN projects p ON p.id=e.project_id WHERE e.id=NEW.environment_id;
  IF TG_OP='INSERT' THEN
    IF NEW.project_id='00000000-0000-0000-0000-000000000000'::uuid THEN NEW.project_id=actualproject; END IF;
    IF NEW.organization_id='00000000-0000-0000-0000-000000000000'::uuid THEN NEW.organization_id=actualorg; END IF;
  END IF;
  IF (NEW.project_id,NEW.organization_id) IS DISTINCT FROM (actualproject,actualorg) THEN RAISE EXCEPTION 'permission_scope_mismatch' USING ERRCODE='23514'; END IF;
  IF TG_OP='UPDATE' THEN
    IF (NEW.application_id,NEW.environment_id,NEW.api_id,NEW.project_id,NEW.organization_id) IS DISTINCT FROM (OLD.application_id,OLD.environment_id,OLD.api_id,OLD.project_id,OLD.organization_id)
    THEN RAISE EXCEPTION 'permission_scope_immutable' USING ERRCODE='23514'; END IF;
    NEW.revision=GREATEST(NEW.revision,OLD.revision+1);
  END IF;
  IF NOT EXISTS(SELECT 1 FROM applications a JOIN apis api ON api.id=NEW.api_id JOIN environments e ON e.id=NEW.environment_id
      JOIN projects p ON p.id=e.project_id WHERE a.id=NEW.application_id AND a.organization_id=api.organization_id
      AND p.organization_id=api.organization_id AND api.project_id=e.project_id AND (a.project_id IS NULL OR a.project_id=api.project_id))
  THEN RAISE EXCEPTION 'permission_scope_mismatch' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER permission_parent_check BEFORE INSERT OR UPDATE ON application_api_permissions FOR EACH ROW EXECUTE FUNCTION permission_parent_guard();
    CREATE FUNCTION permission_application_scope_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF EXISTS(SELECT 1 FROM application_api_permissions g WHERE g.application_id=NEW.id AND (g.organization_id<>NEW.organization_id OR NEW.project_id IS NOT NULL AND g.project_id<>NEW.project_id))
  THEN RAISE EXCEPTION 'permission_application_scope_mismatch' USING ERRCODE='23514'; END IF;
  RETURN NULL;
END $$;
    CREATE CONSTRAINT TRIGGER permission_application_scope_check AFTER UPDATE ON applications DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION permission_application_scope_guard();
    CREATE FUNCTION route_grant_approval_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='UPDATE' AND (NEW.permission_id,NEW.environment_id,NEW.api_id,NEW.route_id,NEW.method)
      IS DISTINCT FROM (OLD.permission_id,OLD.environment_id,OLD.api_id,OLD.route_id,OLD.method)
  THEN RAISE EXCEPTION 'route_grant_identity_immutable' USING ERRCODE='23514'; END IF;
  IF NOT EXISTS(SELECT 1 FROM api_routes r WHERE r.id=NEW.route_id AND r.environment_id=NEW.environment_id AND r.api_id=NEW.api_id
    AND r.authorization_revision=NEW.authorization_revision AND NEW.method=ANY(r.methods))
  THEN RAISE EXCEPTION 'route_grant_unapproved_identity' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER route_grant_approval_check BEFORE INSERT OR UPDATE ON application_route_method_grants FOR EACH ROW EXECUTE FUNCTION route_grant_approval_guard();
    CREATE FUNCTION route_grant_parent_guard() RETURNS trigger LANGUAGE plpgsql AS $$ DECLARE target uuid; parentmode text; parentxid bigint; BEGIN
  target=CASE WHEN TG_OP='DELETE' THEN OLD.permission_id ELSE NEW.permission_id END;
  SELECT mode,revision_transaction_id INTO parentmode,parentxid FROM application_api_permissions WHERE id=target;
  IF FOUND THEN
    IF TG_OP<>'DELETE' AND parentmode<>'RouteMethod' THEN RAISE EXCEPTION 'route_grant_parent_mode' USING ERRCODE='23514'; END IF;
    IF parentxid<>txid_current() THEN RAISE EXCEPTION 'route_grant_parent_revision_transaction_required' USING ERRCODE='23514'; END IF;
  END IF;
  RETURN NULL;
END $$;
    CREATE CONSTRAINT TRIGGER route_grant_parent_check AFTER INSERT OR UPDATE OR DELETE ON application_route_method_grants
      DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION route_grant_parent_guard();
    CREATE FUNCTION permission_mode_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF EXISTS(SELECT 1 FROM application_api_permissions p JOIN application_route_method_grants g ON g.permission_id=p.id WHERE p.id=NEW.id AND p.mode<>'RouteMethod')
  THEN RAISE EXCEPTION 'route_grant_parent_mode' USING ERRCODE='23514'; END IF;
  RETURN NULL;
END $$;
    CREATE CONSTRAINT TRIGGER permission_mode_check AFTER INSERT OR UPDATE ON application_api_permissions
      DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION permission_mode_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185114_Governance24RouteMethods') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261103185114_Governance24RouteMethods', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 24

-- Integrated security ledger; generated by the locked EF tool.
-- WEBAPI_MIGRATION_START 25

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    ALTER TABLE gateway_nodes ADD CONSTRAINT "AK_gateway_nodes_id_environment_id" UNIQUE (id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE environment_security_states (
        environment_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        security_generation uuid NOT NULL,
        epoch bigint NOT NULL,
        head_hash character varying(64) NOT NULL,
        initial_require_environment_credentials boolean,
        require_environment_credentials boolean NOT NULL,
        minimum_snapshot_schema character varying(24) NOT NULL,
        lease_seconds integer NOT NULL DEFAULT 30,
        lease_refresh_seconds integer NOT NULL DEFAULT 5,
        maximum_clock_skew_seconds integer NOT NULL DEFAULT 2,
        revision bigint NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_environment_security_states" PRIMARY KEY (environment_id),
        CONSTRAINT "AK_environment_security_states_environment_id_security_generat~" UNIQUE (environment_id, security_generation),
        CONSTRAINT ck_security_lease_policy CHECK (lease_seconds BETWEEN 5 AND 30 AND lease_refresh_seconds BETWEEN 1 AND 15 AND lease_refresh_seconds<=lease_seconds/2 AND maximum_clock_skew_seconds BETWEEN 0 AND 2 AND minimum_snapshot_schema IN ('2.0','2.1','2.2','2.3','2.4')),
        CONSTRAINT ck_security_state CHECK (epoch >= 0 AND revision > 0 AND length(head_hash)=64 AND security_generation <> '00000000-0000-0000-0000-000000000000'::uuid),
        CONSTRAINT "FK_environment_security_states_environments_environment_id_pro~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_environment_security_states_projects_project_id_organizatio~" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE security_operations (
        id uuid NOT NULL,
        organization_id uuid NOT NULL,
        application_id uuid,
        credential_id uuid,
        kind character varying(32) NOT NULL,
        requested_by uuid NOT NULL,
        reason character varying(1000) NOT NULL,
        committed_at timestamp with time zone NOT NULL,
        revision bigint NOT NULL,
        CONSTRAINT "PK_security_operations" PRIMARY KEY (id),
        CONSTRAINT "AK_security_operations_id_organization_id" UNIQUE (id, organization_id),
        CONSTRAINT ck_security_operation CHECK (revision > 0 AND ((kind = 'CredentialRevoke' AND application_id IS NOT NULL AND credential_id IS NOT NULL) OR kind = 'EnvironmentPolicyCutover')),
        CONSTRAINT "FK_security_operations_application_credentials_credential_id_a~" FOREIGN KEY (credential_id, application_id, organization_id) REFERENCES application_credentials (id, application_id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_operations_applications_application_id_organizatio~" FOREIGN KEY (application_id, organization_id) REFERENCES applications (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_operations_organizations_organization_id" FOREIGN KEY (organization_id) REFERENCES organizations (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_operations_users_requested_by" FOREIGN KEY (requested_by) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE gateway_security_leases (
        node_id uuid NOT NULL,
        instance_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        security_generation uuid NOT NULL,
        epoch bigint NOT NULL,
        checkpoint_hash character varying(64) NOT NULL,
        lease_id uuid NOT NULL,
        issued_at timestamp with time zone NOT NULL,
        fresh_until timestamp with time zone NOT NULL,
        last_issued_lease_until timestamp with time zone NOT NULL,
        signing_key_id character varying(64) NOT NULL,
        lease_seconds integer NOT NULL,
        revision bigint NOT NULL,
        CONSTRAINT "PK_gateway_security_leases" PRIMARY KEY (node_id, instance_id),
        CONSTRAINT ck_gateway_security_lease CHECK (epoch>=0 AND revision>0 AND lease_seconds BETWEEN 5 AND 30 AND last_issued_lease_until>=fresh_until AND fresh_until>issued_at AND fresh_until<=issued_at+make_interval(secs=>lease_seconds) AND instance_id<>'00000000-0000-0000-0000-000000000000'::uuid AND checkpoint_hash ~ '^[a-f0-9]{64}$'),
        CONSTRAINT "FK_gateway_security_leases_environment_security_states_environ~" FOREIGN KEY (environment_id, security_generation) REFERENCES environment_security_states (environment_id, security_generation) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_leases_environments_environment_id_project~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_leases_gateway_nodes_node_id_environment_id" FOREIGN KEY (node_id, environment_id) REFERENCES gateway_nodes (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_leases_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE security_events (
        environment_id uuid NOT NULL,
        security_generation uuid NOT NULL,
        epoch bigint NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        operation_id uuid NOT NULL,
        event_type character varying(32) NOT NULL,
        credential_id uuid,
        policy_json jsonb,
        previous_hash character varying(64) NOT NULL,
        event_hash character varying(64) NOT NULL,
        occurred_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_security_events" PRIMARY KEY (environment_id, security_generation, epoch),
        CONSTRAINT "AK_security_events_operation_id_environment_id" UNIQUE (operation_id, environment_id),
        CONSTRAINT ck_security_event CHECK (epoch > 0 AND length(previous_hash)=64 AND length(event_hash)=64 AND ((event_type='CredentialRevoked' AND credential_id IS NOT NULL AND policy_json IS NULL) OR (event_type='EnvironmentPolicyChanged' AND credential_id IS NULL AND policy_json IS NOT NULL))),
        CONSTRAINT "FK_security_events_application_credentials_credential_id" FOREIGN KEY (credential_id) REFERENCES application_credentials (id) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_events_environment_security_states_environment_id_~" FOREIGN KEY (environment_id, security_generation) REFERENCES environment_security_states (environment_id, security_generation) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_events_environments_environment_id_project_id" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_events_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_events_security_operations_operation_id_organizati~" FOREIGN KEY (operation_id, organization_id) REFERENCES security_operations (id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE gateway_security_current_receipts (
        node_id uuid NOT NULL,
        instance_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        security_generation uuid NOT NULL,
        epoch bigint NOT NULL,
        checkpoint_hash character varying(64) NOT NULL,
        head_hash character varying(64) NOT NULL,
        lease_id uuid NOT NULL,
        fresh_until timestamp with time zone NOT NULL,
        received_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_gateway_security_current_receipts" PRIMARY KEY (node_id, instance_id),
        CONSTRAINT ck_current_security_receipt CHECK (epoch>=0 AND checkpoint_hash ~ '^[a-f0-9]{64}$' AND head_hash ~ '^[a-f0-9]{64}$' AND fresh_until>received_at AND lease_id<>'00000000-0000-0000-0000-000000000000'::uuid),
        CONSTRAINT "FK_gateway_security_current_receipts_environment_security_stat~" FOREIGN KEY (environment_id, security_generation) REFERENCES environment_security_states (environment_id, security_generation) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_current_receipts_environments_environment_~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_current_receipts_gateway_nodes_node_id_env~" FOREIGN KEY (node_id, environment_id) REFERENCES gateway_nodes (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_current_receipts_gateway_security_leases_n~" FOREIGN KEY (node_id, instance_id) REFERENCES gateway_security_leases (node_id, instance_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_current_receipts_projects_project_id_organ~" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE credential_revocations (
        environment_id uuid NOT NULL,
        credential_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        application_id uuid NOT NULL,
        security_generation uuid NOT NULL,
        epoch bigint NOT NULL,
        operation_id uuid NOT NULL,
        revoked_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_credential_revocations" PRIMARY KEY (environment_id, credential_id),
        CONSTRAINT "FK_credential_revocations_application_credentials_credential_i~" FOREIGN KEY (credential_id, application_id, organization_id) REFERENCES application_credentials (id, application_id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_credential_revocations_environments_environment_id_project_~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_credential_revocations_projects_project_id_organization_id" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_credential_revocations_security_events_environment_id_secur~" FOREIGN KEY (environment_id, security_generation, epoch) REFERENCES security_events (environment_id, security_generation, epoch) ON DELETE RESTRICT,
        CONSTRAINT "FK_credential_revocations_security_operations_operation_id_org~" FOREIGN KEY (operation_id, organization_id) REFERENCES security_operations (id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE security_operation_alerts (
        operation_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        threshold_seconds integer NOT NULL,
        severity character varying(16) NOT NULL,
        operation_status character varying(32) NOT NULL,
        pending_targets integer NOT NULL,
        observed_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_security_operation_alerts" PRIMARY KEY (operation_id, environment_id, threshold_seconds),
        CONSTRAINT ck_security_operation_alert CHECK (threshold_seconds BETWEEN 1 AND 86400 AND pending_targets>=0 AND severity IN ('Warning','Escalation') AND operation_status IN ('Unconfirmed','Partial')),
        CONSTRAINT "FK_security_operation_alerts_security_events_operation_id_envi~" FOREIGN KEY (operation_id, environment_id) REFERENCES security_events (operation_id, environment_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE security_operation_targets (
        operation_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        node_id uuid NOT NULL,
        instance_guid uuid NOT NULL,
        challenge_nonce uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        target_state character varying(32) NOT NULL,
        protocol_supported_at_freeze boolean NOT NULL,
        superseded_by_instance_guid uuid,
        last_lease_until timestamp with time zone,
        CONSTRAINT "PK_security_operation_targets" PRIMARY KEY (operation_id, environment_id, node_id, instance_guid),
        CONSTRAINT ck_security_target CHECK (target_state IN ('Pending','Applied','SafeSuperseded','AwaitingRegistration') AND ((instance_guid = '00000000-0000-0000-0000-000000000000'::uuid AND target_state IN ('AwaitingRegistration','SafeSuperseded')) OR (instance_guid<>'00000000-0000-0000-0000-000000000000'::uuid AND target_state<>'AwaitingRegistration'))),
        CONSTRAINT "FK_security_operation_targets_gateway_nodes_node_id_environmen~" FOREIGN KEY (node_id, environment_id) REFERENCES gateway_nodes (id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_operation_targets_security_events_operation_id_env~" FOREIGN KEY (operation_id, environment_id) REFERENCES security_events (operation_id, environment_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_operation_targets_security_operation_targets_opera~" FOREIGN KEY (operation_id, environment_id, node_id, superseded_by_instance_guid) REFERENCES security_operation_targets (operation_id, environment_id, node_id, instance_guid) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE security_outbox_messages (
        id uuid NOT NULL,
        environment_id uuid NOT NULL,
        security_generation uuid NOT NULL,
        epoch bigint NOT NULL,
        operation_id uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        processed_at timestamp with time zone,
        lease_until timestamp with time zone,
        attempts integer NOT NULL,
        CONSTRAINT "PK_security_outbox_messages" PRIMARY KEY (id),
        CONSTRAINT ck_security_outbox CHECK (epoch > 0 AND attempts >= 0),
        CONSTRAINT "FK_security_outbox_messages_security_events_environment_id_sec~" FOREIGN KEY (environment_id, security_generation, epoch) REFERENCES security_events (environment_id, security_generation, epoch) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_outbox_messages_security_operations_operation_id" FOREIGN KEY (operation_id) REFERENCES security_operations (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE security_ack_attempts (
        id uuid NOT NULL,
        operation_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        node_id uuid NOT NULL,
        instance_guid uuid NOT NULL,
        challenge_nonce uuid NOT NULL,
        request_hash character varying(64) NOT NULL,
        error_code character varying(64) NOT NULL,
        received_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_security_ack_attempts" PRIMARY KEY (id),
        CONSTRAINT ck_security_ack_attempt CHECK (request_hash ~ '^[a-f0-9]{64}$' AND error_code IN ('security_lkg_write_denied','security_material_invalid','security_generation_recovery_required','security_state_unavailable','security_full_checkpoint_required','security_epoch_conflict','security_tombstone_regression') AND challenge_nonce<>'00000000-0000-0000-0000-000000000000'::uuid),
        CONSTRAINT "FK_security_ack_attempts_security_operation_targets_operation_~" FOREIGN KEY (operation_id, environment_id, node_id, instance_guid) REFERENCES security_operation_targets (operation_id, environment_id, node_id, instance_guid) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE TABLE security_acks (
        operation_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        node_id uuid NOT NULL,
        instance_guid uuid NOT NULL,
        security_generation uuid NOT NULL,
        epoch bigint NOT NULL,
        checkpoint_hash character varying(64) NOT NULL,
        challenge_nonce uuid NOT NULL,
        applied_at timestamp with time zone NOT NULL,
        received_at timestamp with time zone NOT NULL,
        success boolean NOT NULL,
        durable boolean NOT NULL,
        admission_enforced boolean NOT NULL,
        error_code character varying(64),
        CONSTRAINT "PK_security_acks" PRIMARY KEY (operation_id, environment_id, node_id, instance_guid),
        CONSTRAINT ck_security_ack CHECK (epoch>0 AND checkpoint_hash ~ '^[a-f0-9]{64}$' AND success AND durable AND admission_enforced AND error_code IS NULL AND challenge_nonce<>'00000000-0000-0000-0000-000000000000'::uuid),
        CONSTRAINT "FK_security_acks_security_operation_targets_operation_id_envir~" FOREIGN KEY (operation_id, environment_id, node_id, instance_guid) REFERENCES security_operation_targets (operation_id, environment_id, node_id, instance_guid) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_credential_revocations_credential_id_application_id_organiz~" ON credential_revocations (credential_id, application_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_credential_revocations_environment_id_project_id" ON credential_revocations (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_credential_revocations_environment_id_security_generation_e~" ON credential_revocations (environment_id, security_generation, epoch);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_credential_revocations_operation_id_organization_id" ON credential_revocations (operation_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_credential_revocations_project_id_organization_id" ON credential_revocations (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_environment_security_states_environment_id_project_id" ON environment_security_states (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_environment_security_states_project_id_organization_id" ON environment_security_states (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_gateway_security_current_receipts_environment_id_project_id" ON gateway_security_current_receipts (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_gateway_security_current_receipts_environment_id_security_g~" ON gateway_security_current_receipts (environment_id, security_generation);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE UNIQUE INDEX "IX_gateway_security_current_receipts_lease_id" ON gateway_security_current_receipts (lease_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_gateway_security_current_receipts_node_id_environment_id" ON gateway_security_current_receipts (node_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_gateway_security_current_receipts_project_id_organization_id" ON gateway_security_current_receipts (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_gateway_security_leases_environment_id_fresh_until" ON gateway_security_leases (environment_id, fresh_until);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_gateway_security_leases_environment_id_project_id" ON gateway_security_leases (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_gateway_security_leases_environment_id_security_generation" ON gateway_security_leases (environment_id, security_generation);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE UNIQUE INDEX "IX_gateway_security_leases_lease_id" ON gateway_security_leases (lease_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_gateway_security_leases_node_id_environment_id" ON gateway_security_leases (node_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_gateway_security_leases_project_id_organization_id" ON gateway_security_leases (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE UNIQUE INDEX "IX_security_ack_attempts_operation_id_environment_id_node_id_i~" ON security_ack_attempts (operation_id, environment_id, node_id, instance_guid, request_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_events_credential_id" ON security_events (credential_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_events_environment_id_project_id" ON security_events (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_events_operation_id_organization_id" ON security_events (operation_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_events_project_id_organization_id" ON security_events (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_operation_alerts_observed_at" ON security_operation_alerts (observed_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE UNIQUE INDEX "IX_security_operation_targets_challenge_nonce" ON security_operation_targets (challenge_nonce);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_operation_targets_node_id_environment_id" ON security_operation_targets (node_id, environment_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_operation_targets_operation_id_environment_id_node~" ON security_operation_targets (operation_id, environment_id, node_id, superseded_by_instance_guid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_operations_application_id_organization_id" ON security_operations (application_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE UNIQUE INDEX "IX_security_operations_credential_id" ON security_operations (credential_id) WHERE kind = 'CredentialRevoke';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_operations_credential_id_application_id_organizati~" ON security_operations (credential_id, application_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_operations_organization_id" ON security_operations (organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_operations_requested_by" ON security_operations (requested_by);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE UNIQUE INDEX "IX_security_outbox_messages_environment_id_security_generation~" ON security_outbox_messages (environment_id, security_generation, epoch);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_outbox_messages_operation_id" ON security_outbox_messages (operation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE INDEX "IX_security_outbox_messages_processed_at_lease_until_created_at" ON security_outbox_messages (processed_at, lease_until, created_at) WHERE processed_at IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    CREATE FUNCTION security_facts_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  RAISE EXCEPTION 'security_fact_immutable';
END $$;
    CREATE TRIGGER security_event_immutable BEFORE UPDATE OR DELETE ON security_events FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
    CREATE TRIGGER credential_tombstone_immutable BEFORE UPDATE OR DELETE ON credential_revocations FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
    CREATE TRIGGER security_operation_immutable BEFORE UPDATE OR DELETE ON security_operations FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
    CREATE TRIGGER security_ack_immutable BEFORE UPDATE OR DELETE ON security_acks FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
    CREATE TRIGGER security_ack_attempt_immutable BEFORE UPDATE OR DELETE ON security_ack_attempts FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
    CREATE TRIGGER security_alert_immutable BEFORE UPDATE OR DELETE ON security_operation_alerts FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();

    CREATE FUNCTION security_initial_policy_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='INSERT' THEN
    IF NOT EXISTS (SELECT 1 FROM environment_security_initializations i
      WHERE i.environment_id=NEW.environment_id AND i.project_id=NEW.project_id AND i.organization_id=NEW.organization_id
        AND i.state IN ('FreshNonStrict','VerifiedLegacyNetwork','Governed'))
    THEN RAISE EXCEPTION 'security_environment_initialization_unverified'; END IF;
    IF NEW.epoch<>0 THEN RAISE EXCEPTION 'security_genesis_required'; END IF;
    IF NEW.initial_require_environment_credentials IS NOT NULL AND NEW.initial_require_environment_credentials<>NEW.require_environment_credentials
    THEN RAISE EXCEPTION 'security_genesis_mismatch'; END IF;
    NEW.initial_require_environment_credentials:=NEW.require_environment_credentials;
  ELSIF NEW.initial_require_environment_credentials IS DISTINCT FROM OLD.initial_require_environment_credentials THEN
    RAISE EXCEPTION 'security_initial_policy_immutable';
  END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER security_initial_policy_guard BEFORE INSERT OR UPDATE ON environment_security_states FOR EACH ROW EXECUTE FUNCTION security_initial_policy_guard();

    CREATE FUNCTION security_state_monotonic() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'security_state_delete_blocked'; END IF;
  IF NEW.environment_id<>OLD.environment_id OR NEW.organization_id<>OLD.organization_id OR NEW.project_id<>OLD.project_id
    OR NEW.security_generation<>OLD.security_generation OR NEW.epoch<OLD.epoch
    OR (NEW.epoch=OLD.epoch AND NEW.head_hash<>OLD.head_hash)
    OR NEW.revision<=OLD.revision
    OR (OLD.require_environment_credentials AND NOT NEW.require_environment_credentials)
    OR array_position(ARRAY['2.0','2.1','2.2','2.3','2.4'],NEW.minimum_snapshot_schema)
      < array_position(ARRAY['2.0','2.1','2.2','2.3','2.4'],OLD.minimum_snapshot_schema)
    OR ((NEW.require_environment_credentials,NEW.minimum_snapshot_schema,NEW.lease_seconds,NEW.lease_refresh_seconds,NEW.maximum_clock_skew_seconds)
      IS DISTINCT FROM (OLD.require_environment_credentials,OLD.minimum_snapshot_schema,OLD.lease_seconds,OLD.lease_refresh_seconds,OLD.maximum_clock_skew_seconds)
      AND NEW.epoch<=OLD.epoch)
  THEN RAISE EXCEPTION 'security_state_regression'; END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER security_state_guard BEFORE UPDATE OR DELETE ON environment_security_states FOR EACH ROW EXECUTE FUNCTION security_state_monotonic();

    CREATE FUNCTION credential_working_revocation_terminal() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF OLD.status='Revoked' AND (NEW.status<>'Revoked' OR NEW.revoked_at IS DISTINCT FROM OLD.revoked_at)
  THEN RAISE EXCEPTION 'credential_revocation_terminal'; END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER credential_working_revocation_terminal BEFORE UPDATE ON application_credentials FOR EACH ROW EXECUTE FUNCTION credential_working_revocation_terminal();

    CREATE FUNCTION security_target_frozen() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'security_target_delete_blocked'; END IF;
  IF NEW.operation_id<>OLD.operation_id OR NEW.environment_id<>OLD.environment_id OR NEW.node_id<>OLD.node_id
    OR NEW.instance_guid<>OLD.instance_guid OR NEW.challenge_nonce<>OLD.challenge_nonce OR NEW.created_at<>OLD.created_at
    OR NEW.protocol_supported_at_freeze<>OLD.protocol_supported_at_freeze THEN RAISE EXCEPTION 'security_target_identity_immutable'; END IF;
  IF OLD.last_lease_until IS NOT NULL AND (NEW.last_lease_until IS NULL OR NEW.last_lease_until<OLD.last_lease_until)
  THEN RAISE EXCEPTION 'security_target_lease_regression'; END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER security_target_frozen BEFORE UPDATE OR DELETE ON security_operation_targets FOR EACH ROW EXECUTE FUNCTION security_target_frozen();

    CREATE FUNCTION credential_tombstone_matches_event() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF NOT EXISTS(SELECT 1 FROM security_events e WHERE e.environment_id=NEW.environment_id AND e.security_generation=NEW.security_generation
    AND e.epoch=NEW.epoch AND e.operation_id=NEW.operation_id AND e.credential_id=NEW.credential_id
    AND e.organization_id=NEW.organization_id AND e.project_id=NEW.project_id AND e.occurred_at=NEW.revoked_at AND e.event_type='CredentialRevoked')
  THEN RAISE EXCEPTION 'credential_tombstone_event_mismatch'; END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER credential_tombstone_event_guard BEFORE INSERT ON credential_revocations FOR EACH ROW EXECUTE FUNCTION credential_tombstone_matches_event();

    CREATE FUNCTION gateway_security_lease_monotonic() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'security_lease_history_delete_blocked'; END IF;
  IF NEW.node_id<>OLD.node_id OR NEW.instance_id<>OLD.instance_id OR NEW.environment_id<>OLD.environment_id
    OR NEW.project_id<>OLD.project_id OR NEW.organization_id<>OLD.organization_id OR NEW.security_generation<>OLD.security_generation
    OR NEW.epoch<OLD.epoch OR NEW.issued_at<OLD.issued_at OR NEW.last_issued_lease_until<OLD.last_issued_lease_until
    OR NEW.last_issued_lease_until<NEW.fresh_until OR NEW.revision<=OLD.revision
  THEN RAISE EXCEPTION 'security_lease_history_regression'; END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER gateway_security_lease_monotonic BEFORE UPDATE OR DELETE ON gateway_security_leases FOR EACH ROW EXECUTE FUNCTION gateway_security_lease_monotonic();

    CREATE FUNCTION security_ack_matches_target() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF NOT EXISTS(SELECT 1 FROM security_operation_targets t WHERE t.operation_id=NEW.operation_id AND t.environment_id=NEW.environment_id
    AND t.node_id=NEW.node_id AND t.instance_guid=NEW.instance_guid AND t.challenge_nonce=NEW.challenge_nonce)
  THEN RAISE EXCEPTION 'security_ack_challenge_mismatch'; END IF;
  IF TG_TABLE_NAME='security_acks' THEN
    IF NOT EXISTS(SELECT 1 FROM security_events e JOIN environment_security_states s ON s.environment_id=e.environment_id
      WHERE e.operation_id=NEW.operation_id AND e.environment_id=NEW.environment_id AND e.security_generation=NEW.security_generation
        AND s.security_generation=NEW.security_generation AND e.epoch<=NEW.epoch AND NEW.epoch<=s.epoch)
    THEN RAISE EXCEPTION 'security_ack_epoch_mismatch'; END IF;
  END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER security_ack_matches_target BEFORE INSERT ON security_acks FOR EACH ROW EXECUTE FUNCTION security_ack_matches_target();
    CREATE TRIGGER security_ack_attempt_matches_target BEFORE INSERT ON security_ack_attempts FOR EACH ROW EXECUTE FUNCTION security_ack_matches_target();

    CREATE FUNCTION current_security_receipt_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'accepted_security_proof_immutable' USING ERRCODE='23514'; END IF;
  IF TG_OP='UPDATE' THEN
    IF ROW(NEW.node_id,NEW.instance_id,NEW.environment_id,NEW.project_id,NEW.organization_id,NEW.security_generation)
      IS DISTINCT FROM ROW(OLD.node_id,OLD.instance_id,OLD.environment_id,OLD.project_id,OLD.organization_id,OLD.security_generation)
      OR NEW.epoch<OLD.epoch OR NEW.received_at<OLD.received_at
      OR NEW.epoch=OLD.epoch AND (NEW.head_hash<>OLD.head_hash OR NEW.fresh_until<OLD.fresh_until
        OR NEW.fresh_until=OLD.fresh_until AND ROW(NEW.checkpoint_hash,NEW.lease_id) IS DISTINCT FROM ROW(OLD.checkpoint_hash,OLD.lease_id))
    THEN RAISE EXCEPTION 'accepted_security_proof_regression' USING ERRCODE='23514'; END IF;
  END IF;
  IF NOT EXISTS(SELECT 1 FROM gateway_security_leases l JOIN environment_security_states e ON e.environment_id=l.environment_id
    WHERE l.node_id=NEW.node_id AND l.instance_id=NEW.instance_id AND l.environment_id=NEW.environment_id AND l.project_id=NEW.project_id
      AND l.organization_id=NEW.organization_id AND l.security_generation=NEW.security_generation AND e.security_generation=NEW.security_generation
      AND e.epoch=NEW.epoch AND e.head_hash=NEW.head_hash AND l.epoch>=NEW.epoch AND l.last_issued_lease_until>=NEW.fresh_until
      AND NEW.received_at>=l.issued_at-interval '2 seconds')
  THEN RAISE EXCEPTION 'accepted_security_proof_scope_unproven' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER current_security_receipt_authority BEFORE INSERT OR UPDATE OR DELETE ON gateway_security_current_receipts
      FOR EACH ROW EXECUTE FUNCTION current_security_receipt_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103185741_Governance24SecurityLedger') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261103185741_Governance24SecurityLedger', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 25

-- Integrated environment trust and ledger binding.
-- WEBAPI_MIGRATION_START 26

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103190424_Governance24TrustLedgerBinding') THEN
    DO $$ BEGIN
      IF EXISTS(SELECT 1 FROM environment_security_states) OR EXISTS(SELECT 1 FROM security_events)
        OR EXISTS(SELECT 1 FROM security_operations) OR EXISTS(SELECT 1 FROM credential_revocations)
      THEN RAISE EXCEPTION 'trust_binding_requires_offline_review'; END IF;
    END $$;

    CREATE OR REPLACE FUNCTION environment_initialization_transition_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='DELETE' THEN
    RAISE EXCEPTION 'environment_initialization_deletion_blocked' USING ERRCODE='23514';
  END IF;
  IF (NEW.environment_id,NEW.project_id,NEW.organization_id) IS DISTINCT FROM
     (OLD.environment_id,OLD.project_id,OLD.organization_id) OR NEW.revision<>OLD.revision+1 THEN
    RAISE EXCEPTION 'environment_initialization_identity_or_revision_invalid' USING ERRCODE='23514';
  END IF;
  IF (OLD.state='Unknown' AND NEW.state IN ('FreshNonStrict','Governed')) OR
     (OLD.state='Governed' AND NEW.state<>'Governed') OR
     (OLD.state IN ('FreshNonStrict','VerifiedLegacyNetwork') AND NEW.state NOT IN (OLD.state,'Governed')) THEN
    RAISE EXCEPTION 'environment_initialization_regression' USING ERRCODE='23514';
  END IF;
  IF NEW.state='Governed' AND OLD.state<>'Governed' AND NOT EXISTS (
    SELECT 1 FROM environment_security_states s WHERE s.environment_id=NEW.environment_id
      AND s.project_id=NEW.project_id AND s.organization_id=NEW.organization_id)
  THEN RAISE EXCEPTION 'environment_initialization_ledger_missing' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;

    CREATE FUNCTION environment_initialization_insert_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF NEW.state NOT IN ('Unknown','FreshNonStrict') OR
    (NEW.state='FreshNonStrict' AND NEW.fresh_proof_id='00000000-0000-0000-0000-000000000000'::uuid)
  THEN RAISE EXCEPTION 'environment_initialization_requires_verified_creator' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
    CREATE TRIGGER environment_initialization_insert_check BEFORE INSERT ON environment_security_initializations
      FOR EACH ROW EXECUTE FUNCTION environment_initialization_insert_guard();

    CREATE OR REPLACE FUNCTION security_initial_policy_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='INSERT' THEN
    IF NOT EXISTS (SELECT 1 FROM environment_security_initializations i
      WHERE i.environment_id=NEW.environment_id AND i.project_id=NEW.project_id AND i.organization_id=NEW.organization_id
        AND i.state IN ('FreshNonStrict','VerifiedLegacyNetwork','Governed'))
    THEN RAISE EXCEPTION 'security_environment_initialization_unverified'; END IF;
    IF NEW.epoch<>0 THEN RAISE EXCEPTION 'security_genesis_required'; END IF;
    IF NEW.head_hash<>'4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
    THEN RAISE EXCEPTION 'security_genesis_hash_mismatch'; END IF;
    IF NEW.initial_require_environment_credentials IS NOT NULL AND NEW.initial_require_environment_credentials<>NEW.require_environment_credentials
    THEN RAISE EXCEPTION 'security_genesis_mismatch'; END IF;
    NEW.initial_require_environment_credentials:=NEW.require_environment_credentials;
  ELSIF NEW.initial_require_environment_credentials IS DISTINCT FROM OLD.initial_require_environment_credentials THEN
    RAISE EXCEPTION 'security_initial_policy_immutable';
  END IF;
  RETURN NEW;
END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103190424_Governance24TrustLedgerBinding') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261103190424_Governance24TrustLedgerBinding', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 26

-- Integrated migration source receipts.
-- WEBAPI_MIGRATION_START 27

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103191515_Governance24SourceReceipt') THEN
    DROP INDEX "IX_environment_security_initializations_environment_id_project~";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103191515_Governance24SourceReceipt') THEN
    ALTER TABLE environment_security_initializations ADD CONSTRAINT "AK_environment_security_initializations_environment_id_project~" UNIQUE (environment_id, project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103191515_Governance24SourceReceipt') THEN
    CREATE TABLE environment_security_source_receipts (
        environment_id uuid NOT NULL,
        project_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        source_database varchar(63) NOT NULL,
        source_database_oid bigint NOT NULL,
        source_cluster_id varchar(20) NOT NULL,
        source_migration varchar(150) NOT NULL,
        source_schema_hash varchar(64) NOT NULL,
        source_history_hash varchar(64) NOT NULL,
        captured_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_environment_security_source_receipts" PRIMARY KEY (environment_id),
        CONSTRAINT ck_environment_security_source_receipt CHECK (source_database_oid > 0 AND length(source_database) > 0 AND source_cluster_id ~ '^[0-9]{1,20}$'
    AND length(source_migration) > 0 AND source_schema_hash ~ '^[a-f0-9]{64}$' AND source_history_hash ~ '^[a-f0-9]{64}$'),
        CONSTRAINT "FK_environment_security_source_receipts_environment_security_i~" FOREIGN KEY (environment_id, project_id, organization_id) REFERENCES environment_security_initializations (environment_id, project_id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103191515_Governance24SourceReceipt') THEN
    CREATE INDEX "IX_environment_security_source_receipts_environment_id_project~" ON environment_security_source_receipts (environment_id, project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103191515_Governance24SourceReceipt') THEN
    CREATE FUNCTION environment_security_source_receipt_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  RAISE EXCEPTION 'environment_source_receipt_immutable' USING ERRCODE='23514';
END $$;
    CREATE TRIGGER environment_security_source_receipt_immutable
      BEFORE UPDATE OR DELETE ON environment_security_source_receipts
      FOR EACH ROW EXECUTE FUNCTION environment_security_source_receipt_immutable();

    DO $$ DECLARE
      proof_text text := current_setting('webapi.gov24_source',true);
      proof jsonb;
      scope_table text;
      cluster_id text;
      database_oid bigint;
      history_hash text;
      scope_mismatch boolean;
    BEGIN
      IF COALESCE(proof_text,'')='' THEN RETURN; END IF;
      proof := proof_text::jsonb;
      scope_table := proof->>'table';
      IF scope_table !~ '^webapi_gov24_[0-9a-f]{32}$' OR to_regclass('pg_temp.'||quote_ident(scope_table)) IS NULL
      THEN RAISE EXCEPTION 'environment_source_receipt_invalid_scope'; END IF;
      SELECT oid::bigint INTO database_oid FROM pg_database WHERE datname=current_database();
      SELECT system_identifier::text INTO cluster_id FROM pg_control_system();
      IF proof->>'database' IS DISTINCT FROM current_database() OR
         (proof->>'databaseOid')::bigint IS DISTINCT FROM database_oid OR proof->>'clusterId' IS DISTINCT FROM cluster_id
      THEN RAISE EXCEPTION 'environment_source_receipt_database_changed'; END IF;
      IF NOT EXISTS(SELECT 1 FROM (VALUES
          ('20261003182012_InitialCore','fe52425b83f9af0a549474b72f66d9fa289e21439476cad5c55b3915d186abf7'),
          ('20261003201632_PublishCoordinatorIdentity','a617755e26fdb12067dd6841567a630bb270de8c702abd4491c5b94ad7138983'),
          ('20261003213457_RecoveryReference','0212ebfc95f13892e5cf31ef8046e57de0db4e5a53a7ae0ff46f791303a2ca51'),
          ('20261004030000_ObservabilityAlerts','8f1db7ab46bdf8aee98b7eaa12d94e95d1d155ed08a0079654222955c44b697c'),
          ('20261005090000_SystemSettings','9f26f635180641eecd18c74004f036dade54fab0185115c43f9faf40e6647239'),
          ('20261005120000_SsoOidc','0de24a53b793610521b20a81f1a118162a4c8f3888fe616532c99e8cfe18c95b'),
          ('20261005123210_SsoDisplayEmail','2c7aa41de00dd3532a80d6e6d263d0b6bcd7afc27132acdd62a6773feb065949'),
          ('20261005152401_SsoSecretVersion','8837d28545ac4debb271bc6db26adab8ccf53bae232ec664d3b646ffa62d9169'),
          ('20261006090000_ApiVersionComparisons','2d6ca65b43e2a2528403e11839caca9b6541d7abd2c0267d05fc513c67628a21'),
          ('20261006165518_ContractManagement','a5c22b728870446573cc809e9922c97ba01120c432accb86a66d3272131b49a0'),
          ('20261008020000_ApprovalInboxIndexes','c940a2a82b167200c6fd7de8ca4c31b5b945993611dc114527ee22116d143370'),
          ('20261008030000_ExternalNotifications','0a44412db53e587ee76a2fd50bb96ae5847e9cce4e02736eee2b95739ecd33dc'),
          ('20261009010000_EnvironmentAccessAddresses','9bec84df3efb17dd12ec6a1ec3aa38174c6e3c7c85b0ad323da1d66967dd323f'),
          ('20261009011000_ReleaseAccessContexts','8b60204041d0b5b56c156efb6717cda984a47b9cc3043788976c53eadb79f63f'),
          ('20261009020000_ReleaseDelivery','576c4931ea93b3e04946d96180c3e8772ac61d31d01a1ce24d1a6b57fb442a4a'),
          ('20261009030000_ReleasePipelines','fea6abd90bf547e102d1e0393116778d4386645823ac9d54debec7ee3e9678c6'),
          ('20261010010000_PipelineProjectionCursor','0ee9b8dfc47768d2ed7a4ffc38f994aa096847358618bd6e99bbe74e3b063af2'),
          ('20261010091247_GatewayQueryIndexes','c5f0183ba93eeff1531997c7db642e7d3fc4287c072fc3a8240fea08fb9cb864'),
          ('20261012004548_ReleaseFrozenContracts','daf385188ac734468e7b6d0c16dde4600175a0c45a8ab66168c8cadc657ea04f'),
          ('20261015010000_AccountPasswordLifecycle','6987551e4582450323242cc56ac4a461c52a6c8d2f15c7804cd7600784a90714'),
          ('20261015020000_CredentialUsageTracking','b9676942e2e462605b1f00d56dd5e27dcf2910c8b9a78c812383a65079a31560')
        ) AS expected(migration_id,schema_hash)
        WHERE expected.migration_id=proof->>'migration' AND expected.schema_hash=proof->>'schemaHash')
      THEN RAISE EXCEPTION 'environment_source_receipt_unknown_mainline'; END IF;
      SELECT encode(sha256(convert_to(COALESCE(json_agg(json_build_array("MigrationId","ProductVersion") ORDER BY "MigrationId")::text,'[]'),'UTF8')),'hex')
        INTO history_hash FROM public."__EFMigrationsHistory" WHERE "MigrationId"<=proof->>'migration';
      IF proof->>'historyHash' IS DISTINCT FROM history_hash OR (proof->>'capturedAt') IS NULL
      THEN RAISE EXCEPTION 'environment_source_receipt_history_changed'; END IF;
      EXECUTE format('SELECT EXISTS (SELECT 1 FROM pg_temp.%I s LEFT JOIN environment_security_initializations i ON i.environment_id=s.environment_id AND i.project_id=s.project_id AND i.organization_id=s.organization_id WHERE i.environment_id IS NULL)',scope_table)
        INTO scope_mismatch;
      IF scope_mismatch THEN RAISE EXCEPTION 'environment_source_receipt_scope_changed'; END IF;
      EXECUTE format('INSERT INTO environment_security_source_receipts(environment_id,project_id,organization_id,source_database,source_database_oid,source_cluster_id,source_migration,source_schema_hash,source_history_hash,captured_at) SELECT s.environment_id,s.project_id,s.organization_id,$1,$2,$3,$4,$5,$6,$7 FROM pg_temp.%I s JOIN environment_security_initializations i ON i.environment_id=s.environment_id AND i.project_id=s.project_id AND i.organization_id=s.organization_id',scope_table)
        USING proof->>'database',database_oid,cluster_id,proof->>'migration',proof->>'schemaHash',history_hash,(proof->>'capturedAt')::timestamptz;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261103191515_Governance24SourceReceipt') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261103191515_Governance24SourceReceipt', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 27

-- WEBAPI_MIGRATION_START 28

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104010000_Governance24LegacyPermissionMove') THEN
CREATE OR REPLACE FUNCTION permission_parent_guard() RETURNS trigger LANGUAGE plpgsql AS $$ DECLARE actualproject uuid; actualorg uuid; BEGIN
  NEW.revision_transaction_id=txid_current();
  SELECT e.project_id,p.organization_id INTO actualproject,actualorg FROM environments e JOIN projects p ON p.id=e.project_id WHERE e.id=NEW.environment_id;
  IF TG_OP='INSERT' THEN
    IF NEW.project_id='00000000-0000-0000-0000-000000000000'::uuid THEN NEW.project_id=actualproject; END IF;
    IF NEW.organization_id='00000000-0000-0000-0000-000000000000'::uuid THEN NEW.organization_id=actualorg; END IF;
  ELSE
    IF NEW.application_id IS DISTINCT FROM OLD.application_id THEN RAISE EXCEPTION 'permission_scope_immutable' USING ERRCODE='23514'; END IF;
    IF (NEW.environment_id,NEW.api_id) IS DISTINCT FROM (OLD.environment_id,OLD.api_id) THEN
      IF OLD.mode<>'LegacyApi' OR NEW.mode<>'LegacyApi' OR EXISTS(SELECT 1 FROM application_route_method_grants g WHERE g.permission_id=OLD.id)
      THEN RAISE EXCEPTION 'permission_scope_immutable' USING ERRCODE='23514'; END IF;
      NEW.project_id=actualproject; NEW.organization_id=actualorg;
    ELSIF (NEW.project_id,NEW.organization_id) IS DISTINCT FROM (OLD.project_id,OLD.organization_id) THEN
      RAISE EXCEPTION 'permission_scope_immutable' USING ERRCODE='23514';
    END IF;
    NEW.revision=GREATEST(NEW.revision,OLD.revision+1);
  END IF;
  IF (NEW.project_id,NEW.organization_id) IS DISTINCT FROM (actualproject,actualorg) THEN RAISE EXCEPTION 'permission_scope_mismatch' USING ERRCODE='23514'; END IF;
  IF NOT EXISTS(SELECT 1 FROM applications a JOIN apis api ON api.id=NEW.api_id JOIN environments e ON e.id=NEW.environment_id
      JOIN projects p ON p.id=e.project_id WHERE a.id=NEW.application_id AND a.organization_id=api.organization_id
      AND p.organization_id=api.organization_id AND api.project_id=e.project_id AND (a.project_id IS NULL OR a.project_id=api.project_id))
  THEN RAISE EXCEPTION 'permission_scope_mismatch' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104010000_Governance24LegacyPermissionMove') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104010000_Governance24LegacyPermissionMove', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 28

-- WEBAPI_MIGRATION_START 29

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104020000_Governance24RegistrationHistory') THEN
CREATE FUNCTION gateway_registration_history_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF TG_OP='TRUNCATE' THEN RAISE EXCEPTION 'gateway_registration_history_immutable' USING ERRCODE='23514'; END IF;
  IF OLD.event_type='registered' THEN RAISE EXCEPTION 'gateway_registration_history_immutable' USING ERRCODE='23514'; END IF;
  IF TG_OP='UPDATE' AND NEW.event_type='registered' THEN RAISE EXCEPTION 'gateway_registration_history_immutable' USING ERRCODE='23514'; END IF;
  IF TG_OP='DELETE' THEN RETURN OLD; END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER gateway_registration_history_immutable BEFORE UPDATE OR DELETE ON gateway_node_events
  FOR EACH ROW EXECUTE FUNCTION gateway_registration_history_immutable();
CREATE TRIGGER gateway_registration_history_no_truncate BEFORE TRUNCATE ON gateway_node_events
  FOR EACH STATEMENT EXECUTE FUNCTION gateway_registration_history_immutable();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104020000_Governance24RegistrationHistory') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104020000_Governance24RegistrationHistory', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 29

-- WEBAPI_MIGRATION_START 30

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104030000_Governance24LeaseIssuance') THEN
    CREATE TABLE gateway_security_lease_issuances (
        lease_id uuid NOT NULL,
        node_id uuid NOT NULL,
        instance_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        organization_id uuid NOT NULL,
        project_id uuid NOT NULL,
        security_generation uuid NOT NULL,
        epoch bigint NOT NULL,
        checkpoint_hash character varying(64) NOT NULL,
        payload_hash character varying(64) NOT NULL,
        issued_at timestamp with time zone NOT NULL,
        fresh_until timestamp with time zone NOT NULL,
        signing_key_id character varying(64) NOT NULL,
        CONSTRAINT "PK_gateway_security_lease_issuances" PRIMARY KEY (lease_id),
        CONSTRAINT ck_security_lease_issuance CHECK (lease_id<>'00000000-0000-0000-0000-000000000000'::uuid AND instance_id<>'00000000-0000-0000-0000-000000000000'::uuid AND epoch>=0 AND fresh_until>issued_at AND fresh_until<=issued_at+interval '30 seconds' AND checkpoint_hash ~ '^[a-f0-9]{64}$' AND payload_hash ~ '^[a-f0-9]{64}$'),
        CONSTRAINT "FK_gateway_security_lease_issuances_environment_security_state~" FOREIGN KEY (environment_id, security_generation) REFERENCES environment_security_states (environment_id, security_generation) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_lease_issuances_environments_environment_i~" FOREIGN KEY (environment_id, project_id) REFERENCES environments (id, project_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_lease_issuances_gateway_security_leases_no~" FOREIGN KEY (node_id, instance_id) REFERENCES gateway_security_leases (node_id, instance_id) ON DELETE RESTRICT,
        CONSTRAINT "FK_gateway_security_lease_issuances_projects_project_id_organi~" FOREIGN KEY (project_id, organization_id) REFERENCES projects (id, organization_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104030000_Governance24LeaseIssuance') THEN
    CREATE INDEX "IX_gateway_security_lease_issuances_environment_id_project_id" ON gateway_security_lease_issuances (environment_id, project_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104030000_Governance24LeaseIssuance') THEN
    CREATE INDEX "IX_gateway_security_lease_issuances_environment_id_security_ge~" ON gateway_security_lease_issuances (environment_id, security_generation);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104030000_Governance24LeaseIssuance') THEN
    CREATE INDEX "IX_gateway_security_lease_issuances_node_id_instance_id" ON gateway_security_lease_issuances (node_id, instance_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104030000_Governance24LeaseIssuance') THEN
    CREATE INDEX "IX_gateway_security_lease_issuances_project_id_organization_id" ON gateway_security_lease_issuances (project_id, organization_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104030000_Governance24LeaseIssuance') THEN
CREATE FUNCTION gateway_lease_issuance_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  RAISE EXCEPTION 'gateway_lease_issuance_immutable' USING ERRCODE='23514';
END $$;
CREATE TRIGGER gateway_lease_issuance_immutable BEFORE UPDATE OR DELETE ON gateway_security_lease_issuances
  FOR EACH ROW EXECUTE FUNCTION gateway_lease_issuance_immutable();
CREATE TRIGGER gateway_lease_issuance_no_truncate BEFORE TRUNCATE ON gateway_security_lease_issuances
  FOR EACH STATEMENT EXECUTE FUNCTION gateway_lease_issuance_immutable();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104030000_Governance24LeaseIssuance') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104030000_Governance24LeaseIssuance', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 30

-- WEBAPI_MIGRATION_START 31

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    ALTER TABLE gateway_security_lease_issuances DROP CONSTRAINT "FK_gateway_security_lease_issuances_gateway_security_leases_no~";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    DROP INDEX "IX_gateway_security_lease_issuances_node_id_instance_id";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    ALTER TABLE security_operation_targets ADD CONSTRAINT "AK_security_operation_targets_operation_id_environment_id_node~" UNIQUE (operation_id, environment_id, node_id, instance_guid, challenge_nonce);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    ALTER TABLE gateway_security_leases ADD CONSTRAINT "AK_gateway_security_leases_node_id_instance_id_environment_id_~" UNIQUE (node_id, instance_id, environment_id, project_id, organization_id, security_generation);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    ALTER TABLE gateway_security_lease_issuances ADD CONSTRAINT "AK_gateway_security_lease_issuances_lease_id_node_id_instance_~" UNIQUE (lease_id, node_id, instance_id, environment_id, security_generation, epoch, checkpoint_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    CREATE TABLE security_ack_verifications (
        operation_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        node_id uuid NOT NULL,
        instance_guid uuid NOT NULL,
        challenge_nonce uuid NOT NULL,
        security_generation uuid NOT NULL,
        epoch bigint NOT NULL,
        checkpoint_hash character varying(64) NOT NULL,
        lease_id uuid NOT NULL,
        applied_at timestamp with time zone NOT NULL,
        received_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_security_ack_verifications" PRIMARY KEY (operation_id, environment_id, node_id, instance_guid),
        CONSTRAINT ck_security_ack_verification CHECK (epoch>0 AND checkpoint_hash ~ '^[a-f0-9]{64}$' AND challenge_nonce<>'00000000-0000-0000-0000-000000000000'::uuid AND lease_id<>'00000000-0000-0000-0000-000000000000'::uuid),
        CONSTRAINT "FK_security_ack_verifications_gateway_security_lease_issuances~" FOREIGN KEY (lease_id, node_id, instance_guid, environment_id, security_generation, epoch, checkpoint_hash) REFERENCES gateway_security_lease_issuances (lease_id, node_id, instance_id, environment_id, security_generation, epoch, checkpoint_hash) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_ack_verifications_security_acks_operation_id_envir~" FOREIGN KEY (operation_id, environment_id, node_id, instance_guid) REFERENCES security_acks (operation_id, environment_id, node_id, instance_guid) ON DELETE RESTRICT,
        CONSTRAINT "FK_security_ack_verifications_security_operation_targets_opera~" FOREIGN KEY (operation_id, environment_id, node_id, instance_guid, challenge_nonce) REFERENCES security_operation_targets (operation_id, environment_id, node_id, instance_guid, challenge_nonce) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    CREATE INDEX "IX_gateway_security_lease_issuances_node_id_instance_id_enviro~" ON gateway_security_lease_issuances (node_id, instance_id, environment_id, project_id, organization_id, security_generation);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    CREATE INDEX "IX_security_ack_verifications_lease_id_node_id_instance_guid_e~" ON security_ack_verifications (lease_id, node_id, instance_guid, environment_id, security_generation, epoch, checkpoint_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    CREATE INDEX "IX_security_ack_verifications_operation_id_environment_id_node~" ON security_ack_verifications (operation_id, environment_id, node_id, instance_guid, challenge_nonce);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    ALTER TABLE gateway_security_lease_issuances ADD CONSTRAINT "FK_gateway_security_lease_issuances_gateway_security_leases_no~" FOREIGN KEY (node_id, instance_id, environment_id, project_id, organization_id, security_generation) REFERENCES gateway_security_leases (node_id, instance_id, environment_id, project_id, organization_id, security_generation) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
CREATE FUNCTION security_ack_verification_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF NOT EXISTS(SELECT 1 FROM gateway_security_lease_issuances i WHERE i.lease_id=NEW.lease_id
    AND NEW.applied_at>=i.issued_at-interval '2 seconds' AND NEW.applied_at<i.fresh_until
    AND NEW.received_at>=NEW.applied_at-interval '2 seconds')
  THEN RAISE EXCEPTION 'security_ack_verification_time_invalid' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER security_ack_verification_guard BEFORE INSERT ON security_ack_verifications
  FOR EACH ROW EXECUTE FUNCTION security_ack_verification_guard();
CREATE FUNCTION security_ack_verification_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  RAISE EXCEPTION 'security_ack_verification_immutable' USING ERRCODE='23514';
END $$;
CREATE TRIGGER security_ack_verification_immutable BEFORE UPDATE OR DELETE ON security_ack_verifications
  FOR EACH ROW EXECUTE FUNCTION security_ack_verification_immutable();
CREATE TRIGGER security_ack_verification_no_truncate BEFORE TRUNCATE ON security_ack_verifications
  FOR EACH STATEMENT EXECUTE FUNCTION security_ack_verification_immutable();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104040000_Governance24AckVerification') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104040000_Governance24AckVerification', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 31

-- WEBAPI_MIGRATION_START 32

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104050000_Governance24AckRevalidation') THEN
    DO $$ BEGIN IF EXISTS(SELECT 1 FROM security_ack_verifications) THEN RAISE EXCEPTION 'security_ack_verification_origin_unverified' USING ERRCODE='23514'; END IF; END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104050000_Governance24AckRevalidation') THEN
    ALTER TABLE security_ack_verifications ADD revalidation_nonce uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104050000_Governance24AckRevalidation') THEN
    CREATE TABLE security_ack_revalidation_challenges (
        nonce uuid NOT NULL,
        operation_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        node_id uuid NOT NULL,
        instance_guid uuid NOT NULL,
        created_at timestamp with time zone NOT NULL,
        expires_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_security_ack_revalidation_challenges" PRIMARY KEY (nonce),
        CONSTRAINT ck_security_ack_revalidation_challenge CHECK (nonce<>'00000000-0000-0000-0000-000000000000'::uuid AND instance_guid<>'00000000-0000-0000-0000-000000000000'::uuid AND expires_at>created_at AND expires_at<=created_at+interval '30 seconds'),
        CONSTRAINT "FK_security_ack_revalidation_challenges_security_operation_tar~" FOREIGN KEY (operation_id, environment_id, node_id, instance_guid) REFERENCES security_operation_targets (operation_id, environment_id, node_id, instance_guid) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104050000_Governance24AckRevalidation') THEN
    CREATE INDEX "IX_security_ack_verifications_revalidation_nonce" ON security_ack_verifications (revalidation_nonce);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104050000_Governance24AckRevalidation') THEN
    CREATE INDEX "IX_security_ack_revalidation_challenges_operation_id_environme~" ON security_ack_revalidation_challenges (operation_id, environment_id, node_id, instance_guid, expires_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104050000_Governance24AckRevalidation') THEN
    ALTER TABLE security_ack_verifications ADD CONSTRAINT "FK_security_ack_verifications_security_ack_revalidation_challe~" FOREIGN KEY (revalidation_nonce) REFERENCES security_ack_revalidation_challenges (nonce) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104050000_Governance24AckRevalidation') THEN
CREATE OR REPLACE FUNCTION security_ack_verification_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF NOT EXISTS(SELECT 1 FROM gateway_security_lease_issuances i WHERE i.lease_id=NEW.lease_id
    AND NEW.applied_at>=i.issued_at-interval '2 seconds' AND NEW.applied_at<i.fresh_until
    AND NEW.received_at>=i.issued_at AND NEW.received_at>=NEW.applied_at-interval '2 seconds')
  THEN RAISE EXCEPTION 'security_ack_verification_time_invalid' USING ERRCODE='23514'; END IF;
  IF NEW.revalidation_nonce IS NOT NULL AND NOT EXISTS(
    SELECT 1 FROM security_ack_revalidation_challenges c JOIN gateway_security_lease_issuances i ON i.lease_id=NEW.lease_id
    WHERE c.nonce=NEW.revalidation_nonce AND c.operation_id=NEW.operation_id AND c.environment_id=NEW.environment_id
      AND c.node_id=NEW.node_id AND c.instance_guid=NEW.instance_guid AND i.issued_at>=c.created_at
      AND NEW.applied_at>=c.created_at-interval '2 seconds' AND NEW.applied_at<c.expires_at+interval '2 seconds'
      AND NEW.received_at>=c.created_at AND NEW.received_at<c.expires_at)
  THEN RAISE EXCEPTION 'security_ack_revalidation_invalid' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
CREATE FUNCTION security_ack_revalidation_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  RAISE EXCEPTION 'security_ack_revalidation_immutable' USING ERRCODE='23514';
END $$;
CREATE TRIGGER security_ack_revalidation_immutable BEFORE UPDATE OR DELETE ON security_ack_revalidation_challenges
  FOR EACH ROW EXECUTE FUNCTION security_ack_revalidation_immutable();
CREATE TRIGGER security_ack_revalidation_no_truncate BEFORE TRUNCATE ON security_ack_revalidation_challenges
  FOR EACH STATEMENT EXECUTE FUNCTION security_ack_revalidation_immutable();
CREATE FUNCTION current_security_receipt_clock_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
  IF NOT EXISTS(SELECT 1 FROM gateway_security_leases l WHERE l.node_id=NEW.node_id AND l.instance_id=NEW.instance_id
    AND l.environment_id=NEW.environment_id AND NEW.received_at>=l.issued_at)
  THEN RAISE EXCEPTION 'security_receipt_clock_regression' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER current_security_receipt_clock_guard BEFORE INSERT OR UPDATE ON gateway_security_current_receipts
  FOR EACH ROW EXECUTE FUNCTION current_security_receipt_clock_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104050000_Governance24AckRevalidation') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104050000_Governance24AckRevalidation', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 32

-- WEBAPI_MIGRATION_START 33

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104060000_Governance24FailureObservation') THEN
    ALTER TABLE security_ack_attempts ADD observation_sequence bigint;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104060000_Governance24FailureObservation') THEN
    ALTER TABLE security_ack_attempts ADD reported_nonce uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104060000_Governance24FailureObservation') THEN
    CREATE TABLE security_ack_failure_latest (
        operation_id uuid NOT NULL,
        environment_id uuid NOT NULL,
        node_id uuid NOT NULL,
        instance_guid uuid NOT NULL,
        reported_nonce uuid NOT NULL,
        error_code character varying(64) NOT NULL,
        last_accepted_at timestamp with time zone NOT NULL,
        latest_sequence bigint NOT NULL,
        observation_count bigint NOT NULL,
        CONSTRAINT "PK_security_ack_failure_latest" PRIMARY KEY (operation_id, environment_id, node_id, instance_guid),
        CONSTRAINT ck_security_ack_failure_latest CHECK (reported_nonce<>'00000000-0000-0000-0000-000000000000'::uuid AND latest_sequence>0 AND observation_count>0 AND error_code IN ('security_lkg_write_denied','security_material_invalid','security_generation_recovery_required','security_state_unavailable','security_full_checkpoint_required','security_epoch_conflict','security_tombstone_regression')),
        CONSTRAINT "FK_security_ack_failure_latest_security_operation_targets_oper~" FOREIGN KEY (operation_id, environment_id, node_id, instance_guid) REFERENCES security_operation_targets (operation_id, environment_id, node_id, instance_guid) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104060000_Governance24FailureObservation') THEN
CREATE FUNCTION public.security_failure_nonce_valid(p_operation uuid,p_environment uuid,p_node uuid,p_instance uuid,p_nonce uuid,p_at timestamptz)
RETURNS boolean LANGUAGE sql VOLATILE SET search_path=pg_catalog,public AS $$
  SELECT EXISTS(SELECT 1 FROM public.security_operation_targets t WHERE t.operation_id=p_operation AND t.environment_id=p_environment
    AND t.node_id=p_node AND t.instance_guid=p_instance AND (t.challenge_nonce=p_nonce OR EXISTS(
      SELECT 1 FROM public.security_ack_revalidation_challenges c WHERE c.nonce=p_nonce AND c.operation_id=p_operation
        AND c.environment_id=p_environment AND c.node_id=p_node AND c.instance_guid=p_instance
        AND c.created_at<=p_at AND c.expires_at>p_at AND c.created_at<=pg_catalog.clock_timestamp()
        AND c.expires_at>pg_catalog.clock_timestamp())))
$$;
CREATE FUNCTION public.security_failure_attempt_nonce_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$ BEGIN
  IF NEW.reported_nonce IS NULL OR NEW.observation_sequence IS NULL OR NEW.observation_sequence<=0 OR NEW.received_at>pg_catalog.clock_timestamp()
    OR NOT public.security_failure_nonce_valid(NEW.operation_id,NEW.environment_id,NEW.node_id,NEW.instance_guid,NEW.reported_nonce,NEW.received_at)
  THEN RAISE EXCEPTION 'security_failure_nonce_invalid' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER security_failure_attempt_nonce_guard BEFORE INSERT ON public.security_ack_attempts
  FOR EACH ROW EXECUTE FUNCTION public.security_failure_attempt_nonce_guard();
CREATE FUNCTION public.security_failure_latest_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$ BEGIN
  IF TG_OP='DELETE' OR TG_OP='TRUNCATE' THEN RAISE EXCEPTION 'security_failure_latest_immutable_identity' USING ERRCODE='23514'; END IF;
  IF TG_OP='INSERT' AND (NEW.observation_count<>1 OR NEW.latest_sequence<>1) THEN RAISE EXCEPTION 'security_failure_transition_invalid' USING ERRCODE='23514'; END IF;
  IF TG_OP='UPDATE' AND (ROW(NEW.operation_id,NEW.environment_id,NEW.node_id,NEW.instance_guid) IS DISTINCT FROM ROW(OLD.operation_id,OLD.environment_id,OLD.node_id,OLD.instance_guid)
    OR NEW.last_accepted_at<OLD.last_accepted_at OR NEW.latest_sequence<=OLD.latest_sequence OR NEW.latest_sequence-OLD.latest_sequence<>1 OR NEW.observation_count<>OLD.observation_count+1)
  THEN RAISE EXCEPTION 'security_failure_transition_invalid' USING ERRCODE='23514'; END IF;
  IF NEW.last_accepted_at>pg_catalog.clock_timestamp() OR EXISTS(SELECT 1 FROM public.security_ack_attempts a WHERE a.operation_id=NEW.operation_id
    AND a.environment_id=NEW.environment_id AND a.node_id=NEW.node_id AND a.instance_guid=NEW.instance_guid AND a.received_at>NEW.last_accepted_at)
  THEN RAISE EXCEPTION 'security_failure_time_invalid' USING ERRCODE='23514'; END IF;
  IF EXISTS(SELECT 1 FROM public.security_ack_attempts a WHERE a.operation_id=NEW.operation_id
    AND a.environment_id=NEW.environment_id AND a.node_id=NEW.node_id AND a.instance_guid=NEW.instance_guid
    AND (a.observation_sequence>NEW.latest_sequence OR a.observation_sequence=NEW.latest_sequence
      AND (a.error_code IS DISTINCT FROM NEW.error_code OR a.reported_nonce IS DISTINCT FROM NEW.reported_nonce)))
  THEN RAISE EXCEPTION 'security_failure_sequence_invalid' USING ERRCODE='23514'; END IF;
  IF TG_OP='UPDATE' THEN
    IF EXISTS(SELECT 1 FROM public.security_ack_attempts a WHERE a.operation_id=OLD.operation_id
      AND a.environment_id=OLD.environment_id AND a.node_id=OLD.node_id AND a.instance_guid=OLD.instance_guid
      AND a.observation_sequence=OLD.latest_sequence
      AND (a.error_code IS DISTINCT FROM OLD.error_code OR a.reported_nonce IS DISTINCT FROM OLD.reported_nonce))
    THEN RAISE EXCEPTION 'security_failure_sequence_invalid' USING ERRCODE='23514'; END IF;
  END IF;
  IF NOT public.security_failure_nonce_valid(NEW.operation_id,NEW.environment_id,NEW.node_id,NEW.instance_guid,NEW.reported_nonce,NEW.last_accepted_at)
  THEN RAISE EXCEPTION 'security_failure_nonce_invalid' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER security_failure_latest_guard BEFORE INSERT OR UPDATE OR DELETE ON public.security_ack_failure_latest
  FOR EACH ROW EXECUTE FUNCTION public.security_failure_latest_guard();
CREATE TRIGGER security_failure_latest_no_truncate BEFORE TRUNCATE ON public.security_ack_failure_latest
  FOR EACH STATEMENT EXECUTE FUNCTION public.security_failure_latest_guard();
CREATE FUNCTION public.security_failure_attempt_no_truncate() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$ BEGIN
  RAISE EXCEPTION 'security_failure_attempt_immutable' USING ERRCODE='23514';
END $$;
CREATE TRIGGER security_failure_attempt_no_truncate BEFORE TRUNCATE ON public.security_ack_attempts
  FOR EACH STATEMENT EXECUTE FUNCTION public.security_failure_attempt_no_truncate();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104060000_Governance24FailureObservation') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104060000_Governance24FailureObservation', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 33

-- WEBAPI_MIGRATION_START 34

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104070000_Governance24LeaseExpiryObservation') THEN
    CREATE TABLE gateway_security_lease_expiry_observations (
        lease_id uuid NOT NULL,
        observed_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_gateway_security_lease_expiry_observations" PRIMARY KEY (lease_id),
        CONSTRAINT "FK_gateway_security_lease_expiry_observations_gateway_security~" FOREIGN KEY (lease_id) REFERENCES gateway_security_lease_issuances (lease_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104070000_Governance24LeaseExpiryObservation') THEN
    CREATE INDEX "IX_gateway_security_lease_expiry_observations_observed_at" ON gateway_security_lease_expiry_observations (observed_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104070000_Governance24LeaseExpiryObservation') THEN
    CREATE INDEX "IX_gateway_security_lease_issuances_issued_at" ON gateway_security_lease_issuances (issued_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104070000_Governance24LeaseExpiryObservation') THEN
CREATE FUNCTION public.security_lease_expiry_observation_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$ BEGIN
  IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'security_lease_expiry_observation_immutable' USING ERRCODE='23514'; END IF;
  NEW.observed_at:=pg_catalog.clock_timestamp();
  IF NOT EXISTS(SELECT 1 FROM public.gateway_security_lease_issuances i WHERE i.lease_id=NEW.lease_id AND NEW.observed_at>=i.fresh_until)
  THEN RAISE EXCEPTION 'security_lease_expiry_observation_invalid' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER security_lease_expiry_observation_guard BEFORE INSERT OR UPDATE OR DELETE ON public.gateway_security_lease_expiry_observations
  FOR EACH ROW EXECUTE FUNCTION public.security_lease_expiry_observation_guard();
CREATE TRIGGER security_lease_expiry_observation_no_truncate BEFORE TRUNCATE ON public.gateway_security_lease_expiry_observations
  FOR EACH STATEMENT EXECUTE FUNCTION public.security_lease_expiry_observation_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104070000_Governance24LeaseExpiryObservation') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104070000_Governance24LeaseExpiryObservation', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 34

-- WEBAPI_MIGRATION_START 35

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104080000_Governance24DatabaseClock') THEN
    CREATE TABLE security_database_clock_watermark (
        singleton boolean NOT NULL,
        observed_at timestamp with time zone NOT NULL,
        CONSTRAINT "PK_security_database_clock_watermark" PRIMARY KEY (singleton),
        CONSTRAINT ck_security_clock_singleton CHECK (singleton)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104080000_Governance24DatabaseClock') THEN
    CREATE INDEX "IX_gateway_security_current_receipts_received_at" ON gateway_security_current_receipts (received_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104080000_Governance24DatabaseClock') THEN
    CREATE INDEX "IX_security_ack_verifications_received_at" ON security_ack_verifications (received_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104080000_Governance24DatabaseClock') THEN
    CREATE INDEX "IX_security_ack_revalidation_challenges_created_at" ON security_ack_revalidation_challenges (created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104080000_Governance24DatabaseClock') THEN
INSERT INTO public.security_database_clock_watermark(singleton,observed_at)
SELECT true,GREATEST(pg_catalog.clock_timestamp(),
  COALESCE((SELECT MAX(issued_at) FROM public.gateway_security_lease_issuances),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(issued_at) FROM public.gateway_security_leases),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(received_at) FROM public.security_acks),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(received_at) FROM public.gateway_security_current_receipts),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(received_at) FROM public.security_ack_verifications),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(created_at) FROM public.security_ack_revalidation_challenges),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(observed_at) FROM public.gateway_security_lease_expiry_observations),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(last_accepted_at) FROM public.security_ack_failure_latest),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(received_at) FROM public.security_ack_attempts),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(committed_at) FROM public.security_operations),'-infinity'::timestamptz),
  COALESCE((SELECT MAX(occurred_at) FROM public.security_events),'-infinity'::timestamptz));
CREATE FUNCTION public.security_database_clock_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
DECLARE current_time_value timestamptz;
BEGIN
  IF TG_OP<>'UPDATE' THEN RAISE EXCEPTION 'security_database_clock_immutable' USING ERRCODE='23514'; END IF;
  IF NEW.singleton IS DISTINCT FROM OLD.singleton OR NOT EXISTS(
    SELECT 1 FROM pg_catalog.pg_locks WHERE locktype='advisory' AND pid=pg_catalog.pg_backend_pid()
      AND classid=0 AND objid=8901202 AND objsubid=1 AND mode='ExclusiveLock' AND granted)
  THEN RAISE EXCEPTION 'security_database_clock_lock_required' USING ERRCODE='23514'; END IF;
  -- A session-level lock also appears as ExclusiveLock. Retain
  -- our own transaction-level lock even if that session lock
  -- is subsequently released before the transaction commits.
  PERFORM pg_catalog.pg_advisory_xact_lock(8901202);
  current_time_value:=pg_catalog.clock_timestamp();
  IF current_time_value<OLD.observed_at THEN RAISE EXCEPTION 'security_database_clock_regressed' USING ERRCODE='23514'; END IF;
  NEW.observed_at:=current_time_value;
  RETURN NEW;
END $$;
CREATE TRIGGER security_database_clock_guard BEFORE INSERT OR UPDATE OR DELETE ON public.security_database_clock_watermark
  FOR EACH ROW EXECUTE FUNCTION public.security_database_clock_guard();
CREATE TRIGGER security_database_clock_no_truncate BEFORE TRUNCATE ON public.security_database_clock_watermark
  FOR EACH STATEMENT EXECUTE FUNCTION public.security_database_clock_guard();
CREATE FUNCTION public.security_observe_clock() RETURNS timestamptz LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
DECLARE observed timestamptz;
BEGIN
  UPDATE public.security_database_clock_watermark SET observed_at=observed_at WHERE singleton RETURNING observed_at INTO observed;
  IF NOT FOUND THEN RAISE EXCEPTION 'security_database_clock_missing' USING ERRCODE='23514'; END IF;
  RETURN observed;
END $$;
CREATE FUNCTION public.security_authoritative_fact_time_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
DECLARE fact_time timestamptz;
BEGIN
  IF TG_TABLE_NAME='gateway_security_lease_issuances' THEN fact_time:=NEW.issued_at;
  ELSIF TG_TABLE_NAME='security_ack_revalidation_challenges' THEN fact_time:=NEW.created_at;
  ELSE fact_time:=NEW.received_at;
  END IF;
  IF fact_time>pg_catalog.clock_timestamp() THEN RAISE EXCEPTION 'security_authoritative_fact_in_future' USING ERRCODE='23514'; END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER security_issuance_not_future BEFORE INSERT ON public.gateway_security_lease_issuances
  FOR EACH ROW EXECUTE FUNCTION public.security_authoritative_fact_time_guard();
CREATE TRIGGER security_receipt_not_future BEFORE INSERT OR UPDATE ON public.gateway_security_current_receipts
  FOR EACH ROW EXECUTE FUNCTION public.security_authoritative_fact_time_guard();
CREATE TRIGGER security_ack_verification_not_future BEFORE INSERT ON public.security_ack_verifications
  FOR EACH ROW EXECUTE FUNCTION public.security_authoritative_fact_time_guard();
CREATE TRIGGER security_challenge_not_future BEFORE INSERT ON public.security_ack_revalidation_challenges
  FOR EACH ROW EXECUTE FUNCTION public.security_authoritative_fact_time_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104080000_Governance24DatabaseClock') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261104080000_Governance24DatabaseClock', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 35

-- WEBAPI_MIGRATION_START 36
DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104090000_Governance24MigrationReceipts') THEN
CREATE TABLE public.migration_validation_receipts (
  attempt_id uuid PRIMARY KEY,
  entry_point varchar(16) NOT NULL CHECK (entry_point IN ('Managed','Sql')),
  source_migration varchar(150),
  target_migration varchar(150) NOT NULL,
  identity_state varchar(16) NOT NULL CHECK (identity_state IN ('Unknown','Observed')),
  cluster_identifier varchar(20),
  database_name varchar(63),
  database_oid bigint,
  source_schema_hash char(64),
  migration_manifest_hash char(64),
  validation_state varchar(16) NOT NULL CHECK (validation_state IN ('Unknown','NotApplicable','Passed')),
  settings_present boolean,
  rule_count integer,
  total_bytes bigint,
  transaction_timeout_ms integer NOT NULL CHECK (transaction_timeout_ms BETWEEN 60000 AND 3600000),
  settings_max_bytes integer CHECK (settings_max_bytes BETWEEN 1 AND 1048576),
  rule_max_bytes integer CHECK (rule_max_bytes BETWEEN 1 AND 16384),
  rule_max_count integer CHECK (rule_max_count BETWEEN 1 AND 1000000),
  total_max_bytes bigint CHECK (total_max_bytes BETWEEN 1 AND 1073741824),
  receipt_max_rows integer NOT NULL CHECK (receipt_max_rows BETWEEN 1 AND 1000000),
  historical_sql_hash char(64),
  build_hash char(64),
  recorded_at timestamp with time zone NOT NULL,
  CHECK ((entry_point='Managed' AND settings_max_bytes IS NOT NULL AND rule_max_bytes IS NOT NULL AND rule_max_count IS NOT NULL AND total_max_bytes IS NOT NULL)
      OR (entry_point='Sql' AND settings_max_bytes IS NULL AND rule_max_bytes IS NULL AND rule_max_count IS NULL AND total_max_bytes IS NULL AND validation_state IN ('Unknown','NotApplicable'))),
  CHECK ((identity_state='Unknown' AND cluster_identifier IS NULL AND database_name IS NULL AND database_oid IS NULL)
      OR (identity_state='Observed' AND cluster_identifier IS NOT NULL AND cluster_identifier ~ '^[0-9]{1,20}$'
          AND database_name IS NOT NULL AND database_oid IS NOT NULL AND database_oid>0)),
  CHECK ((validation_state='Unknown' AND settings_present IS NULL AND rule_count IS NULL AND total_bytes IS NULL)
      OR (validation_state='NotApplicable' AND settings_present IS NOT NULL AND settings_present=false
          AND rule_count IS NOT NULL AND rule_count=0 AND total_bytes IS NOT NULL AND total_bytes=0)
      OR (validation_state='Passed' AND settings_present IS NOT NULL AND rule_count IS NOT NULL AND rule_count BETWEEN 0 AND rule_max_count
          AND total_bytes IS NOT NULL AND total_bytes BETWEEN 0 AND total_max_bytes AND (settings_present OR rule_count>0))),
  CHECK (source_schema_hash IS NULL OR source_schema_hash ~ '^[0-9a-f]{64}$'),
  CHECK (migration_manifest_hash IS NULL OR migration_manifest_hash ~ '^[0-9a-f]{64}$'),
  CHECK (historical_sql_hash IS NULL OR historical_sql_hash ~ '^[0-9a-f]{64}$'),
  CHECK (build_hash IS NULL OR build_hash ~ '^[0-9a-f]{64}$')
);
CREATE FUNCTION public.migration_validation_receipt_immutable() RETURNS trigger
  LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
  RAISE EXCEPTION 'migration_validation_receipt_immutable' USING ERRCODE='23514';
END $$;
CREATE TRIGGER migration_receipt_no_change BEFORE UPDATE OR DELETE ON public.migration_validation_receipts
  FOR EACH ROW EXECUTE FUNCTION public.migration_validation_receipt_immutable();
CREATE TRIGGER migration_receipt_no_truncate BEFORE TRUNCATE ON public.migration_validation_receipts
  FOR EACH STATEMENT EXECUTE FUNCTION public.migration_validation_receipt_immutable();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261104090000_Governance24MigrationReceipts') THEN
        INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
        VALUES ('20261104090000_Governance24MigrationReceipts', '10.0.12');
    END IF;
END $EF$;
-- WEBAPI_MIGRATION_END 36

DO $WEBAPI_MIGRATION_RECEIPT$
DECLARE
    attempt uuid := pg_catalog.current_setting('webapi.migration_attempt_id')::uuid;
    max_rows integer := pg_catalog.current_setting('webapi.migration_receipt_limit_validated')::integer;
    timeout_ms integer := pg_catalog.current_setting('webapi.migration_timeout_ms_validated')::integer;
    validation text := pg_catalog.current_setting('webapi.migration_validation_state');
    cluster text;
    database_oid bigint;
    retained integer;
BEGIN
    LOCK TABLE public.migration_validation_receipts IN SHARE ROW EXCLUSIVE MODE;
    IF EXISTS (SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id=attempt) THEN
        RAISE EXCEPTION 'migration_receipt_duplicate';
    END IF;
    SELECT pg_catalog.count(*)::integer INTO retained FROM (SELECT 1 FROM public.migration_validation_receipts LIMIT max_rows) bounded;
    IF retained >= max_rows THEN RAISE EXCEPTION 'migration_receipt_capacity_exceeded'; END IF;
    BEGIN
        SELECT system_identifier::text INTO cluster FROM pg_catalog.pg_control_system();
        SELECT oid::bigint INTO database_oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database();
    EXCEPTION WHEN insufficient_privilege THEN
        cluster := NULL;
        database_oid := NULL;
    END;
    INSERT INTO public.migration_validation_receipts(
        attempt_id,entry_point,source_migration,target_migration,identity_state,cluster_identifier,database_name,database_oid,
        source_schema_hash,migration_manifest_hash,validation_state,settings_present,rule_count,total_bytes,
        transaction_timeout_ms,settings_max_bytes,rule_max_bytes,rule_max_count,total_max_bytes,receipt_max_rows,
        historical_sql_hash,build_hash,recorded_at)
    VALUES(
        attempt,'Sql',NULLIF(pg_catalog.current_setting('webapi.migration_source_version'),''),
        '20261104090000_Governance24MigrationReceipts',
        CASE WHEN cluster IS NULL OR database_oid IS NULL THEN 'Unknown' ELSE 'Observed' END,
        CASE WHEN database_oid IS NULL THEN NULL ELSE cluster END,
        CASE WHEN cluster IS NULL OR database_oid IS NULL THEN NULL ELSE pg_catalog.current_database() END,
        CASE WHEN cluster IS NULL THEN NULL ELSE database_oid END,
        pg_catalog.current_setting('webapi.migration_source_schema_hash'),
        pg_catalog.current_setting('webapi.migration_manifest_hash'),validation,
        CASE WHEN validation='NotApplicable' THEN false ELSE NULL END,
        CASE WHEN validation='NotApplicable' THEN 0 ELSE NULL END,
        CASE WHEN validation='NotApplicable' THEN 0 ELSE NULL END,
        timeout_ms,NULL,NULL,NULL,NULL,max_rows,NULL,NULL,pg_catalog.clock_timestamp());
END
$WEBAPI_MIGRATION_RECEIPT$;

COMMIT;

SELECT pg_catalog.current_setting('webapi.migration_attempt_id') AS migration_attempt_id;
DO $WEBAPI_CLEAR_ATTEMPT$
BEGIN
    PERFORM pg_catalog.set_config('webapi.migration_attempt_id','',false);
    PERFORM pg_catalog.set_config('webapi.migration_journal_history','',false);
END
$WEBAPI_CLEAR_ATTEMPT$;

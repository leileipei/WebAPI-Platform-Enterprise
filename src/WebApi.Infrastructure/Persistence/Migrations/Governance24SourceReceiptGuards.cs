using Microsoft.EntityFrameworkCore.Migrations;

namespace WebApi.Infrastructure.Persistence.Migrations;

// Source shape is supporting evidence only; it is not an environment attestation.
internal static class Governance24SourceReceiptGuards
{
    internal static void Install(MigrationBuilder migration) => migration.Sql("""
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
        """);
}

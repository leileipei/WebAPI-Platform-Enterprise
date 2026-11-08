using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations;

public partial class Governance24MigrationReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
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
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $WEBAPI_RECEIPT_DOWN$ BEGIN
          RAISE EXCEPTION 'migration_receipts_down_blocked' USING ERRCODE='23514';
        END $WEBAPI_RECEIPT_DOWN$;
        """);
}

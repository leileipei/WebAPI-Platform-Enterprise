using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebApi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WebApiDbContext))]
[Migration(CredentialGovernanceJournal.Id)]
public sealed class CredentialGovernanceJournal : Migration
{
    internal const string Id = "20261118130000_CredentialGovernanceJournal";

    // The migration installs structure only. Enrollment binds an independently
    // verified authority and generation under the governance writer fence.
    internal const string InstallSql = """
        CREATE SCHEMA credential_governance;
        CREATE TABLE credential_governance.journal_root (
            singleton boolean PRIMARY KEY CHECK (singleton),
            authority_id uuid NOT NULL CHECK (authority_id <> '00000000-0000-0000-0000-000000000000'),
            generation uuid NOT NULL CHECK (generation <> '00000000-0000-0000-0000-000000000000'),
            genesis_hash varchar(64) NOT NULL CHECK (genesis_hash COLLATE "C" ~ '^[0-9a-f]{64}$')
        );
        CREATE TABLE credential_governance.journal_entries (
            sequence bigint NOT NULL CHECK (sequence > 0),
            is_confirmation boolean NOT NULL,
            operation_id uuid NOT NULL CHECK (operation_id <> '00000000-0000-0000-0000-000000000000'),
            previous_hash varchar(64) NOT NULL CHECK (previous_hash COLLATE "C" ~ '^[0-9a-f]{64}$'),
            position_hash varchar(64) NOT NULL CHECK (position_hash COLLATE "C" ~ '^[0-9a-f]{64}$'),
            recovery_material varchar(349528),
            PRIMARY KEY (sequence, is_confirmation),
            UNIQUE (operation_id, is_confirmation),
            CHECK ((is_confirmation AND recovery_material IS NULL) OR
                   (NOT is_confirmation AND recovery_material IS NOT NULL AND
                    pg_catalog.length(recovery_material) >= 2 AND recovery_material COLLATE "C" ~ '^[A-Za-z0-9_-]+$'))
        );

        CREATE FUNCTION credential_governance.reject_journal_change() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog AS $$
        BEGIN
            RAISE EXCEPTION 'credential_governance_journal_immutable' USING ERRCODE = '23514';
        END;
        $$;
        CREATE TRIGGER root_no_change BEFORE UPDATE OR DELETE ON credential_governance.journal_root
            FOR EACH ROW EXECUTE FUNCTION credential_governance.reject_journal_change();
        CREATE TRIGGER root_no_truncate BEFORE TRUNCATE ON credential_governance.journal_root
            FOR EACH STATEMENT EXECUTE FUNCTION credential_governance.reject_journal_change();
        CREATE TRIGGER entries_no_change BEFORE UPDATE OR DELETE ON credential_governance.journal_entries
            FOR EACH ROW EXECUTE FUNCTION credential_governance.reject_journal_change();
        CREATE TRIGGER entries_no_truncate BEFORE TRUNCATE ON credential_governance.journal_entries
            FOR EACH STATEMENT EXECUTE FUNCTION credential_governance.reject_journal_change();

        CREATE FUNCTION credential_governance.require_next_entry() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog AS $$
        DECLARE
            previous credential_governance.journal_entries%ROWTYPE;
            genesis text;
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid = pg_catalog.pg_backend_pid()
                    AND locktype = 'advisory' AND mode = 'ExclusiveLock' AND classid = 0
                    AND objid = 8901202 AND objsubid = 1 AND granted) THEN
                RAISE EXCEPTION 'credential_governance_journal_fence_required' USING ERRCODE = '23514';
            END IF;
            -- Keep the append serialized until commit even if the caller drops
            -- the session-level advisory lock while its transaction is open.
            PERFORM pg_catalog.pg_advisory_xact_lock(8901202);
            IF TG_TABLE_NAME = 'journal_root' THEN
                RETURN NEW;
            END IF;

            SELECT * INTO previous FROM credential_governance.journal_entries ORDER BY sequence DESC, is_confirmation DESC LIMIT 1;
            IF NOT FOUND THEN
                SELECT genesis_hash INTO genesis FROM credential_governance.journal_root WHERE singleton;
                IF NOT FOUND OR NEW.is_confirmation OR NEW.sequence <> 1 OR NEW.previous_hash <> genesis THEN
                    RAISE EXCEPTION 'credential_governance_journal_sequence_mismatch' USING ERRCODE = '23514';
                END IF;
            ELSIF NEW.is_confirmation THEN
                IF previous.is_confirmation OR ROW(NEW.sequence, NEW.operation_id, NEW.previous_hash, NEW.position_hash)
                    IS DISTINCT FROM ROW(previous.sequence, previous.operation_id, previous.previous_hash, previous.position_hash) THEN
                    RAISE EXCEPTION 'credential_governance_journal_confirmation_mismatch' USING ERRCODE = '23514';
                END IF;
            ELSIF NOT previous.is_confirmation OR NEW.sequence - 1 <> previous.sequence OR NEW.previous_hash <> previous.position_hash THEN
                RAISE EXCEPTION 'credential_governance_journal_sequence_mismatch' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER root_requires_fence BEFORE INSERT ON credential_governance.journal_root
            FOR EACH ROW EXECUTE FUNCTION credential_governance.require_next_entry();
        CREATE TRIGGER entries_in_sequence BEFORE INSERT ON credential_governance.journal_entries
            FOR EACH ROW EXECUTE FUNCTION credential_governance.require_next_entry();
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(InstallSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $WEBAPI_CREDENTIAL_GOVERNANCE_DOWN$ BEGIN
            RAISE EXCEPTION 'credential_governance_journal_down_blocked' USING ERRCODE = '23514';
        END $WEBAPI_CREDENTIAL_GOVERNANCE_DOWN$;
        """);
}

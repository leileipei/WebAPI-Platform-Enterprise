using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations;

public partial class Governance24RegistrationHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Registration identities participate in replay protection and usage
        // provenance, so ordinary event retention must not rewrite their past.
        migrationBuilder.Sql("""
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
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
}

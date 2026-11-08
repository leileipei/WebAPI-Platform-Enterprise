using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations;

public partial class Governance24LegacyPermissionMove : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing API-level grants keep their stable identity when the legacy
        // editor changes API/environment. Strict route-method grants remain
        // immutable; the service authorizes both environments before a move.
        migrationBuilder.Sql("""
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
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
}

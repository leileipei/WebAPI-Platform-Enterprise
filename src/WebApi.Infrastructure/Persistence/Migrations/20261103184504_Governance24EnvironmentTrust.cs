using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24EnvironmentTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environment_security_initializations",
                columns: table => new
                {
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "Unknown"),
                    evidence_hash = table.Column<string>(type: "varchar(64)", nullable: true),
                    fresh_proof_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmation_reason = table.Column<string>(type: "varchar(1000)", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_security_initializations", x => x.environment_id);
                    table.CheckConstraint("ck_environment_security_initialization", "revision > 0 AND (\n  (state = 'Unknown' AND evidence_hash IS NULL AND fresh_proof_id IS NULL AND confirmed_by IS NULL AND confirmed_at IS NULL AND confirmation_reason IS NULL)\n  OR (state = 'FreshNonStrict' AND fresh_proof_id IS NOT NULL AND evidence_hash IS NULL AND confirmed_by IS NULL AND confirmed_at IS NULL AND confirmation_reason IS NULL)\n  OR (state = 'VerifiedLegacyNetwork' AND evidence_hash IS NOT NULL AND evidence_hash ~ '^[a-f0-9]{64}$' AND fresh_proof_id IS NULL AND confirmed_by IS NOT NULL AND confirmed_at IS NOT NULL AND confirmation_reason IS NOT NULL AND length(trim(confirmation_reason)) BETWEEN 1 AND 1000)\n  OR (state = 'Governed' AND (evidence_hash IS NULL OR evidence_hash ~ '^[a-f0-9]{64}$') AND ((confirmed_by IS NULL AND confirmed_at IS NULL AND confirmation_reason IS NULL) OR (confirmed_by IS NOT NULL AND confirmed_at IS NOT NULL AND confirmation_reason IS NOT NULL AND length(trim(confirmation_reason)) BETWEEN 1 AND 1000)))\n)");
                    table.ForeignKey(
                        name: "FK_environment_security_initializations_environments_environme~",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_environment_security_initializations_projects_project_id_or~",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_environment_security_initializations_users_confirmed_by",
                        column: x => x.confirmed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environment_security_initializations_confirmed_by",
                table: "environment_security_initializations",
                column: "confirmed_by");

            migrationBuilder.CreateIndex(
                name: "IX_environment_security_initializations_environment_id_project~",
                table: "environment_security_initializations",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_environment_security_initializations_project_id_organizatio~",
                table: "environment_security_initializations",
                columns: new[] { "project_id", "organization_id" });

            // Pre-existing environments are never inferred to be non-strict.
            migrationBuilder.Sql("""
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
            migrationBuilder.DropTable(
                name: "environment_security_initializations");
        }
    }
}

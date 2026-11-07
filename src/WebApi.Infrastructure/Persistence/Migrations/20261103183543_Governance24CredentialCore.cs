using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24CredentialCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Validate legacy scope before any DDL or data changes; incompatible rows
            // require a separate recovery, not a guessed organization or project.
            migrationBuilder.Sql("""
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
                """);
            migrationBuilder.DropIndex(
                name: "IX_application_credentials_application_id",
                table: "application_credentials");

            migrationBuilder.AddColumn<long>(
                name: "authorization_revision",
                table: "environments",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<bool>(
                name: "require_environment_credential",
                table: "environments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "authorization_revision",
                table: "applications",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "owner_revision",
                table: "applications",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                table: "applications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "binding_mode",
                table: "application_credentials",
                type: "varchar(24)",
                nullable: false,
                defaultValue: "LegacyGlobal");

            migrationBuilder.AddColumn<Guid>(
                name: "environment_id",
                table: "application_credentials",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "expiry_revision",
                table: "application_credentials",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "application_credentials",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE application_credentials c SET organization_id=a.organization_id
                FROM applications a WHERE a.id=c.application_id;
                """);
            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "application_credentials",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "application_credentials",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_application_credentials_id_application_id_organization_id",
                table: "application_credentials",
                columns: new[] { "id", "application_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_applications_owner_user_id",
                table: "applications",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_application_credentials_application_id_environment_id_status",
                table: "application_credentials",
                columns: new[] { "application_id", "environment_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_application_credentials_application_id_organization_id_id",
                table: "application_credentials",
                columns: new[] { "application_id", "organization_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_credentials_environment_id_expires_at",
                table: "application_credentials",
                columns: new[] { "environment_id", "expires_at" },
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_application_credentials_environment_id_project_id",
                table: "application_credentials",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_credentials_last_used_at",
                table: "application_credentials",
                column: "last_used_at");

            migrationBuilder.CreateIndex(
                name: "IX_application_credentials_project_id_organization_id",
                table: "application_credentials",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_credential_binding",
                table: "application_credentials",
                sql: "(binding_mode = 'LegacyGlobal' AND environment_id IS NULL AND project_id IS NULL) OR (binding_mode = 'EnvironmentBound' AND environment_id IS NOT NULL AND project_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credential_expiry_revision",
                table: "application_credentials",
                sql: "expiry_revision > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_application_credentials_applications_application_id_organiz~",
                table: "application_credentials",
                columns: new[] { "application_id", "organization_id" },
                principalTable: "applications",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_application_credentials_environments_environment_id_project~",
                table: "application_credentials",
                columns: new[] { "environment_id", "project_id" },
                principalTable: "environments",
                principalColumns: new[] { "id", "project_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_application_credentials_projects_project_id_organization_id",
                table: "application_credentials",
                columns: new[] { "project_id", "organization_id" },
                principalTable: "projects",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_applications_users_owner_user_id",
                table: "applications",
                column: "owner_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Automatic software downgrade can discard security state. Recovery
            // requires a separate, verified procedure and must never run this DDL.
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
            migrationBuilder.DropForeignKey(
                name: "FK_application_credentials_applications_application_id_organiz~",
                table: "application_credentials");

            migrationBuilder.DropForeignKey(
                name: "FK_application_credentials_environments_environment_id_project~",
                table: "application_credentials");

            migrationBuilder.DropForeignKey(
                name: "FK_application_credentials_projects_project_id_organization_id",
                table: "application_credentials");

            migrationBuilder.DropForeignKey(
                name: "FK_applications_users_owner_user_id",
                table: "applications");

            migrationBuilder.DropIndex(
                name: "IX_applications_owner_user_id",
                table: "applications");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_application_credentials_id_application_id_organization_id",
                table: "application_credentials");

            migrationBuilder.DropIndex(
                name: "IX_application_credentials_application_id_environment_id_status",
                table: "application_credentials");

            migrationBuilder.DropIndex(
                name: "IX_application_credentials_application_id_organization_id_id",
                table: "application_credentials");

            migrationBuilder.DropIndex(
                name: "IX_application_credentials_environment_id_expires_at",
                table: "application_credentials");

            migrationBuilder.DropIndex(
                name: "IX_application_credentials_environment_id_project_id",
                table: "application_credentials");

            migrationBuilder.DropIndex(
                name: "IX_application_credentials_last_used_at",
                table: "application_credentials");

            migrationBuilder.DropIndex(
                name: "IX_application_credentials_project_id_organization_id",
                table: "application_credentials");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credential_binding",
                table: "application_credentials");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credential_expiry_revision",
                table: "application_credentials");

            migrationBuilder.DropColumn(
                name: "authorization_revision",
                table: "environments");

            migrationBuilder.DropColumn(
                name: "require_environment_credential",
                table: "environments");

            migrationBuilder.DropColumn(
                name: "authorization_revision",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "owner_revision",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "binding_mode",
                table: "application_credentials");

            migrationBuilder.DropColumn(
                name: "environment_id",
                table: "application_credentials");

            migrationBuilder.DropColumn(
                name: "expiry_revision",
                table: "application_credentials");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "application_credentials");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "application_credentials");

            migrationBuilder.CreateIndex(
                name: "IX_application_credentials_application_id",
                table: "application_credentials",
                column: "application_id");
        }
    }
}

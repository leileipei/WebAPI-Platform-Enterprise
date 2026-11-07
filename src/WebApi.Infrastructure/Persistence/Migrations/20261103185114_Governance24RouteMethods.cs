using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24RouteMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
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
                """);
            migrationBuilder.DropIndex(
                name: "IX_application_api_permissions_api_id",
                table: "application_api_permissions");

            migrationBuilder.DropIndex(
                name: "IX_application_api_permissions_environment_id",
                table: "application_api_permissions");

            migrationBuilder.DropIndex(
                name: "IX_api_routes_api_version_id",
                table: "api_routes");

            migrationBuilder.AddColumn<long>(
                name: "expiry_revision",
                table: "application_api_permissions",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "application_api_permissions",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "RouteMethod");

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "application_api_permissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "application_api_permissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "revision_transaction_id",
                table: "application_api_permissions",
                type: "bigint",
                nullable: false,
                defaultValueSql: "txid_current()");

            migrationBuilder.AddColumn<Guid>(
                name: "api_id",
                table: "api_routes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "authentication_mode",
                table: "api_routes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "ApiKey");

            migrationBuilder.AddColumn<long>(
                name: "authorization_revision",
                table: "api_routes",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql("""
                UPDATE application_api_permissions g SET mode='LegacyApi',project_id=e.project_id,organization_id=p.organization_id
                FROM environments e JOIN projects p ON p.id=e.project_id WHERE e.id=g.environment_id;
                UPDATE api_routes r SET api_id=v.api_id FROM api_versions v WHERE v.id=r.api_version_id;
                """);
            migrationBuilder.AlterColumn<Guid>(name: "organization_id", table: "application_api_permissions", type: "uuid", nullable: false,
                defaultValueSql: "'00000000-0000-0000-0000-000000000000'::uuid", oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "project_id", table: "application_api_permissions", type: "uuid", nullable: false,
                defaultValueSql: "'00000000-0000-0000-0000-000000000000'::uuid", oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>(name: "api_id", table: "api_routes", type: "uuid", nullable: false,
                defaultValueSql: "'00000000-0000-0000-0000-000000000000'::uuid", oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_application_api_permissions_id_environment_id_api_id",
                table: "application_api_permissions",
                columns: new[] { "id", "environment_id", "api_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_apis_id_project_id_organization_id",
                table: "apis",
                columns: new[] { "id", "project_id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_api_versions_id_api_id",
                table: "api_versions",
                columns: new[] { "id", "api_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_api_routes_id_environment_id_api_id",
                table: "api_routes",
                columns: new[] { "id", "environment_id", "api_id" });

            migrationBuilder.CreateTable(
                name: "application_route_method_grants",
                columns: table => new
                {
                    permission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_id = table.Column<Guid>(type: "uuid", nullable: false),
                    authorization_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_route_method_grants", x => new { x.permission_id, x.route_id, x.method });
                    table.CheckConstraint("ck_route_method_grant", "authorization_revision>0 AND method IN ('GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS')");
                    table.ForeignKey(
                        name: "FK_application_route_method_grants_api_routes_route_id_environ~",
                        columns: x => new { x.route_id, x.environment_id, x.api_id },
                        principalTable: "api_routes",
                        principalColumns: new[] { "id", "environment_id", "api_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_route_method_grants_application_api_permissions~",
                        columns: x => new { x.permission_id, x.environment_id, x.api_id },
                        principalTable: "application_api_permissions",
                        principalColumns: new[] { "id", "environment_id", "api_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_api_id_project_id_organization_~",
                table: "application_api_permissions",
                columns: new[] { "api_id", "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_application_id_organization_id",
                table: "application_api_permissions",
                columns: new[] { "application_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_environment_id_project_id",
                table: "application_api_permissions",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_project_id_organization_id",
                table: "application_api_permissions",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_permission_mode",
                table: "application_api_permissions",
                sql: "mode IN ('LegacyApi','RouteMethod')");

            migrationBuilder.CreateIndex(
                name: "IX_api_routes_api_version_id_api_id",
                table: "api_routes",
                columns: new[] { "api_version_id", "api_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_route_authorization",
                table: "api_routes",
                sql: "authorization_revision>0 AND authentication_mode IN ('ApiKey','Anonymous','JWT')");

            migrationBuilder.CreateIndex(
                name: "IX_application_route_method_grants_permission_id_environment_i~",
                table: "application_route_method_grants",
                columns: new[] { "permission_id", "environment_id", "api_id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_route_method_grants_route_id",
                table: "application_route_method_grants",
                column: "route_id");

            migrationBuilder.CreateIndex(
                name: "IX_application_route_method_grants_route_id_environment_id_api~",
                table: "application_route_method_grants",
                columns: new[] { "route_id", "environment_id", "api_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_api_routes_api_versions_api_version_id_api_id",
                table: "api_routes",
                columns: new[] { "api_version_id", "api_id" },
                principalTable: "api_versions",
                principalColumns: new[] { "id", "api_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_application_api_permissions_apis_api_id_project_id_organiza~",
                table: "application_api_permissions",
                columns: new[] { "api_id", "project_id", "organization_id" },
                principalTable: "apis",
                principalColumns: new[] { "id", "project_id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_application_api_permissions_applications_application_id_org~",
                table: "application_api_permissions",
                columns: new[] { "application_id", "organization_id" },
                principalTable: "applications",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_application_api_permissions_environments_environment_id_pro~",
                table: "application_api_permissions",
                columns: new[] { "environment_id", "project_id" },
                principalTable: "environments",
                principalColumns: new[] { "id", "project_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_application_api_permissions_projects_project_id_organizatio~",
                table: "application_api_permissions",
                columns: new[] { "project_id", "organization_id" },
                principalTable: "projects",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            Governance24RouteGuards.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
            migrationBuilder.DropForeignKey(
                name: "FK_api_routes_api_versions_api_version_id_api_id",
                table: "api_routes");

            migrationBuilder.DropForeignKey(
                name: "FK_application_api_permissions_apis_api_id_project_id_organiza~",
                table: "application_api_permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_application_api_permissions_applications_application_id_org~",
                table: "application_api_permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_application_api_permissions_environments_environment_id_pro~",
                table: "application_api_permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_application_api_permissions_projects_project_id_organizatio~",
                table: "application_api_permissions");

            migrationBuilder.DropTable(
                name: "application_route_method_grants");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_application_api_permissions_id_environment_id_api_id",
                table: "application_api_permissions");

            migrationBuilder.DropIndex(
                name: "IX_application_api_permissions_api_id_project_id_organization_~",
                table: "application_api_permissions");

            migrationBuilder.DropIndex(
                name: "IX_application_api_permissions_application_id_organization_id",
                table: "application_api_permissions");

            migrationBuilder.DropIndex(
                name: "IX_application_api_permissions_environment_id_project_id",
                table: "application_api_permissions");

            migrationBuilder.DropIndex(
                name: "IX_application_api_permissions_project_id_organization_id",
                table: "application_api_permissions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_permission_mode",
                table: "application_api_permissions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_apis_id_project_id_organization_id",
                table: "apis");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_api_versions_id_api_id",
                table: "api_versions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_api_routes_id_environment_id_api_id",
                table: "api_routes");

            migrationBuilder.DropIndex(
                name: "IX_api_routes_api_version_id_api_id",
                table: "api_routes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_route_authorization",
                table: "api_routes");

            migrationBuilder.DropColumn(
                name: "expiry_revision",
                table: "application_api_permissions");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "application_api_permissions");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "application_api_permissions");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "application_api_permissions");

            migrationBuilder.DropColumn(
                name: "revision_transaction_id",
                table: "application_api_permissions");

            migrationBuilder.DropColumn(
                name: "api_id",
                table: "api_routes");

            migrationBuilder.DropColumn(
                name: "authentication_mode",
                table: "api_routes");

            migrationBuilder.DropColumn(
                name: "authorization_revision",
                table: "api_routes");

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_api_id",
                table: "application_api_permissions",
                column: "api_id");

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_environment_id",
                table: "application_api_permissions",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_routes_api_version_id",
                table: "api_routes",
                column: "api_version_id");
        }
    }
}

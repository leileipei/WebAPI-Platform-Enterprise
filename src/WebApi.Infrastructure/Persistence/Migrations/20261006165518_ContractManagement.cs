using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_import_previews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cluster_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    bundle_json = table.Column<string>(type: "jsonb", nullable: true),
                    preview_json = table.Column<string>(type: "jsonb", nullable: true),
                    source_hash = table.Column<string>(type: "varchar(64)", nullable: false),
                    bundle_hash = table.Column<string>(type: "varchar(64)", nullable: false),
                    source_format = table.Column<string>(type: "varchar(16)", nullable: false),
                    dialect = table.Column<string>(type: "varchar(16)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: true),
                    committed_targets_json = table.Column<string>(type: "jsonb", nullable: true),
                    receipt_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_import_previews", x => x.id);
                    table.CheckConstraint("ck_import_preview_dialect", "dialect IN ('Oas30','Oas31') AND source_format IN ('json','yaml')");
                    table.CheckConstraint("ck_import_preview_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_import_preview_revision", "revision >= 1 AND source_policy_revision >= 0");
                    table.CheckConstraint("ck_import_preview_status", "status IN ('Active','Committed','Revoked','Expired')");
                    table.ForeignKey(
                        name: "FK_api_import_previews_environments_environment_id_project_id",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_import_previews_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_import_previews_upstream_clusters_cluster_id_environmen~",
                        columns: x => new { x.cluster_id, x.environment_id },
                        principalTable: "upstream_clusters",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_import_previews_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_version_contract_sources",
                columns: table => new
                {
                    api_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bundle_json = table.Column<string>(type: "jsonb", nullable: false),
                    bundle_hash = table.Column<string>(type: "varchar(64)", nullable: false),
                    sources_json = table.Column<string>(type: "jsonb", nullable: false),
                    dialect = table.Column<string>(type: "varchar(16)", nullable: false),
                    source_policy_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_version_contract_sources", x => x.api_version_id);
                    table.CheckConstraint("ck_contract_source_dialect", "dialect IN ('Oas30','Oas31')");
                    table.CheckConstraint("ck_contract_source_policy_revision", "source_policy_revision >= 0");
                    table.ForeignKey(
                        name: "FK_api_version_contract_sources_api_versions_api_version_id",
                        column: x => x.api_version_id,
                        principalTable: "api_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_import_source_policies",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rules_json = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_import_source_policies", x => x.project_id);
                    table.CheckConstraint("ck_import_policy_revision", "revision >= 1");
                    table.ForeignKey(
                        name: "FK_project_import_source_policies_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_import_source_policies_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_import_previews_actor_id",
                table: "api_import_previews",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_import_previews_cluster_id_environment_id",
                table: "api_import_previews",
                columns: new[] { "cluster_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_api_import_previews_environment_id_project_id",
                table: "api_import_previews",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_api_import_previews_project_id_actor_id_status_expires_at",
                table: "api_import_previews",
                columns: new[] { "project_id", "actor_id", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_api_import_previews_project_id_organization_id",
                table: "api_import_previews",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_api_import_previews_receipt_expires_at",
                table: "api_import_previews",
                column: "receipt_expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_api_import_previews_status_expires_at",
                table: "api_import_previews",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_project_import_source_policies_updated_by",
                table: "project_import_source_policies",
                column: "updated_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_import_previews");

            migrationBuilder.DropTable(
                name: "api_version_contract_sources");

            migrationBuilder.DropTable(
                name: "project_import_source_policies");
        }
    }
}

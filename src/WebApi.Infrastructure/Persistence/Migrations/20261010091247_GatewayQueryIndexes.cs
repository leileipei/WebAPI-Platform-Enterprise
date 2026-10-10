using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GatewayQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Exact existing route rank; no computed/business column. Transactional DDL.
            migrationBuilder.Sql("CREATE INDEX ix_api_routes_environment_order_path_id ON api_routes (environment_id, ((CASE WHEN normalized_path LIKE '%{*}%' THEN 2 WHEN normalized_path LIKE '%{}%' THEN 1 ELSE 0 END) * 2000000 - priority), path, id);");

            migrationBuilder.CreateIndex(
                name: "ix_policies_organization_name_id",
                table: "policies",
                columns: new[] { "organization_id", "name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_organization_name_id",
                table: "alert_rules",
                columns: new[] { "organization_id", "normalized_name", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_events_organization_project_started_id",
                table: "alert_events",
                columns: new[] { "organization_id", "project_id", "started_at", "id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_api_routes_environment_order_path_id", table: "api_routes");

            migrationBuilder.DropIndex(
                name: "ix_policies_organization_name_id",
                table: "policies");

            migrationBuilder.DropIndex(
                name: "ix_alert_rules_organization_name_id",
                table: "alert_rules");

            migrationBuilder.DropIndex(
                name: "ix_alert_events_organization_project_started_id",
                table: "alert_events");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalInboxIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_release_records_environment_id_created_at_id",
                table: "release_records",
                columns: new[] { "environment_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_approval_tasks_assignee_user_id_status_release_id",
                table: "approval_tasks",
                columns: new[] { "assignee_user_id", "status", "release_id" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_tasks_release_id_status_step_order_id",
                table: "approval_tasks",
                columns: new[] { "release_id", "status", "step_order", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_release_records_environment_id_created_at_id",
                table: "release_records");

            migrationBuilder.DropIndex(
                name: "IX_approval_tasks_assignee_user_id_status_release_id",
                table: "approval_tasks");

            migrationBuilder.DropIndex(
                name: "IX_approval_tasks_release_id_status_step_order_id",
                table: "approval_tasks");
        }
    }
}

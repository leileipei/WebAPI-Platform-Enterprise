using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublishCoordinatorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "publish_requested_by",
                table: "release_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "publish_trace_id",
                table: "release_records",
                type: "varchar(128)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_records_environment_id_deployment_sequence",
                table: "release_records",
                columns: new[] { "environment_id", "deployment_sequence" },
                unique: true,
                filter: "deployment_sequence > 0");

            migrationBuilder.CreateIndex(
                name: "IX_release_records_publish_requested_by",
                table: "release_records",
                column: "publish_requested_by");

            migrationBuilder.AddForeignKey(
                name: "FK_release_records_users_publish_requested_by",
                table: "release_records",
                column: "publish_requested_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_release_records_users_publish_requested_by",
                table: "release_records");

            migrationBuilder.DropIndex(
                name: "IX_release_records_environment_id_deployment_sequence",
                table: "release_records");

            migrationBuilder.DropIndex(
                name: "IX_release_records_publish_requested_by",
                table: "release_records");

            migrationBuilder.DropColumn(
                name: "publish_requested_by",
                table: "release_records");

            migrationBuilder.DropColumn(
                name: "publish_trace_id",
                table: "release_records");
        }
    }
}

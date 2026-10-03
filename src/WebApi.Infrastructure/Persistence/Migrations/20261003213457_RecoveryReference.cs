using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "recovery_of",
                table: "release_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_records_recovery_of",
                table: "release_records",
                column: "recovery_of");

            migrationBuilder.AddForeignKey(
                name: "FK_release_records_release_records_recovery_of",
                table: "release_records",
                column: "recovery_of",
                principalTable: "release_records",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_release_records_release_records_recovery_of",
                table: "release_records");

            migrationBuilder.DropIndex(
                name: "IX_release_records_recovery_of",
                table: "release_records");

            migrationBuilder.DropColumn(
                name: "recovery_of",
                table: "release_records");
        }
    }
}

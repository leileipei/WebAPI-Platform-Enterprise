using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24SourceReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_environment_security_initializations_environment_id_project~",
                table: "environment_security_initializations");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_environment_security_initializations_environment_id_project~",
                table: "environment_security_initializations",
                columns: new[] { "environment_id", "project_id", "organization_id" });

            migrationBuilder.CreateTable(
                name: "environment_security_source_receipts",
                columns: table => new
                {
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_database = table.Column<string>(type: "varchar(63)", nullable: false),
                    source_database_oid = table.Column<long>(type: "bigint", nullable: false),
                    source_cluster_id = table.Column<string>(type: "varchar(20)", nullable: false),
                    source_migration = table.Column<string>(type: "varchar(150)", nullable: false),
                    source_schema_hash = table.Column<string>(type: "varchar(64)", nullable: false),
                    source_history_hash = table.Column<string>(type: "varchar(64)", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_security_source_receipts", x => x.environment_id);
                    table.CheckConstraint("ck_environment_security_source_receipt", "source_database_oid > 0 AND length(source_database) > 0 AND source_cluster_id ~ '^[0-9]{1,20}$'\nAND length(source_migration) > 0 AND source_schema_hash ~ '^[a-f0-9]{64}$' AND source_history_hash ~ '^[a-f0-9]{64}$'");
                    table.ForeignKey(
                        name: "FK_environment_security_source_receipts_environment_security_i~",
                        columns: x => new { x.environment_id, x.project_id, x.organization_id },
                        principalTable: "environment_security_initializations",
                        principalColumns: new[] { "environment_id", "project_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environment_security_source_receipts_environment_id_project~",
                table: "environment_security_source_receipts",
                columns: new[] { "environment_id", "project_id", "organization_id" });

            Governance24SourceReceiptGuards.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
            migrationBuilder.DropTable(
                name: "environment_security_source_receipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_environment_security_initializations_environment_id_project~",
                table: "environment_security_initializations");

            migrationBuilder.CreateIndex(
                name: "IX_environment_security_initializations_environment_id_project~",
                table: "environment_security_initializations",
                columns: new[] { "environment_id", "project_id" });
        }
    }
}

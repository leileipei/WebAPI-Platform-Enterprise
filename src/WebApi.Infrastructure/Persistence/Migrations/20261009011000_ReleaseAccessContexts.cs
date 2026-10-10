using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseAccessContexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_release_records_id_environment_id",
                table: "release_records",
                columns: new[] { "id", "environment_id" });

            migrationBuilder.CreateTable(
                name: "release_access_contexts",
                columns: table => new
                {
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_address_revision = table.Column<long>(type: "bigint", nullable: false),
                    public_origin = table.Column<string>(type: "varchar(2048)", nullable: true),
                    internal_origin = table.Column<string>(type: "varchar(2048)", nullable: true),
                    base_path = table.Column<string>(type: "varchar(512)", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_access_contexts", x => x.release_id);
                    table.ForeignKey(
                        name: "FK_release_access_contexts_release_records_release_id_environm~",
                        columns: x => new { x.release_id, x.environment_id },
                        principalTable: "release_records",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_release_access_contexts_release_id_environment_id",
                table: "release_access_contexts",
                columns: new[] { "release_id", "environment_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "release_access_contexts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_release_records_id_environment_id",
                table: "release_records");
        }
    }
}

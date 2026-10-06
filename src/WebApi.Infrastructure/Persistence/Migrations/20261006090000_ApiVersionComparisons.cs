using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiVersionComparisons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_version_comparisons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_revision = table.Column<long>(type: "bigint", nullable: false),
                    to_revision = table.Column<long>(type: "bigint", nullable: false),
                    from_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    to_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    engine_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    input_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    report_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    coverage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    counts_json = table.Column<string>(type: "jsonb", nullable: false),
                    input_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    report_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_version_comparisons", x => x.id);
                    table.CheckConstraint("ck_api_version_comparisons_coverage", "coverage IN ('Complete','Limited','Invalid')");
                    table.CheckConstraint("ck_api_version_comparisons_hashes", "input_fingerprint ~ '^[0-9a-f]{64}$' AND report_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_api_version_comparisons_versions", "from_version_id <> to_version_id AND from_revision >= 1 AND to_revision >= 1");
                });

            migrationBuilder.CreateTable(
                name: "api_version_risk_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    comparison_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_id = table.Column<Guid>(type: "uuid", nullable: false),
                    input_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    report_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    decision = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_version_risk_reviews", x => x.id);
                    table.CheckConstraint("ck_api_version_risk_reviews_decision", "decision IN ('Reviewed','AcceptedRisk') AND (decision <> 'AcceptedRisk' OR (comment IS NOT NULL AND length(btrim(comment)) BETWEEN 10 AND 2000))");
                    table.CheckConstraint("ck_api_version_risk_reviews_hashes", "input_fingerprint ~ '^[0-9a-f]{64}$' AND report_hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_api_version_risk_reviews_api_version_comparisons_comparison~",
                        column: x => x.comparison_id,
                        principalTable: "api_version_comparisons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_version_comparisons_api_id_created_at_id",
                table: "api_version_comparisons",
                columns: new[] { "api_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_api_version_risk_reviews_comparison_id_created_at_id",
                table: "api_version_risk_reviews",
                columns: new[] { "comparison_id", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_version_risk_reviews");

            migrationBuilder.DropTable(
                name: "api_version_comparisons");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CredentialUsageTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "credential_usage_collectors",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protocol = table.Column<string>(type: "varchar(8)", nullable: false, defaultValue: "1.0"),
                    accepted_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    accepted_through = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()"),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credential_usage_collectors", x => new { x.node_id, x.collector_id });
                    table.CheckConstraint("ck_usage_collector_protocol", "protocol = '1.0'");
                    table.CheckConstraint("ck_usage_collector_sequence", "accepted_sequence >= 0");
                    table.ForeignKey(
                        name: "FK_credential_usage_collectors_gateway_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "gateway_nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "credential_usage_coverage",
                columns: table => new
                {
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hour_start = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    intervals = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    gaps = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    durable_through = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    accepted_through = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    state = table.Column<string>(type: "varchar(16)", nullable: false, defaultValue: "Partial"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credential_usage_coverage", x => new { x.environment_id, x.node_id, x.hour_start });
                    table.CheckConstraint("ck_usage_coverage_arrays", "jsonb_typeof(intervals)='array' AND jsonb_typeof(gaps)='array'");
                    table.CheckConstraint("ck_usage_coverage_budget", "octet_length(intervals::text)+octet_length(gaps::text)<=1048576");
                    table.CheckConstraint("ck_usage_coverage_hour", "hour_start = date_trunc('hour', hour_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'");
                    table.CheckConstraint("ck_usage_coverage_state", "state IN ('Available','NoData','Partial','Unknown','Unavailable')");
                });

            migrationBuilder.CreateTable(
                name: "credential_usage_hourly",
                columns: table => new
                {
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hour_start = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credential_usage_hourly", x => new { x.environment_id, x.credential_id, x.hour_start });
                    table.CheckConstraint("ck_usage_hourly_count", "count > 0");
                    table.CheckConstraint("ck_usage_hourly_hour", "hour_start = date_trunc('hour', hour_start AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'");
                    table.CheckConstraint("ck_usage_hourly_last_used", "last_used_at >= hour_start AND last_used_at < hour_start + INTERVAL '1 hour'");
                });

            migrationBuilder.CreateTable(
                name: "credential_usage_batches",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    core_hash = table.Column<string>(type: "char(64)", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()"),
                    accepted_through = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credential_usage_batches", x => new { x.node_id, x.collector_id, x.sequence });
                    table.CheckConstraint("ck_usage_batch_hash", "core_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_usage_batch_sequence", "sequence > 0");
                    table.ForeignKey(
                        name: "FK_credential_usage_batches_credential_usage_collectors_node_i~",
                        columns: x => new { x.node_id, x.collector_id },
                        principalTable: "credential_usage_collectors",
                        principalColumns: new[] { "node_id", "collector_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "credential_usage_collector_instances",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "NOW()"),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    identity_proof = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "current")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credential_usage_collector_instances", x => new { x.node_id, x.collector_id, x.instance_id });
                    table.ForeignKey(
                        name: "FK_credential_usage_collector_instances_credential_usage_colle~",
                        columns: x => new { x.node_id, x.collector_id },
                        principalTable: "credential_usage_collectors",
                        principalColumns: new[] { "node_id", "collector_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_usage_batches_applied",
                table: "credential_usage_batches",
                column: "applied_at");

            migrationBuilder.CreateIndex(
                name: "ix_usage_instances_ended",
                table: "credential_usage_collector_instances",
                column: "ended_at");

            migrationBuilder.CreateIndex(
                name: "ix_usage_collectors_closed",
                table: "credential_usage_collectors",
                column: "closed_at");

            migrationBuilder.CreateIndex(
                name: "ix_usage_coverage_retention",
                table: "credential_usage_coverage",
                column: "hour_start");

            migrationBuilder.CreateIndex(
                name: "ix_usage_hourly_application_window",
                table: "credential_usage_hourly",
                columns: new[] { "application_id", "hour_start", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_hourly_retention",
                table: "credential_usage_hourly",
                column: "hour_start");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credential_usage_batches");

            migrationBuilder.DropTable(
                name: "credential_usage_collector_instances");

            migrationBuilder.DropTable(
                name: "credential_usage_coverage");

            migrationBuilder.DropTable(
                name: "credential_usage_hourly");

            migrationBuilder.DropTable(
                name: "credential_usage_collectors");
        }
    }
}

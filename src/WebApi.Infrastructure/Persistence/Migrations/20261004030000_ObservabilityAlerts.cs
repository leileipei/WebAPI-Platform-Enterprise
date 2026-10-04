using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ObservabilityAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    normalized_name = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    metric = table.Column<string>(type: "text", nullable: false),
                    expression = table.Column<string>(type: "text", maxLength: 256, nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    for_seconds = table.Column<int>(type: "integer", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    window_seconds = table.Column<int>(type: "integer", nullable: false),
                    notification = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    logic_revision = table.Column<long>(type: "bigint", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.id);
                    table.UniqueConstraint("AK_alert_rules_id_organization_id", x => new { x.id, x.organization_id });
                    table.CheckConstraint("ck_alert_rule_limits", "for_seconds BETWEEN 0 AND 86400 AND window_seconds BETWEEN 60 AND 3600 AND revision > 0 AND logic_revision > 0 AND length(normalized_name) BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_alert_rule_metric", "metric IN ('request_rps','error_5xx_ratio','latency_p95_ms','unhealthy_destinations')");
                    table.CheckConstraint("ck_alert_rule_scope", "environment_id IS NULL OR project_id IS NOT NULL");
                    table.CheckConstraint("ck_alert_rule_severity", "severity IN ('Info','Warning','Critical')");
                    table.CheckConstraint("ck_alert_rule_target", "(target_type = 'Environment' AND target_id IS NULL) OR (target_type = 'Api' AND target_id IS NOT NULL AND project_id IS NOT NULL) OR (target_type = 'Destination' AND target_id IS NOT NULL AND project_id IS NOT NULL AND environment_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_alert_rules_environments_environment_id_project_id",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_rules_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_rules_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_rules_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_rules_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alert_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_revision = table.Column<long>(type: "bigint", nullable: false),
                    logic_revision = table.Column<long>(type: "bigint", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_key = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    resource_type = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurrence_no = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    message = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    rule_summary = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    condition_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    acked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    silenced_by = table.Column<Guid>(type: "uuid", nullable: true),
                    silenced_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    silence_reason = table.Column<string>(type: "text", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolve_reason = table.Column<string>(type: "text", nullable: true),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_value = table.Column<double>(type: "double precision", nullable: true),
                    last_condition = table.Column<bool>(type: "boolean", nullable: true),
                    evaluation_state = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_events", x => x.id);
                    table.UniqueConstraint("AK_alert_events_id_rule_id_environment_id_resource_key", x => new { x.id, x.rule_id, x.environment_id, x.resource_key });
                    table.CheckConstraint("ck_alert_event_resource", "length(resource_key) BETWEEN 1 AND 128 AND ((resource_type='Environment' AND resource_id IS NULL) OR (resource_type IN ('Api','Destination') AND resource_id IS NOT NULL))");
                    table.CheckConstraint("ck_alert_event_revision", "revision > 0 AND rule_revision > 0 AND logic_revision > 0 AND occurrence_no > 0");
                    table.CheckConstraint("ck_alert_event_severity", "severity IN ('Info','Warning','Critical')");
                    table.CheckConstraint("ck_alert_event_status", "status IN ('Open','Ack','Silenced','Resolved') AND evaluation_state IN ('Known','Unknown','ScopeInactive')");
                    table.ForeignKey(
                        name: "FK_alert_events_alert_rules_rule_id_organization_id",
                        columns: x => new { x.rule_id, x.organization_id },
                        principalTable: "alert_rules",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_events_environments_environment_id_project_id",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_events_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_events_users_acked_by",
                        column: x => x.acked_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_events_users_resolved_by",
                        column: x => x.resolved_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_events_users_silenced_by",
                        column: x => x.silenced_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alert_evaluation_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    logic_revision = table.Column<long>(type: "bigint", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_key = table.Column<string>(type: "text", maxLength: 128, nullable: false),
                    resource_type = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    phase = table.Column<string>(type: "text", nullable: false),
                    pending_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_evaluated_slot = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_condition = table.Column<bool>(type: "boolean", nullable: true),
                    last_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    next_occurrence_no = table.Column<long>(type: "bigint", nullable: false),
                    evaluation_state = table.Column<string>(type: "text", nullable: false),
                    suppressed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "text", maxLength: 128, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_evaluation_states", x => x.id);
                    table.CheckConstraint("ck_alert_evaluation_phase", "phase IN ('Inactive','Pending','Firing','SuppressedUntilRecovery') AND evaluation_state IN ('Known','Unknown','ScopeInactive')");
                    table.CheckConstraint("ck_alert_evaluation_resource", "length(resource_key) BETWEEN 1 AND 128 AND ((resource_type='Environment' AND resource_id IS NULL) OR (resource_type IN ('Api','Destination') AND resource_id IS NOT NULL))");
                    table.CheckConstraint("ck_alert_evaluation_revision", "logic_revision > 0 AND revision > 0 AND lease_token >= 0 AND next_occurrence_no > 0");
                    table.ForeignKey(
                        name: "FK_alert_evaluation_states_alert_events_last_event_id_rule_id_~",
                        columns: x => new { x.last_event_id, x.rule_id, x.environment_id, x.resource_key },
                        principalTable: "alert_events",
                        principalColumns: new[] { "id", "rule_id", "environment_id", "resource_key" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_evaluation_states_alert_rules_rule_id_organization_id",
                        columns: x => new { x.rule_id, x.organization_id },
                        principalTable: "alert_rules",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_evaluation_states_environments_environment_id_project~",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_evaluation_states_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alert_event_transitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "text", nullable: true),
                    to_status = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "text", maxLength: 512, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "text", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_event_transitions", x => x.id);
                    table.CheckConstraint("ck_alert_transition_status", "(from_status IS NULL OR from_status IN ('Open','Ack','Silenced','Resolved')) AND to_status IN ('Open','Ack','Silenced','Resolved')");
                    table.ForeignKey(
                        name: "FK_alert_event_transitions_alert_events_event_id",
                        column: x => x.event_id,
                        principalTable: "alert_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_event_transitions_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_evaluation_states_environment_id_project_id",
                table: "alert_evaluation_states",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_evaluation_states_last_event_id_rule_id_environment_i~",
                table: "alert_evaluation_states",
                columns: new[] { "last_event_id", "rule_id", "environment_id", "resource_key" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_evaluation_states_lease_until_last_evaluated_slot",
                table: "alert_evaluation_states",
                columns: new[] { "lease_until", "last_evaluated_slot" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_evaluation_states_project_id_organization_id",
                table: "alert_evaluation_states",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_evaluation_states_rule_id_logic_revision_environment_~",
                table: "alert_evaluation_states",
                columns: new[] { "rule_id", "logic_revision", "environment_id", "resource_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_evaluation_states_rule_id_organization_id",
                table: "alert_evaluation_states",
                columns: new[] { "rule_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_event_transitions_actor_id",
                table: "alert_event_transitions",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_event_transitions_event_id_occurred_at",
                table: "alert_event_transitions",
                columns: new[] { "event_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_acked_by",
                table: "alert_events",
                column: "acked_by");

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_environment_id_project_id",
                table: "alert_events",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_environment_id_status_started_at",
                table: "alert_events",
                columns: new[] { "environment_id", "status", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_project_id_organization_id",
                table: "alert_events",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_resolved_by",
                table: "alert_events",
                column: "resolved_by");

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_rule_id_environment_id_resource_key",
                table: "alert_events",
                columns: new[] { "rule_id", "environment_id", "resource_key" },
                unique: true,
                filter: "status <> 'Resolved'");

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_rule_id_logic_revision_environment_id_resource~",
                table: "alert_events",
                columns: new[] { "rule_id", "logic_revision", "environment_id", "resource_key", "occurrence_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_rule_id_organization_id",
                table: "alert_events",
                columns: new[] { "rule_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_silenced_by",
                table: "alert_events",
                column: "silenced_by");

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_created_by",
                table: "alert_rules",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_environment_id_project_id",
                table: "alert_rules",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_organization_id_project_id_environment_id_norma~",
                table: "alert_rules",
                columns: new[] { "organization_id", "project_id", "environment_id", "normalized_name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_project_id_organization_id",
                table: "alert_rules",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_updated_by",
                table: "alert_rules",
                column: "updated_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_evaluation_states");

            migrationBuilder.DropTable(
                name: "alert_event_transitions");

            migrationBuilder.DropTable(
                name: "alert_events");

            migrationBuilder.DropTable(
                name: "alert_rules");
        }
    }
}

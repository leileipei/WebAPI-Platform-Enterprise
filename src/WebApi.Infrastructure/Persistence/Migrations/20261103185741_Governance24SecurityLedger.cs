using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24SecurityLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_gateway_nodes_id_environment_id",
                table: "gateway_nodes",
                columns: new[] { "id", "environment_id" });

            migrationBuilder.CreateTable(
                name: "environment_security_states",
                columns: table => new
                {
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    head_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    initial_require_environment_credentials = table.Column<bool>(type: "boolean", nullable: true),
                    require_environment_credentials = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_snapshot_schema = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    lease_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    lease_refresh_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    maximum_clock_skew_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 2),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_security_states", x => x.environment_id);
                    table.UniqueConstraint("AK_environment_security_states_environment_id_security_generat~", x => new { x.environment_id, x.security_generation });
                    table.CheckConstraint("ck_security_lease_policy", "lease_seconds BETWEEN 5 AND 30 AND lease_refresh_seconds BETWEEN 1 AND 15 AND lease_refresh_seconds<=lease_seconds/2 AND maximum_clock_skew_seconds BETWEEN 0 AND 2 AND minimum_snapshot_schema IN ('2.0','2.1','2.2','2.3','2.4')");
                    table.CheckConstraint("ck_security_state", "epoch >= 0 AND revision > 0 AND length(head_hash)=64 AND security_generation <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_environment_security_states_environments_environment_id_pro~",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_environment_security_states_projects_project_id_organizatio~",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    committed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_operations", x => x.id);
                    table.UniqueConstraint("AK_security_operations_id_organization_id", x => new { x.id, x.organization_id });
                    table.CheckConstraint("ck_security_operation", "revision > 0 AND ((kind = 'CredentialRevoke' AND application_id IS NOT NULL AND credential_id IS NOT NULL) OR kind = 'EnvironmentPolicyCutover')");
                    table.ForeignKey(
                        name: "FK_security_operations_application_credentials_credential_id_a~",
                        columns: x => new { x.credential_id, x.application_id, x.organization_id },
                        principalTable: "application_credentials",
                        principalColumns: new[] { "id", "application_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_operations_applications_application_id_organizatio~",
                        columns: x => new { x.application_id, x.organization_id },
                        principalTable: "applications",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_operations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_operations_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gateway_security_leases",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    checkpoint_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fresh_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_issued_lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    signing_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lease_seconds = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_security_leases", x => new { x.node_id, x.instance_id });
                    table.CheckConstraint("ck_gateway_security_lease", "epoch>=0 AND revision>0 AND lease_seconds BETWEEN 5 AND 30 AND last_issued_lease_until>=fresh_until AND fresh_until>issued_at AND fresh_until<=issued_at+make_interval(secs=>lease_seconds) AND instance_id<>'00000000-0000-0000-0000-000000000000'::uuid AND checkpoint_hash ~ '^[a-f0-9]{64}$'");
                    table.ForeignKey(
                        name: "FK_gateway_security_leases_environment_security_states_environ~",
                        columns: x => new { x.environment_id, x.security_generation },
                        principalTable: "environment_security_states",
                        principalColumns: new[] { "environment_id", "security_generation" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_leases_environments_environment_id_project~",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_leases_gateway_nodes_node_id_environment_id",
                        columns: x => new { x.node_id, x.environment_id },
                        principalTable: "gateway_nodes",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_leases_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_events",
                columns: table => new
                {
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_json = table.Column<string>(type: "jsonb", nullable: true),
                    previous_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_events", x => new { x.environment_id, x.security_generation, x.epoch });
                    table.UniqueConstraint("AK_security_events_operation_id_environment_id", x => new { x.operation_id, x.environment_id });
                    table.CheckConstraint("ck_security_event", "epoch > 0 AND length(previous_hash)=64 AND length(event_hash)=64 AND ((event_type='CredentialRevoked' AND credential_id IS NOT NULL AND policy_json IS NULL) OR (event_type='EnvironmentPolicyChanged' AND credential_id IS NULL AND policy_json IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_security_events_application_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "application_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_events_environment_security_states_environment_id_~",
                        columns: x => new { x.environment_id, x.security_generation },
                        principalTable: "environment_security_states",
                        principalColumns: new[] { "environment_id", "security_generation" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_events_environments_environment_id_project_id",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_events_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_events_security_operations_operation_id_organizati~",
                        columns: x => new { x.operation_id, x.organization_id },
                        principalTable: "security_operations",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gateway_security_current_receipts",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    checkpoint_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    head_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fresh_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_security_current_receipts", x => new { x.node_id, x.instance_id });
                    table.CheckConstraint("ck_current_security_receipt", "epoch>=0 AND checkpoint_hash ~ '^[a-f0-9]{64}$' AND head_hash ~ '^[a-f0-9]{64}$' AND fresh_until>received_at AND lease_id<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_gateway_security_current_receipts_environment_security_stat~",
                        columns: x => new { x.environment_id, x.security_generation },
                        principalTable: "environment_security_states",
                        principalColumns: new[] { "environment_id", "security_generation" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_current_receipts_environments_environment_~",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_current_receipts_gateway_nodes_node_id_env~",
                        columns: x => new { x.node_id, x.environment_id },
                        principalTable: "gateway_nodes",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_current_receipts_gateway_security_leases_n~",
                        columns: x => new { x.node_id, x.instance_id },
                        principalTable: "gateway_security_leases",
                        principalColumns: new[] { "node_id", "instance_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_current_receipts_projects_project_id_organ~",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "credential_revocations",
                columns: table => new
                {
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credential_revocations", x => new { x.environment_id, x.credential_id });
                    table.ForeignKey(
                        name: "FK_credential_revocations_application_credentials_credential_i~",
                        columns: x => new { x.credential_id, x.application_id, x.organization_id },
                        principalTable: "application_credentials",
                        principalColumns: new[] { "id", "application_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credential_revocations_environments_environment_id_project_~",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credential_revocations_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credential_revocations_security_events_environment_id_secur~",
                        columns: x => new { x.environment_id, x.security_generation, x.epoch },
                        principalTable: "security_events",
                        principalColumns: new[] { "environment_id", "security_generation", "epoch" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_credential_revocations_security_operations_operation_id_org~",
                        columns: x => new { x.operation_id, x.organization_id },
                        principalTable: "security_operations",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_operation_alerts",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    threshold_seconds = table.Column<int>(type: "integer", nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    operation_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pending_targets = table.Column<int>(type: "integer", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_operation_alerts", x => new { x.operation_id, x.environment_id, x.threshold_seconds });
                    table.CheckConstraint("ck_security_operation_alert", "threshold_seconds BETWEEN 1 AND 86400 AND pending_targets>=0 AND severity IN ('Warning','Escalation') AND operation_status IN ('Unconfirmed','Partial')");
                    table.ForeignKey(
                        name: "FK_security_operation_alerts_security_events_operation_id_envi~",
                        columns: x => new { x.operation_id, x.environment_id },
                        principalTable: "security_events",
                        principalColumns: new[] { "operation_id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_operation_targets",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_nonce = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    target_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    protocol_supported_at_freeze = table.Column<bool>(type: "boolean", nullable: false),
                    superseded_by_instance_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    last_lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_operation_targets", x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid });
                    table.CheckConstraint("ck_security_target", "target_state IN ('Pending','Applied','SafeSuperseded','AwaitingRegistration') AND ((instance_guid = '00000000-0000-0000-0000-000000000000'::uuid AND target_state IN ('AwaitingRegistration','SafeSuperseded')) OR (instance_guid<>'00000000-0000-0000-0000-000000000000'::uuid AND target_state<>'AwaitingRegistration'))");
                    table.ForeignKey(
                        name: "FK_security_operation_targets_gateway_nodes_node_id_environmen~",
                        columns: x => new { x.node_id, x.environment_id },
                        principalTable: "gateway_nodes",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_operation_targets_security_events_operation_id_env~",
                        columns: x => new { x.operation_id, x.environment_id },
                        principalTable: "security_events",
                        principalColumns: new[] { "operation_id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_operation_targets_security_operation_targets_opera~",
                        columns: x => new { x.operation_id, x.environment_id, x.node_id, x.superseded_by_instance_guid },
                        principalTable: "security_operation_targets",
                        principalColumns: new[] { "operation_id", "environment_id", "node_id", "instance_guid" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_security_outbox", "epoch > 0 AND attempts >= 0");
                    table.ForeignKey(
                        name: "FK_security_outbox_messages_security_events_environment_id_sec~",
                        columns: x => new { x.environment_id, x.security_generation, x.epoch },
                        principalTable: "security_events",
                        principalColumns: new[] { "environment_id", "security_generation", "epoch" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_outbox_messages_security_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "security_operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_ack_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_nonce = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_ack_attempts", x => x.id);
                    table.CheckConstraint("ck_security_ack_attempt", "request_hash ~ '^[a-f0-9]{64}$' AND error_code IN ('security_lkg_write_denied','security_material_invalid','security_generation_recovery_required','security_state_unavailable','security_full_checkpoint_required','security_epoch_conflict','security_tombstone_regression') AND challenge_nonce<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_security_ack_attempts_security_operation_targets_operation_~",
                        columns: x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid },
                        principalTable: "security_operation_targets",
                        principalColumns: new[] { "operation_id", "environment_id", "node_id", "instance_guid" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_acks",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    security_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    checkpoint_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    challenge_nonce = table.Column<Guid>(type: "uuid", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    durable = table.Column<bool>(type: "boolean", nullable: false),
                    admission_enforced = table.Column<bool>(type: "boolean", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_acks", x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid });
                    table.CheckConstraint("ck_security_ack", "epoch>0 AND checkpoint_hash ~ '^[a-f0-9]{64}$' AND success AND durable AND admission_enforced AND error_code IS NULL AND challenge_nonce<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_security_acks_security_operation_targets_operation_id_envir~",
                        columns: x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid },
                        principalTable: "security_operation_targets",
                        principalColumns: new[] { "operation_id", "environment_id", "node_id", "instance_guid" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_credential_revocations_credential_id_application_id_organiz~",
                table: "credential_revocations",
                columns: new[] { "credential_id", "application_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_credential_revocations_environment_id_project_id",
                table: "credential_revocations",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_credential_revocations_environment_id_security_generation_e~",
                table: "credential_revocations",
                columns: new[] { "environment_id", "security_generation", "epoch" });

            migrationBuilder.CreateIndex(
                name: "IX_credential_revocations_operation_id_organization_id",
                table: "credential_revocations",
                columns: new[] { "operation_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_credential_revocations_project_id_organization_id",
                table: "credential_revocations",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_environment_security_states_environment_id_project_id",
                table: "environment_security_states",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_environment_security_states_project_id_organization_id",
                table: "environment_security_states",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_current_receipts_environment_id_project_id",
                table: "gateway_security_current_receipts",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_current_receipts_environment_id_security_g~",
                table: "gateway_security_current_receipts",
                columns: new[] { "environment_id", "security_generation" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_current_receipts_lease_id",
                table: "gateway_security_current_receipts",
                column: "lease_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_current_receipts_node_id_environment_id",
                table: "gateway_security_current_receipts",
                columns: new[] { "node_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_current_receipts_project_id_organization_id",
                table: "gateway_security_current_receipts",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_leases_environment_id_fresh_until",
                table: "gateway_security_leases",
                columns: new[] { "environment_id", "fresh_until" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_leases_environment_id_project_id",
                table: "gateway_security_leases",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_leases_environment_id_security_generation",
                table: "gateway_security_leases",
                columns: new[] { "environment_id", "security_generation" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_leases_lease_id",
                table: "gateway_security_leases",
                column: "lease_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_leases_node_id_environment_id",
                table: "gateway_security_leases",
                columns: new[] { "node_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_leases_project_id_organization_id",
                table: "gateway_security_leases",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_security_ack_attempts_operation_id_environment_id_node_id_i~",
                table: "security_ack_attempts",
                columns: new[] { "operation_id", "environment_id", "node_id", "instance_guid", "request_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_events_credential_id",
                table: "security_events",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_security_events_environment_id_project_id",
                table: "security_events",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_security_events_operation_id_organization_id",
                table: "security_events",
                columns: new[] { "operation_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_security_events_project_id_organization_id",
                table: "security_events",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_security_operation_alerts_observed_at",
                table: "security_operation_alerts",
                column: "observed_at");

            migrationBuilder.CreateIndex(
                name: "IX_security_operation_targets_challenge_nonce",
                table: "security_operation_targets",
                column: "challenge_nonce",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_operation_targets_node_id_environment_id",
                table: "security_operation_targets",
                columns: new[] { "node_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_security_operation_targets_operation_id_environment_id_node~",
                table: "security_operation_targets",
                columns: new[] { "operation_id", "environment_id", "node_id", "superseded_by_instance_guid" });

            migrationBuilder.CreateIndex(
                name: "IX_security_operations_application_id_organization_id",
                table: "security_operations",
                columns: new[] { "application_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_security_operations_credential_id",
                table: "security_operations",
                column: "credential_id",
                unique: true,
                filter: "kind = 'CredentialRevoke'");

            migrationBuilder.CreateIndex(
                name: "IX_security_operations_credential_id_application_id_organizati~",
                table: "security_operations",
                columns: new[] { "credential_id", "application_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_security_operations_organization_id",
                table: "security_operations",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_security_operations_requested_by",
                table: "security_operations",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "IX_security_outbox_messages_environment_id_security_generation~",
                table: "security_outbox_messages",
                columns: new[] { "environment_id", "security_generation", "epoch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_outbox_messages_operation_id",
                table: "security_outbox_messages",
                column: "operation_id");

            migrationBuilder.CreateIndex(
                name: "IX_security_outbox_messages_processed_at_lease_until_created_at",
                table: "security_outbox_messages",
                columns: new[] { "processed_at", "lease_until", "created_at" },
                filter: "processed_at IS NULL");

            Governance24SecurityGuards.Install(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
            migrationBuilder.DropTable(
                name: "credential_revocations");

            migrationBuilder.DropTable(
                name: "gateway_security_current_receipts");

            migrationBuilder.DropTable(
                name: "security_ack_attempts");

            migrationBuilder.DropTable(
                name: "security_acks");

            migrationBuilder.DropTable(
                name: "security_operation_alerts");

            migrationBuilder.DropTable(
                name: "security_outbox_messages");

            migrationBuilder.DropTable(
                name: "gateway_security_leases");

            migrationBuilder.DropTable(
                name: "security_operation_targets");

            migrationBuilder.DropTable(
                name: "security_events");

            migrationBuilder.DropTable(
                name: "environment_security_states");

            migrationBuilder.DropTable(
                name: "security_operations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_gateway_nodes_id_environment_id",
                table: "gateway_nodes");
        }
    }
}

using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "varchar(128)", nullable: false),
                    resource_type = table.Column<string>(type: "varchar(64)", nullable: false),
                    resource_id = table.Column<string>(type: "varchar(128)", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true),
                    ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    trace_id = table.Column<string>(type: "varchar(64)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    code = table.Column<string>(type: "varchar(64)", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    code = table.Column<string>(type: "varchar(128)", nullable: false),
                    module = table.Column<string>(type: "varchar(64)", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    username = table.Column<string>(type: "varchar(128)", nullable: false),
                    display_name = table.Column<string>(type: "varchar(128)", nullable: false),
                    email = table.Column<string>(type: "varchar(256)", nullable: true),
                    password_hash = table.Column<string>(type: "varchar(256)", nullable: true),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    auth_source = table.Column<string>(type: "varchar(24)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    security_stamp = table.Column<string>(type: "varchar(128)", nullable: false, defaultValueSql: "gen_random_uuid()::text"),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_flows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    scope_type = table.Column<string>(type: "varchar(32)", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_flows", x => x.id);
                    table.ForeignKey(
                        name: "FK_approval_flows_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "varchar(64)", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_roles_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_key = table.Column<string>(type: "varchar(256)", nullable: false),
                    operation = table.Column<string>(type: "varchar(128)", nullable: false),
                    key = table.Column<string>(type: "varchar(128)", nullable: false),
                    request_hash = table.Column<string>(type: "varchar(128)", nullable: false),
                    response_bytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_idempotency_records_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "varchar(64)", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                    table.UniqueConstraint("AK_projects_id_organization_id", x => new { x.id, x.organization_id });
                    table.ForeignKey(
                        name: "FK_projects_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_projects_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "approval_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "int", nullable: false),
                    role_code = table.Column<string>(type: "varchar(64)", nullable: false),
                    required_count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_steps", x => x.id);
                    table.ForeignKey(
                        name: "FK_approval_steps_approval_flows_flow_id",
                        column: x => x.flow_id,
                        principalTable: "approval_flows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.role_id, x.permission_id });
                    table.ForeignKey(
                        name: "FK_role_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sort_order = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_groups", x => x.id);
                    table.UniqueConstraint("AK_api_groups_id_project_id", x => new { x.id, x.project_id });
                    table.ForeignKey(
                        name: "FK_api_groups_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "varchar(64)", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    owner = table.Column<string>(type: "varchar(128)", nullable: false),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.id);
                    table.ForeignKey(
                        name: "FK_applications_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_applications_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "environments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "varchar(32)", nullable: false),
                    name = table.Column<string>(type: "varchar(64)", nullable: false),
                    is_production = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    release_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    desired_config_version = table.Column<long>(type: "bigint", nullable: true),
                    deployment_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environments", x => x.id);
                    table.UniqueConstraint("AK_environments_id_project_id", x => new { x.id, x.project_id });
                    table.CheckConstraint("ck_deployment_sequence", "deployment_sequence >= 0");
                    table.ForeignKey(
                        name: "FK_environments_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    type = table.Column<string>(type: "varchar(48)", nullable: false),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    version_no = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policies", x => x.id);
                    table.ForeignKey(
                        name: "FK_policies_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_policies_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "apis",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "varchar(64)", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    lifecycle_status = table.Column<string>(type: "varchar(24)", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version_no = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apis", x => x.id);
                    table.ForeignKey(
                        name: "FK_apis_api_groups_group_id_project_id",
                        columns: x => new { x.group_id, x.project_id },
                        principalTable: "api_groups",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_apis_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_apis_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_apis_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "application_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_key = table.Column<string>(type: "varchar(128)", nullable: false),
                    secret_hash = table.Column<string>(type: "varchar(256)", nullable: false),
                    secret_last4 = table.Column<string>(type: "varchar(8)", nullable: false),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_credentials", x => x.id);
                    table.CheckConstraint("ck_credential_window", "expires_at > valid_from");
                    table.ForeignKey(
                        name: "FK_application_credentials_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gateway_config_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    snapshot_key = table.Column<string>(type: "varchar(256)", nullable: true),
                    snapshot_hash = table.Column<string>(type: "varchar(128)", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_config_versions", x => x.id);
                    table.CheckConstraint("ck_config_version", "version_no > 0");
                    table.ForeignKey(
                        name: "FK_gateway_config_versions_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_config_versions_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gateway_nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_name = table.Column<string>(type: "varchar(128)", nullable: false),
                    instance_id = table.Column<string>(type: "varchar(128)", nullable: false),
                    app_version = table.Column<string>(type: "varchar(64)", nullable: false),
                    current_config_version = table.Column<long>(type: "bigint", nullable: false),
                    target_config_version = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    current_deployment_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    identity_hash = table.Column<string>(type: "varchar(256)", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_nodes", x => x.id);
                    table.ForeignKey(
                        name: "FK_gateway_nodes_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_no = table.Column<string>(type: "varchar(64)", nullable: false),
                    from_config_version = table.Column<long>(type: "bigint", nullable: false),
                    to_config_version = table.Column<long>(type: "bigint", nullable: false),
                    release_type = table.Column<string>(type: "varchar(24)", nullable: false),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    rollback_of = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_sequence = table.Column<long>(type: "bigint", nullable: true, defaultValue: 0L),
                    baseline_config_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    candidate_bytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    approval_policy = table.Column<string>(type: "jsonb", nullable: true),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    failure_code = table.Column<string>(type: "varchar(128)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_release_records_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_records_release_records_rollback_of",
                        column: x => x.rollback_of,
                        principalTable: "release_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_records_users_approved_by",
                        column: x => x.approved_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_records_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "upstream_clusters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    load_balancing_policy = table.Column<string>(type: "varchar(32)", nullable: false),
                    health_check_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    health_check_path = table.Column<string>(type: "varchar(256)", nullable: false),
                    health_check_interval_sec = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upstream_clusters", x => x.id);
                    table.UniqueConstraint("AK_upstream_clusters_id_environment_id", x => new { x.id, x.environment_id });
                    table.ForeignKey(
                        name: "FK_upstream_clusters_environments_environment_id_project_id",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_upstream_clusters_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_project_scopes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    access_mode = table.Column<string>(type: "varchar(16)", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_project_scopes", x => x.id);
                    table.CheckConstraint("ck_scope_environment_requires_project", "environment_id IS NULL OR project_id IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_user_project_scopes_environments_environment_id_project_id",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_project_scopes_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_project_scopes_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_project_scopes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    api_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "varchar(32)", nullable: false),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    openapi_document = table.Column<string>(type: "jsonb", nullable: true),
                    schema_hash = table.Column<string>(type: "varchar(128)", nullable: true),
                    change_type = table.Column<string>(type: "varchar(24)", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    openapi_source = table.Column<string>(type: "text", nullable: true),
                    source_format = table.Column<string>(type: "varchar(16)", nullable: true),
                    sealed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_versions_apis_api_id",
                        column: x => x.api_id,
                        principalTable: "apis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_versions_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "application_api_permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_api_permissions", x => x.id);
                    table.CheckConstraint("ck_grant_window", "expires_at IS NULL OR expires_at > valid_from");
                    table.ForeignKey(
                        name: "FK_application_api_permissions_apis_api_id",
                        column: x => x.api_id,
                        principalTable: "apis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_api_permissions_applications_application_id",
                        column: x => x.application_id,
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_api_permissions_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_api_permissions_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gateway_config_snapshots",
                columns: table => new
                {
                    config_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    payload_bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_config_snapshots", x => x.config_version_id);
                    table.ForeignKey(
                        name: "FK_gateway_config_snapshots_gateway_config_versions_config_ver~",
                        column: x => x.config_version_id,
                        principalTable: "gateway_config_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gateway_node_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    gateway_node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "varchar(64)", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_node_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_gateway_node_events_gateway_nodes_gateway_node_id",
                        column: x => x.gateway_node_id,
                        principalTable: "gateway_nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "approval_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    flow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "int", nullable: false),
                    assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    acted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_tasks", x => x.id);
                    table.ForeignKey(
                        name: "FK_approval_tasks_approval_flows_flow_id",
                        column: x => x.flow_id,
                        principalTable: "approval_flows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_approval_tasks_release_records_release_id",
                        column: x => x.release_id,
                        principalTable: "release_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_approval_tasks_users_assignee_user_id",
                        column: x => x.assignee_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    event_type = table.Column<string>(type: "varchar(128)", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_outbox_messages_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_outbox_messages_release_records_release_id",
                        column: x => x.release_id,
                        principalTable: "release_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "varchar(32)", nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_type = table.Column<string>(type: "varchar(24)", nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_release_items_release_records_release_id",
                        column: x => x.release_id,
                        principalTable: "release_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_targets",
                columns: table => new
                {
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<string>(type: "varchar(128)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_targets", x => new { x.release_id, x.node_id });
                    table.ForeignKey(
                        name: "FK_release_targets_gateway_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "gateway_nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_targets_release_records_release_id",
                        column: x => x.release_id,
                        principalTable: "release_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "upstream_destinations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    cluster_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    address = table.Column<string>(type: "varchar(1024)", nullable: false),
                    weight = table.Column<int>(type: "int", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upstream_destinations", x => x.id);
                    table.ForeignKey(
                        name: "FK_upstream_destinations_upstream_clusters_cluster_id",
                        column: x => x.cluster_id,
                        principalTable: "upstream_clusters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_parameters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    api_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location = table.Column<string>(type: "varchar(16)", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    data_type = table.Column<string>(type: "varchar(32)", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    schema = table.Column<string>(type: "jsonb", nullable: true),
                    description = table.Column<string>(type: "text", nullable: true),
                    example_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_parameters", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_parameters_api_versions_api_version_id",
                        column: x => x.api_version_id,
                        principalTable: "api_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_routes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    api_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route_name = table.Column<string>(type: "varchar(128)", nullable: false),
                    path = table.Column<string>(type: "varchar(512)", nullable: false),
                    normalized_path = table.Column<string>(type: "varchar(512)", nullable: false),
                    methods = table.Column<string[]>(type: "varchar[]", nullable: false),
                    cluster_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<int>(type: "int", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    timeout_ms = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_routes", x => x.id);
                    table.UniqueConstraint("AK_api_routes_id_environment_id", x => new { x.id, x.environment_id });
                    table.CheckConstraint("ck_route_methods_timeout", "cardinality(methods) > 0 AND timeout_ms > 0");
                    table.ForeignKey(
                        name: "FK_api_routes_api_versions_api_version_id",
                        column: x => x.api_version_id,
                        principalTable: "api_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_routes_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_api_routes_upstream_clusters_cluster_id_environment_id",
                        columns: x => new { x.cluster_id, x.environment_id },
                        principalTable: "upstream_clusters",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "api_schemas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    api_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schema_type = table.Column<string>(type: "varchar(32)", nullable: false),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    status_code = table.Column<int>(type: "int", nullable: true),
                    content_type = table.Column<string>(type: "varchar(128)", nullable: false),
                    schema_json = table.Column<string>(type: "jsonb", nullable: false),
                    schema_hash = table.Column<string>(type: "varchar(128)", nullable: true),
                    example_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_schemas", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_schemas_api_versions_api_version_id",
                        column: x => x.api_version_id,
                        principalTable: "api_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gateway_acks",
                columns: table => new
                {
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<string>(type: "varchar(128)", nullable: false),
                    config_version = table.Column<long>(type: "bigint", nullable: false),
                    deployment_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    payload_hash = table.Column<string>(type: "varchar(128)", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    success = table.Column<bool>(type: "boolean", nullable: false),
                    error_code = table.Column<string>(type: "varchar(128)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_acks", x => new { x.release_id, x.node_id });
                    table.ForeignKey(
                        name: "FK_gateway_acks_gateway_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "gateway_nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_acks_release_records_release_id",
                        column: x => x.release_id,
                        principalTable: "release_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_acks_release_targets_release_id_node_id",
                        columns: x => new { x.release_id, x.node_id },
                        principalTable: "release_targets",
                        principalColumns: new[] { "release_id", "node_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "route_methods",
                columns: table => new
                {
                    route_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "varchar(16)", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_path = table.Column<string>(type: "varchar(512)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_route_methods", x => new { x.route_id, x.method });
                    table.ForeignKey(
                        name: "FK_route_methods_api_routes_route_id_environment_id",
                        columns: x => new { x.route_id, x.environment_id },
                        principalTable: "api_routes",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "route_policy_bindings",
                columns: table => new
                {
                    route_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_route_policy_bindings", x => new { x.route_id, x.policy_id });
                    table.ForeignKey(
                        name: "FK_route_policy_bindings_api_routes_route_id",
                        column: x => x.route_id,
                        principalTable: "api_routes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_route_policy_bindings_policies_policy_id",
                        column: x => x.policy_id,
                        principalTable: "policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_groups_project_id",
                table: "api_groups",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_parameters_api_version_id",
                table: "api_parameters",
                column: "api_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_routes_api_version_id",
                table: "api_routes",
                column: "api_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_routes_cluster_id_environment_id",
                table: "api_routes",
                columns: new[] { "cluster_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_api_routes_environment_id",
                table: "api_routes",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_schemas_api_version_id",
                table: "api_schemas",
                column: "api_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_api_versions_api_id_version",
                table: "api_versions",
                columns: new[] { "api_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_versions_created_by",
                table: "api_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_apis_group_id_project_id",
                table: "apis",
                columns: new[] { "group_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_apis_organization_id",
                table: "apis",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_apis_owner_user_id",
                table: "apis",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_apis_project_id_code",
                table: "apis",
                columns: new[] { "project_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_apis_project_id_organization_id",
                table: "apis",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_api_id",
                table: "application_api_permissions",
                column: "api_id");

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_application_id_api_id_environme~",
                table: "application_api_permissions",
                columns: new[] { "application_id", "api_id", "environment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_created_by",
                table: "application_api_permissions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_application_api_permissions_environment_id",
                table: "application_api_permissions",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_application_credentials_access_key",
                table: "application_credentials",
                column: "access_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_application_credentials_application_id",
                table: "application_credentials",
                column: "application_id");

            migrationBuilder.CreateIndex(
                name: "IX_applications_organization_id_code",
                table: "applications",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_applications_project_id_organization_id",
                table: "applications",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_flows_organization_id",
                table: "approval_flows",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_approval_steps_flow_id_step_order",
                table: "approval_steps",
                columns: new[] { "flow_id", "step_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_approval_tasks_assignee_user_id",
                table: "approval_tasks",
                column: "assignee_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_approval_tasks_flow_id",
                table: "approval_tasks",
                column: "flow_id");

            migrationBuilder.CreateIndex(
                name: "IX_approval_tasks_release_id",
                table: "approval_tasks",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_environment_id_created_at",
                table: "audit_logs",
                columns: new[] { "environment_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_environments_project_id_code",
                table: "environments",
                columns: new[] { "project_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_acks_node_id",
                table: "gateway_acks",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_config_versions_created_by",
                table: "gateway_config_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_config_versions_environment_id_version_no",
                table: "gateway_config_versions",
                columns: new[] { "environment_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gateway_node_events_gateway_node_id",
                table: "gateway_node_events",
                column: "gateway_node_id");

            migrationBuilder.CreateIndex(
                name: "IX_gateway_nodes_environment_id_enabled",
                table: "gateway_nodes",
                columns: new[] { "environment_id", "enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_nodes_node_name",
                table: "gateway_nodes",
                column: "node_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_actor_id_scope_key_operation_key",
                table: "idempotency_records",
                columns: new[] { "actor_id", "scope_key", "operation", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organizations_code",
                table: "organizations",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_environment_id",
                table: "outbox_messages",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_processed_at_lease_until",
                table: "outbox_messages",
                columns: new[] { "processed_at", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_release_id",
                table: "outbox_messages",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "IX_permissions_code",
                table: "permissions",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policies_organization_id",
                table: "policies",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_policies_project_id_organization_id",
                table: "policies",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_projects_organization_id_code",
                table: "projects",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_owner_user_id",
                table: "projects",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_release_items_release_id",
                table: "release_items",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "IX_release_records_approved_by",
                table: "release_records",
                column: "approved_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_records_environment_id_status",
                table: "release_records",
                columns: new[] { "environment_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_release_records_release_no",
                table: "release_records",
                column: "release_no",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_records_requested_by",
                table: "release_records",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_records_rollback_of",
                table: "release_records",
                column: "rollback_of");

            migrationBuilder.CreateIndex(
                name: "IX_release_targets_node_id",
                table: "release_targets",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_permission_id",
                table: "role_permissions",
                column: "permission_id");

            migrationBuilder.CreateIndex(
                name: "IX_roles_code",
                table: "roles",
                column: "code",
                unique: true,
                filter: "organization_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_roles_organization_id_code",
                table: "roles",
                columns: new[] { "organization_id", "code" },
                unique: true,
                filter: "organization_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_route_methods_environment_id_method_normalized_path",
                table: "route_methods",
                columns: new[] { "environment_id", "method", "normalized_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_route_methods_route_id_environment_id",
                table: "route_methods",
                columns: new[] { "route_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_route_policy_bindings_policy_id",
                table: "route_policy_bindings",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "IX_upstream_clusters_environment_id_name",
                table: "upstream_clusters",
                columns: new[] { "environment_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_upstream_clusters_environment_id_project_id",
                table: "upstream_clusters",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_upstream_clusters_project_id",
                table: "upstream_clusters",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_upstream_destinations_cluster_id",
                table: "upstream_destinations",
                column: "cluster_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_project_scopes_environment_id_project_id",
                table: "user_project_scopes",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_project_scopes_organization_id",
                table: "user_project_scopes",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_project_scopes_project_id_organization_id",
                table: "user_project_scopes",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_project_scopes_user_id",
                table: "user_project_scopes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_role_id",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_parameters");

            migrationBuilder.DropTable(
                name: "api_schemas");

            migrationBuilder.DropTable(
                name: "application_api_permissions");

            migrationBuilder.DropTable(
                name: "application_credentials");

            migrationBuilder.DropTable(
                name: "approval_steps");

            migrationBuilder.DropTable(
                name: "approval_tasks");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "gateway_acks");

            migrationBuilder.DropTable(
                name: "gateway_config_snapshots");

            migrationBuilder.DropTable(
                name: "gateway_node_events");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "release_items");

            migrationBuilder.DropTable(
                name: "role_permissions");

            migrationBuilder.DropTable(
                name: "route_methods");

            migrationBuilder.DropTable(
                name: "route_policy_bindings");

            migrationBuilder.DropTable(
                name: "upstream_destinations");

            migrationBuilder.DropTable(
                name: "user_project_scopes");

            migrationBuilder.DropTable(
                name: "user_roles");

            migrationBuilder.DropTable(
                name: "applications");

            migrationBuilder.DropTable(
                name: "approval_flows");

            migrationBuilder.DropTable(
                name: "release_targets");

            migrationBuilder.DropTable(
                name: "gateway_config_versions");

            migrationBuilder.DropTable(
                name: "permissions");

            migrationBuilder.DropTable(
                name: "api_routes");

            migrationBuilder.DropTable(
                name: "policies");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "gateway_nodes");

            migrationBuilder.DropTable(
                name: "release_records");

            migrationBuilder.DropTable(
                name: "api_versions");

            migrationBuilder.DropTable(
                name: "upstream_clusters");

            migrationBuilder.DropTable(
                name: "apis");

            migrationBuilder.DropTable(
                name: "environments");

            migrationBuilder.DropTable(
                name: "api_groups");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}

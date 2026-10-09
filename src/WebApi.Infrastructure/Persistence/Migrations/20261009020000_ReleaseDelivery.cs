using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "artifact_id",
                table: "release_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "promotion_id",
                table: "release_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_release_id",
                table: "release_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_applications_id_organization_id",
                table: "applications",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.CreateTable(
                name: "project_delivery_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "Legacy"),
                    required_test_types = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "ARRAY['InterfaceFunction','Integration','ContractCompatibility']::text[]"),
                    verification_validity_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 1440),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_delivery_policies", x => x.id);
                    table.CheckConstraint("ck_delivery_policy", "mode IN ('Legacy','PromotionRequired') AND source_environment_id <> target_environment_id AND verification_validity_minutes BETWEEN 1 AND 10080 AND revision >= 1 AND cardinality(required_test_types) BETWEEN 1 AND 3 AND required_test_types <@ ARRAY['InterfaceFunction','Integration','ContractCompatibility']::text[]");
                    table.ForeignKey(
                        name: "FK_project_delivery_policies_environments_source_environment_i~",
                        columns: x => new { x.source_environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_delivery_policies_environments_target_environment_i~",
                        columns: x => new { x.target_environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_delivery_policies_projects_project_id_organization_~",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_delivery_policies_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_content = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    artifact_hash = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    source_snapshot_hash = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_artifacts", x => x.id);
                    table.UniqueConstraint("AK_release_artifacts_id_project_id", x => new { x.id, x.project_id });
                    table.UniqueConstraint("AK_release_artifacts_id_project_id_source_environment_id", x => new { x.id, x.project_id, x.source_environment_id });
                    table.UniqueConstraint("AK_release_artifacts_id_project_id_source_environment_id_sourc~", x => new { x.id, x.project_id, x.source_environment_id, x.source_release_id });
                    table.ForeignKey(
                        name: "FK_release_artifacts_environments_source_environment_id_projec~",
                        columns: x => new { x.source_environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_artifacts_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_artifacts_release_records_source_release_id_source_~",
                        columns: x => new { x.source_release_id, x.source_environment_id },
                        principalTable: "release_records",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_artifacts_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_test_acceptances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verification_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "ARRAY[]::uuid[]"),
                    evidence_hash = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "Requested"),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    acted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    acted_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_test_acceptances", x => x.id);
                    table.UniqueConstraint("AK_release_test_acceptances_id_project_id_source_environment_id", x => new { x.id, x.project_id, x.source_environment_id });
                    table.CheckConstraint("ck_test_acceptance", "status IN ('Requested','Accepted','Rejected','Revoked') AND revision >= 1 AND (acted_by IS NULL OR acted_by <> requested_by)");
                    table.ForeignKey(
                        name: "FK_release_test_acceptances_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_test_acceptances_release_artifacts_artifact_id_proj~",
                        columns: x => new { x.artifact_id, x.project_id, x.source_environment_id },
                        principalTable: "release_artifacts",
                        principalColumns: new[] { "id", "project_id", "source_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_test_acceptances_users_acted_by",
                        column: x => x.acted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_test_acceptances_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_promotions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acceptance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "Draft"),
                    baseline_config_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    mapping_revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    candidate_hash = table.Column<string>(type: "varchar(64)", nullable: true),
                    resource_revisions_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    frozen_policy_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    precheck_json = table.Column<string>(type: "jsonb", nullable: true),
                    target_access_address_revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    verification_context_json = table.Column<string>(type: "jsonb", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_promotions", x => x.id);
                    table.UniqueConstraint("AK_release_promotions_id_artifact_id_source_release_id_target_~", x => new { x.id, x.artifact_id, x.source_release_id, x.target_environment_id });
                    table.UniqueConstraint("AK_release_promotions_id_project_id_target_environment_id", x => new { x.id, x.project_id, x.target_environment_id });
                    table.CheckConstraint("ck_promotion_state", "source_environment_id <> target_environment_id AND revision >= 1 AND mapping_revision >= 0 AND status IN ('Draft','WaitingApproval','Ready','Deploying','Verifying','Completed','Rejected','Cancelled','Invalidated','DeploymentFailed','VerificationFailed','RolledBack')");
                    table.ForeignKey(
                        name: "FK_release_promotions_environments_target_environment_id_proje~",
                        columns: x => new { x.target_environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotions_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotions_release_artifacts_artifact_id_project_id~",
                        columns: x => new { x.artifact_id, x.project_id, x.source_environment_id, x.source_release_id },
                        principalTable: "release_artifacts",
                        principalColumns: new[] { "id", "project_id", "source_environment_id", "source_release_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotions_release_records_target_release_id_target~",
                        columns: x => new { x.target_release_id, x.target_environment_id },
                        principalTable: "release_records",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotions_release_test_acceptances_acceptance_id_p~",
                        columns: x => new { x.acceptance_id, x.project_id, x.source_environment_id },
                        principalTable: "release_test_acceptances",
                        principalColumns: new[] { "id", "project_id", "source_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotions_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_promotion_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_key = table.Column<string>(type: "varchar(512)", nullable: false, defaultValue: ""),
                    kind = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: ""),
                    target_route_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cluster_id = table.Column<Guid>(type: "uuid", nullable: true),
                    application_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_promotion_mappings", x => x.id);
                    table.ForeignKey(
                        name: "FK_release_promotion_mappings_api_routes_target_route_id_targe~",
                        columns: x => new { x.target_route_id, x.target_environment_id },
                        principalTable: "api_routes",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotion_mappings_applications_application_id_orga~",
                        columns: x => new { x.application_id, x.organization_id },
                        principalTable: "applications",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotion_mappings_projects_project_id_organization~",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotion_mappings_release_promotions_promotion_id_~",
                        columns: x => new { x.promotion_id, x.project_id, x.target_environment_id },
                        principalTable: "release_promotions",
                        principalColumns: new[] { "id", "project_id", "target_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotion_mappings_upstream_clusters_cluster_id_tar~",
                        columns: x => new { x.cluster_id, x.target_environment_id },
                        principalTable: "upstream_clusters",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "verification_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    storage_key = table.Column<string>(type: "varchar(128)", nullable: false, defaultValue: ""),
                    content_type = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_verification_reports", x => x.id);
                    table.UniqueConstraint("AK_verification_reports_id_project_id_environment_id", x => new { x.id, x.project_id, x.environment_id });
                    table.CheckConstraint("ck_report", "size_bytes BETWEEN 1 AND 10485760 AND content_type IN ('application/pdf','text/plain') AND (artifact_id IS NOT NULL)::int + (promotion_id IS NOT NULL)::int = 1");
                    table.ForeignKey(
                        name: "FK_verification_reports_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_verification_reports_release_artifacts_artifact_id_project_~",
                        columns: x => new { x.artifact_id, x.project_id, x.environment_id },
                        principalTable: "release_artifacts",
                        principalColumns: new[] { "id", "project_id", "source_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_verification_reports_release_promotions_promotion_id_projec~",
                        columns: x => new { x.promotion_id, x.project_id, x.environment_id },
                        principalTable: "release_promotions",
                        principalColumns: new[] { "id", "project_id", "target_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_verification_reports_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_verifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    config_version = table.Column<long>(type: "bigint", nullable: false),
                    deployment_sequence = table.Column<long>(type: "bigint", nullable: false),
                    snapshot_hash = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    access_address_revision = table.Column<long>(type: "bigint", nullable: false),
                    access_context_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    phase = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "SourceTest"),
                    type = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: ""),
                    result = table.Column<string>(type: "varchar(16)", nullable: false, defaultValue: ""),
                    is_manual = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    report_id = table.Column<Guid>(type: "uuid", nullable: true),
                    report_hash = table.Column<string>(type: "varchar(64)", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_verifications", x => x.id);
                    table.UniqueConstraint("AK_release_verifications_id_project_id_environment_id", x => new { x.id, x.project_id, x.environment_id });
                    table.CheckConstraint("ck_verification_fact", "phase IN ('SourceTest','Production') AND result IN ('Passed','Failed') AND is_manual AND config_version > 0 AND deployment_sequence > 0 AND access_address_revision >= 1 AND started_at <= finished_at AND finished_at <= created_at AND expires_at > finished_at AND ((phase='SourceTest' AND promotion_id IS NULL AND type IN ('InterfaceFunction','Integration','ContractCompatibility')) OR (phase='Production' AND promotion_id IS NOT NULL AND type IN ('EntryConnectivity','AuthenticationAuthorization','CriticalBusinessCall')))");
                    table.ForeignKey(
                        name: "FK_release_verifications_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_verifications_release_artifacts_artifact_id_project~",
                        columns: x => new { x.artifact_id, x.project_id },
                        principalTable: "release_artifacts",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_verifications_release_promotions_promotion_id_proje~",
                        columns: x => new { x.promotion_id, x.project_id, x.environment_id },
                        principalTable: "release_promotions",
                        principalColumns: new[] { "id", "project_id", "target_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_verifications_release_records_release_id_environmen~",
                        columns: x => new { x.release_id, x.environment_id },
                        principalTable: "release_records",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_verifications_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_verifications_verification_reports_report_id_projec~",
                        columns: x => new { x.report_id, x.project_id, x.environment_id },
                        principalTable: "verification_reports",
                        principalColumns: new[] { "id", "project_id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_promotion_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acceptance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phase = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: ""),
                    from_status = table.Column<string>(type: "varchar(32)", nullable: true),
                    to_status = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: ""),
                    reason_code = table.Column<string>(type: "varchar(128)", nullable: false, defaultValue: ""),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verification_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_promotion_events", x => x.id);
                    table.CheckConstraint("ck_delivery_event_owner", "(promotion_id IS NOT NULL)::int + (acceptance_id IS NOT NULL)::int = 1");
                    table.ForeignKey(
                        name: "FK_release_promotion_events_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotion_events_release_promotions_promotion_id_pr~",
                        columns: x => new { x.promotion_id, x.project_id, x.environment_id },
                        principalTable: "release_promotions",
                        principalColumns: new[] { "id", "project_id", "target_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotion_events_release_records_release_id_environ~",
                        columns: x => new { x.release_id, x.environment_id },
                        principalTable: "release_records",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotion_events_release_test_acceptances_acceptanc~",
                        columns: x => new { x.acceptance_id, x.project_id, x.environment_id },
                        principalTable: "release_test_acceptances",
                        principalColumns: new[] { "id", "project_id", "source_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotion_events_release_verifications_verification~",
                        columns: x => new { x.verification_id, x.project_id, x.environment_id },
                        principalTable: "release_verifications",
                        principalColumns: new[] { "id", "project_id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_promotion_events_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_release_records_artifact_id",
                table: "release_records",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "IX_release_records_promotion_id",
                table: "release_records",
                column: "promotion_id",
                unique: true,
                filter: "promotion_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_release_records_promotion_id_artifact_id_source_release_id_~",
                table: "release_records",
                columns: new[] { "promotion_id", "artifact_id", "source_release_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_records_source_release_id",
                table: "release_records",
                column: "source_release_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_formal_promotion_link",
                table: "release_records",
                sql: "promotion_id IS NULL OR (artifact_id IS NOT NULL AND source_release_id IS NOT NULL AND recovery_of IS NULL AND rollback_of IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_project_delivery_policies_project_id",
                table: "project_delivery_policies",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_delivery_policies_project_id_organization_id",
                table: "project_delivery_policies",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_delivery_policies_source_environment_id_project_id",
                table: "project_delivery_policies",
                columns: new[] { "source_environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_delivery_policies_target_environment_id_project_id",
                table: "project_delivery_policies",
                columns: new[] { "target_environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_project_delivery_policies_updated_by",
                table: "project_delivery_policies",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_artifacts_created_by",
                table: "release_artifacts",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_artifacts_project_id_organization_id",
                table: "release_artifacts",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_artifacts_source_environment_id_project_id",
                table: "release_artifacts",
                columns: new[] { "source_environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_artifacts_source_release_id_artifact_hash",
                table: "release_artifacts",
                columns: new[] { "source_release_id", "artifact_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_artifacts_source_release_id_source_environment_id",
                table: "release_artifacts",
                columns: new[] { "source_release_id", "source_environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_events_acceptance_id_project_id_environme~",
                table: "release_promotion_events",
                columns: new[] { "acceptance_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_events_actor_id",
                table: "release_promotion_events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_events_project_id_organization_id",
                table: "release_promotion_events",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_events_promotion_id_project_id_environmen~",
                table: "release_promotion_events",
                columns: new[] { "promotion_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_events_release_id_environment_id",
                table: "release_promotion_events",
                columns: new[] { "release_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_events_verification_id_project_id_environ~",
                table: "release_promotion_events",
                columns: new[] { "verification_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_mappings_application_id_organization_id",
                table: "release_promotion_mappings",
                columns: new[] { "application_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_mappings_cluster_id_target_environment_id",
                table: "release_promotion_mappings",
                columns: new[] { "cluster_id", "target_environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_mappings_project_id_organization_id",
                table: "release_promotion_mappings",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_mappings_promotion_id_kind_resource_key",
                table: "release_promotion_mappings",
                columns: new[] { "promotion_id", "kind", "resource_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_mappings_promotion_id_project_id_target_e~",
                table: "release_promotion_mappings",
                columns: new[] { "promotion_id", "project_id", "target_environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotion_mappings_target_route_id_target_environme~",
                table: "release_promotion_mappings",
                columns: new[] { "target_route_id", "target_environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotions_acceptance_id_project_id_source_environm~",
                table: "release_promotions",
                columns: new[] { "acceptance_id", "project_id", "source_environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotions_artifact_id_project_id_source_environmen~",
                table: "release_promotions",
                columns: new[] { "artifact_id", "project_id", "source_environment_id", "source_release_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotions_project_id_created_at_id",
                table: "release_promotions",
                columns: new[] { "project_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotions_project_id_organization_id",
                table: "release_promotions",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotions_requested_by",
                table: "release_promotions",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_promotions_target_environment_id_project_id",
                table: "release_promotions",
                columns: new[] { "target_environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotions_target_release_id_target_environment_id",
                table: "release_promotions",
                columns: new[] { "target_release_id", "target_environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_test_acceptances_acted_by",
                table: "release_test_acceptances",
                column: "acted_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_test_acceptances_artifact_id_project_id_source_envi~",
                table: "release_test_acceptances",
                columns: new[] { "artifact_id", "project_id", "source_environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_test_acceptances_project_id_organization_id",
                table: "release_test_acceptances",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_test_acceptances_requested_by",
                table: "release_test_acceptances",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_verifications_artifact_id_phase_type_created_at",
                table: "release_verifications",
                columns: new[] { "artifact_id", "phase", "type", "created_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_release_verifications_artifact_id_project_id",
                table: "release_verifications",
                columns: new[] { "artifact_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_verifications_created_by",
                table: "release_verifications",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_verifications_project_id_organization_id",
                table: "release_verifications",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_verifications_promotion_id_project_id_environment_id",
                table: "release_verifications",
                columns: new[] { "promotion_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_verifications_release_id_environment_id",
                table: "release_verifications",
                columns: new[] { "release_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_verifications_report_id_project_id_environment_id",
                table: "release_verifications",
                columns: new[] { "report_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_verification_reports_artifact_id_project_id_environment_id",
                table: "verification_reports",
                columns: new[] { "artifact_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_verification_reports_created_by",
                table: "verification_reports",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_verification_reports_project_id_organization_id",
                table: "verification_reports",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_verification_reports_promotion_id_project_id_environment_id",
                table: "verification_reports",
                columns: new[] { "promotion_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_verification_reports_storage_key",
                table: "verification_reports",
                column: "storage_key",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_release_records_release_artifacts_artifact_id",
                table: "release_records",
                column: "artifact_id",
                principalTable: "release_artifacts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_records_release_promotions_promotion_id_artifact_id~",
                table: "release_records",
                columns: new[] { "promotion_id", "artifact_id", "source_release_id", "environment_id" },
                principalTable: "release_promotions",
                principalColumns: new[] { "id", "artifact_id", "source_release_id", "target_environment_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_records_release_records_source_release_id",
                table: "release_records",
                column: "source_release_id",
                principalTable: "release_records",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_release_records_release_artifacts_artifact_id",
                table: "release_records");

            migrationBuilder.DropForeignKey(
                name: "FK_release_records_release_promotions_promotion_id_artifact_id~",
                table: "release_records");

            migrationBuilder.DropForeignKey(
                name: "FK_release_records_release_records_source_release_id",
                table: "release_records");

            migrationBuilder.DropTable(
                name: "project_delivery_policies");

            migrationBuilder.DropTable(
                name: "release_promotion_events");

            migrationBuilder.DropTable(
                name: "release_promotion_mappings");

            migrationBuilder.DropTable(
                name: "release_verifications");

            migrationBuilder.DropTable(
                name: "verification_reports");

            migrationBuilder.DropTable(
                name: "release_promotions");

            migrationBuilder.DropTable(
                name: "release_test_acceptances");

            migrationBuilder.DropTable(
                name: "release_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_release_records_artifact_id",
                table: "release_records");

            migrationBuilder.DropIndex(
                name: "IX_release_records_promotion_id",
                table: "release_records");

            migrationBuilder.DropIndex(
                name: "IX_release_records_promotion_id_artifact_id_source_release_id_~",
                table: "release_records");

            migrationBuilder.DropIndex(
                name: "IX_release_records_source_release_id",
                table: "release_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_formal_promotion_link",
                table: "release_records");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_applications_id_organization_id",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "artifact_id",
                table: "release_records");

            migrationBuilder.DropColumn(
                name: "promotion_id",
                table: "release_records");

            migrationBuilder.DropColumn(
                name: "source_release_id",
                table: "release_records");
        }
    }
}

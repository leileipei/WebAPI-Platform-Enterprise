using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleasePipelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_delivery_policy",
                table: "project_delivery_policies");

            migrationBuilder.AddColumn<Guid>(
                name: "pipeline_run_stage_id",
                table: "release_verifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "profile_hash",
                table: "release_verifications",
                type: "varchar(64)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "stage_attempt_id",
                table: "release_verifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pipeline_run_stage_id",
                table: "release_test_acceptances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "profile_hash",
                table: "release_test_acceptances",
                type: "varchar(64)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "stage_attempt_id",
                table: "release_test_acceptances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gate_origin",
                table: "release_promotions",
                type: "varchar(32)",
                nullable: false,
                defaultValue: "ProjectConnection");

            migrationBuilder.AddColumn<Guid>(
                name: "pipeline_run_stage_id",
                table: "release_promotions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "stage_attempt_id",
                table: "release_promotions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "active_pipeline_version_id",
                table: "project_delivery_policies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "release_pipelines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "varchar(100)", nullable: false, defaultValue: ""),
                    description = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    draft_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    status = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "Active"),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_pipelines", x => x.id);
                    table.UniqueConstraint("AK_release_pipelines_id_project_id", x => new { x.id, x.project_id });
                    table.CheckConstraint("ck_pipeline_draft", "length(btrim(name)) BETWEEN 1 AND 100 AND revision>=1 AND status IN ('Active','Archived')");
                    table.ForeignKey(
                        name: "FK_release_pipelines_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipelines_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipelines_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_pipeline_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    pipeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    definition_hash = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_pipeline_versions", x => x.id);
                    table.UniqueConstraint("AK_release_pipeline_versions_id_project_id", x => new { x.id, x.project_id });
                    table.CheckConstraint("ck_pipeline_version", "version_no>=1 AND length(definition_hash)=64");
                    table.ForeignKey(
                        name: "FK_release_pipeline_versions_release_pipelines_pipeline_id_pro~",
                        columns: x => new { x.pipeline_id, x.project_id },
                        principalTable: "release_pipelines",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_versions_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_pipeline_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_hash = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    root_artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_artifact_hash = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    source_environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_config_version = table.Column<long>(type: "bigint", nullable: false),
                    source_deployment_sequence = table.Column<long>(type: "bigint", nullable: false),
                    policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "Active"),
                    current_stage_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_pipeline_runs", x => x.id);
                    table.UniqueConstraint("AK_release_pipeline_runs_id_project_id", x => new { x.id, x.project_id });
                    table.CheckConstraint("ck_pipeline_run", "status IN ('Active','Paused','TimedOut','Invalidated','Cancelled','Completed') AND current_stage_order BETWEEN 1 AND 8 AND revision>=1 AND policy_revision>=1 AND source_config_version>0 AND source_deployment_sequence>0 AND length(definition_hash)=64 AND length(root_artifact_hash)=64");
                    table.ForeignKey(
                        name: "FK_release_pipeline_runs_projects_project_id_organization_id",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_runs_release_artifacts_root_artifact_id_pr~",
                        columns: x => new { x.root_artifact_id, x.project_id, x.source_environment_id, x.source_release_id },
                        principalTable: "release_artifacts",
                        principalColumns: new[] { "id", "project_id", "source_environment_id", "source_release_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_runs_release_pipeline_versions_pipeline_ve~",
                        columns: x => new { x.pipeline_version_id, x.project_id },
                        principalTable: "release_pipeline_versions",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_runs_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_pipeline_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_status = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: ""),
                    to_status = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: ""),
                    reason_code = table.Column<string>(type: "varchar(128)", nullable: false, defaultValue: ""),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    related_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_pipeline_events", x => x.id);
                    table.CheckConstraint("ck_pipeline_event", "attempt_id IS NULL OR stage_id IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_release_pipeline_events_release_pipeline_runs_run_id_projec~",
                        columns: x => new { x.run_id, x.project_id },
                        principalTable: "release_pipeline_runs",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_events_users_actor_id",
                        column: x => x.actor_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_pipeline_run_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_order = table.Column<int>(type: "integer", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_stage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stage_artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    profile_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    profile_hash = table.Column<string>(type: "varchar(64)", nullable: false, defaultValue: ""),
                    status = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "Pending"),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_pipeline_run_stages", x => x.id);
                    table.UniqueConstraint("AK_release_pipeline_run_stages_id_project_id_environment_id", x => new { x.id, x.project_id, x.environment_id });
                    table.UniqueConstraint("AK_release_pipeline_run_stages_id_run_id", x => new { x.id, x.run_id });
                    table.UniqueConstraint("AK_release_pipeline_run_stages_id_run_id_project_id_environmen~", x => new { x.id, x.run_id, x.project_id, x.environment_id });
                    table.CheckConstraint("ck_pipeline_stage", "stage_order BETWEEN 1 AND 8 AND revision>=1 AND length(profile_hash)=64 AND (source_stage_id IS NULL OR source_stage_id<>id) AND status IN ('Pending','AwaitingEvidence','AwaitingAcceptance','AwaitingMapping','AwaitingPrecheck','AwaitingApproval','ReadyToDeploy','Deploying','AwaitingVerification','Passed','Rejected','DeploymentFailed','VerificationFailed','TimedOut','Invalidated','Cancelled')");
                    table.ForeignKey(
                        name: "FK_release_pipeline_run_stages_environments_environment_id_pro~",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_run_stages_release_artifacts_stage_artifac~",
                        columns: x => new { x.stage_artifact_id, x.project_id, x.environment_id },
                        principalTable: "release_artifacts",
                        principalColumns: new[] { "id", "project_id", "source_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_run_stages_release_pipeline_run_stages_sou~",
                        columns: x => new { x.source_stage_id, x.run_id },
                        principalTable: "release_pipeline_run_stages",
                        principalColumns: new[] { "id", "run_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_run_stages_release_pipeline_runs_run_id_pr~",
                        columns: x => new { x.run_id, x.project_id },
                        principalTable: "release_pipeline_runs",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "release_pipeline_stage_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    run_stage_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    origin_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    status = table.Column<string>(type: "varchar(32)", nullable: false, defaultValue: "Active"),
                    context_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acceptance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actual_release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_pipeline_stage_attempts", x => x.id);
                    table.UniqueConstraint("AK_release_pipeline_stage_attempts_id_run_stage_id", x => new { x.id, x.run_stage_id });
                    table.CheckConstraint("ck_pipeline_attempt", "attempt_no>=1 AND revision>=1 AND deadline_at>activated_at AND (origin_attempt_id IS NULL OR origin_attempt_id<>id) AND status IN ('Active','Passed','Rejected','DeploymentFailed','VerificationFailed','TimedOut','Invalidated','Cancelled')");
                    table.ForeignKey(
                        name: "FK_release_pipeline_stage_attempts_release_artifacts_artifact_~",
                        columns: x => new { x.artifact_id, x.project_id, x.environment_id },
                        principalTable: "release_artifacts",
                        principalColumns: new[] { "id", "project_id", "source_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_stage_attempts_release_pipeline_run_stages~",
                        columns: x => new { x.run_stage_id, x.run_id, x.project_id, x.environment_id },
                        principalTable: "release_pipeline_run_stages",
                        principalColumns: new[] { "id", "run_id", "project_id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_stage_attempts_release_pipeline_stage_atte~",
                        columns: x => new { x.origin_attempt_id, x.run_stage_id },
                        principalTable: "release_pipeline_stage_attempts",
                        principalColumns: new[] { "id", "run_stage_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_stage_attempts_release_promotions_promotio~",
                        columns: x => new { x.promotion_id, x.project_id, x.environment_id },
                        principalTable: "release_promotions",
                        principalColumns: new[] { "id", "project_id", "target_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_stage_attempts_release_records_actual_rele~",
                        columns: x => new { x.actual_release_id, x.environment_id },
                        principalTable: "release_records",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_release_pipeline_stage_attempts_release_test_acceptances_ac~",
                        columns: x => new { x.acceptance_id, x.project_id, x.environment_id },
                        principalTable: "release_test_acceptances",
                        principalColumns: new[] { "id", "project_id", "source_environment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_release_verifications_pipeline_run_stage_id_project_id_envi~",
                table: "release_verifications",
                columns: new[] { "pipeline_run_stage_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_verifications_stage_attempt_id_pipeline_run_stage_id",
                table: "release_verifications",
                columns: new[] { "stage_attempt_id", "pipeline_run_stage_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_release_verification_pipeline_context",
                table: "release_verifications",
                sql: "((pipeline_run_stage_id IS NULL AND stage_attempt_id IS NULL AND profile_hash IS NULL) OR (pipeline_run_stage_id IS NOT NULL AND stage_attempt_id IS NOT NULL AND profile_hash IS NOT NULL AND length(profile_hash)=64))");

            migrationBuilder.CreateIndex(
                name: "IX_release_test_acceptances_pipeline_run_stage_id_project_id_s~",
                table: "release_test_acceptances",
                columns: new[] { "pipeline_run_stage_id", "project_id", "source_environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_test_acceptances_stage_attempt_id_pipeline_run_stag~",
                table: "release_test_acceptances",
                columns: new[] { "stage_attempt_id", "pipeline_run_stage_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_release_test_acceptance_pipeline_context",
                table: "release_test_acceptances",
                sql: "((pipeline_run_stage_id IS NULL AND stage_attempt_id IS NULL AND profile_hash IS NULL) OR (pipeline_run_stage_id IS NOT NULL AND stage_attempt_id IS NOT NULL AND profile_hash IS NOT NULL AND length(profile_hash)=64))");

            migrationBuilder.CreateIndex(
                name: "IX_release_promotions_pipeline_run_stage_id_project_id_target_~",
                table: "release_promotions",
                columns: new[] { "pipeline_run_stage_id", "project_id", "target_environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_promotions_stage_attempt_id_pipeline_run_stage_id",
                table: "release_promotions",
                columns: new[] { "stage_attempt_id", "pipeline_run_stage_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_release_promotion_pipeline_context",
                table: "release_promotions",
                sql: "((gate_origin='ProjectConnection' AND pipeline_run_stage_id IS NULL AND stage_attempt_id IS NULL) OR (gate_origin='PipelineRunStage' AND pipeline_run_stage_id IS NOT NULL AND stage_attempt_id IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_project_delivery_policies_active_pipeline_version_id_projec~",
                table: "project_delivery_policies",
                columns: new[] { "active_pipeline_version_id", "project_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_delivery_policy",
                table: "project_delivery_policies",
                sql: "((mode IN ('Legacy','PromotionRequired') AND active_pipeline_version_id IS NULL) OR (mode='PipelineRequired' AND active_pipeline_version_id IS NOT NULL)) AND source_environment_id <> target_environment_id AND verification_validity_minutes BETWEEN 1 AND 10080 AND revision >= 1 AND cardinality(required_test_types) BETWEEN 1 AND 3 AND required_test_types <@ ARRAY['InterfaceFunction','Integration','ContractCompatibility']::text[]");

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_events_actor_id",
                table: "release_pipeline_events",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_events_attempt_id_stage_id",
                table: "release_pipeline_events",
                columns: new[] { "attempt_id", "stage_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_events_run_id_created_at_id",
                table: "release_pipeline_events",
                columns: new[] { "run_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_events_run_id_project_id",
                table: "release_pipeline_events",
                columns: new[] { "run_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_events_stage_id_run_id",
                table: "release_pipeline_events",
                columns: new[] { "stage_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_run_stages_current_attempt_id_id",
                table: "release_pipeline_run_stages",
                columns: new[] { "current_attempt_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_run_stages_environment_id_project_id",
                table: "release_pipeline_run_stages",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_run_stages_run_id_environment_id",
                table: "release_pipeline_run_stages",
                columns: new[] { "run_id", "environment_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_run_stages_run_id_project_id",
                table: "release_pipeline_run_stages",
                columns: new[] { "run_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_run_stages_run_id_stage_order",
                table: "release_pipeline_run_stages",
                columns: new[] { "run_id", "stage_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_run_stages_source_stage_id_run_id",
                table: "release_pipeline_run_stages",
                columns: new[] { "source_stage_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_run_stages_stage_artifact_id_project_id_en~",
                table: "release_pipeline_run_stages",
                columns: new[] { "stage_artifact_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_runs_created_by",
                table: "release_pipeline_runs",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_runs_pipeline_version_id_project_id",
                table: "release_pipeline_runs",
                columns: new[] { "pipeline_version_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_runs_project_id",
                table: "release_pipeline_runs",
                column: "project_id",
                unique: true,
                filter: "status IN ('Active','Paused','TimedOut')");

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_runs_project_id_created_at_id",
                table: "release_pipeline_runs",
                columns: new[] { "project_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_runs_project_id_organization_id",
                table: "release_pipeline_runs",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_runs_root_artifact_id_project_id_source_en~",
                table: "release_pipeline_runs",
                columns: new[] { "root_artifact_id", "project_id", "source_environment_id", "source_release_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_stage_attempts_acceptance_id_project_id_en~",
                table: "release_pipeline_stage_attempts",
                columns: new[] { "acceptance_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_stage_attempts_actual_release_id_environme~",
                table: "release_pipeline_stage_attempts",
                columns: new[] { "actual_release_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_stage_attempts_artifact_id_project_id_envi~",
                table: "release_pipeline_stage_attempts",
                columns: new[] { "artifact_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_stage_attempts_origin_attempt_id_run_stage~",
                table: "release_pipeline_stage_attempts",
                columns: new[] { "origin_attempt_id", "run_stage_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_stage_attempts_promotion_id",
                table: "release_pipeline_stage_attempts",
                column: "promotion_id",
                unique: true,
                filter: "promotion_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_stage_attempts_promotion_id_project_id_env~",
                table: "release_pipeline_stage_attempts",
                columns: new[] { "promotion_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_stage_attempts_run_stage_id",
                table: "release_pipeline_stage_attempts",
                column: "run_stage_id",
                unique: true,
                filter: "status='Active'");

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_stage_attempts_run_stage_id_attempt_no",
                table: "release_pipeline_stage_attempts",
                columns: new[] { "run_stage_id", "attempt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_stage_attempts_run_stage_id_run_id_project~",
                table: "release_pipeline_stage_attempts",
                columns: new[] { "run_stage_id", "run_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_versions_created_by",
                table: "release_pipeline_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_versions_pipeline_id_project_id",
                table: "release_pipeline_versions",
                columns: new[] { "pipeline_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipeline_versions_pipeline_id_version_no",
                table: "release_pipeline_versions",
                columns: new[] { "pipeline_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_pipelines_created_by",
                table: "release_pipelines",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_pipelines_project_id_created_at_id",
                table: "release_pipelines",
                columns: new[] { "project_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipelines_project_id_organization_id",
                table: "release_pipelines",
                columns: new[] { "project_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_release_pipelines_updated_by",
                table: "release_pipelines",
                column: "updated_by");

            migrationBuilder.AddForeignKey(
                name: "FK_project_delivery_policies_release_pipeline_versions_active_~",
                table: "project_delivery_policies",
                columns: new[] { "active_pipeline_version_id", "project_id" },
                principalTable: "release_pipeline_versions",
                principalColumns: new[] { "id", "project_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_promotions_release_pipeline_run_stages_pipeline_run~",
                table: "release_promotions",
                columns: new[] { "pipeline_run_stage_id", "project_id", "target_environment_id" },
                principalTable: "release_pipeline_run_stages",
                principalColumns: new[] { "id", "project_id", "environment_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_promotions_release_pipeline_stage_attempts_stage_at~",
                table: "release_promotions",
                columns: new[] { "stage_attempt_id", "pipeline_run_stage_id" },
                principalTable: "release_pipeline_stage_attempts",
                principalColumns: new[] { "id", "run_stage_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_test_acceptances_release_pipeline_run_stages_pipeli~",
                table: "release_test_acceptances",
                columns: new[] { "pipeline_run_stage_id", "project_id", "source_environment_id" },
                principalTable: "release_pipeline_run_stages",
                principalColumns: new[] { "id", "project_id", "environment_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_test_acceptances_release_pipeline_stage_attempts_st~",
                table: "release_test_acceptances",
                columns: new[] { "stage_attempt_id", "pipeline_run_stage_id" },
                principalTable: "release_pipeline_stage_attempts",
                principalColumns: new[] { "id", "run_stage_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_verifications_release_pipeline_run_stages_pipeline_~",
                table: "release_verifications",
                columns: new[] { "pipeline_run_stage_id", "project_id", "environment_id" },
                principalTable: "release_pipeline_run_stages",
                principalColumns: new[] { "id", "project_id", "environment_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_verifications_release_pipeline_stage_attempts_stage~",
                table: "release_verifications",
                columns: new[] { "stage_attempt_id", "pipeline_run_stage_id" },
                principalTable: "release_pipeline_stage_attempts",
                principalColumns: new[] { "id", "run_stage_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_pipeline_events_release_pipeline_run_stages_stage_i~",
                table: "release_pipeline_events",
                columns: new[] { "stage_id", "run_id" },
                principalTable: "release_pipeline_run_stages",
                principalColumns: new[] { "id", "run_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_pipeline_events_release_pipeline_stage_attempts_att~",
                table: "release_pipeline_events",
                columns: new[] { "attempt_id", "stage_id" },
                principalTable: "release_pipeline_stage_attempts",
                principalColumns: new[] { "id", "run_stage_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_release_pipeline_run_stages_release_pipeline_stage_attempts~",
                table: "release_pipeline_run_stages",
                columns: new[] { "current_attempt_id", "id" },
                principalTable: "release_pipeline_stage_attempts",
                principalColumns: new[] { "id", "run_stage_id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                CREATE FUNCTION pipeline_immutable_fact() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Pipeline versions and events are append-only' USING ERRCODE='23514';
                END;
                $$;
                CREATE TRIGGER immutable_pipeline_version BEFORE UPDATE OR DELETE ON release_pipeline_versions
                    FOR EACH ROW EXECUTE FUNCTION pipeline_immutable_fact();
                CREATE TRIGGER immutable_pipeline_event BEFORE UPDATE OR DELETE ON release_pipeline_events
                    FOR EACH ROW EXECUTE FUNCTION pipeline_immutable_fact();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER immutable_pipeline_event ON release_pipeline_events; DROP TRIGGER immutable_pipeline_version ON release_pipeline_versions; DROP FUNCTION pipeline_immutable_fact();");
            migrationBuilder.DropForeignKey(
                name: "FK_project_delivery_policies_release_pipeline_versions_active_~",
                table: "project_delivery_policies");

            migrationBuilder.DropForeignKey(
                name: "FK_release_promotions_release_pipeline_run_stages_pipeline_run~",
                table: "release_promotions");

            migrationBuilder.DropForeignKey(
                name: "FK_release_promotions_release_pipeline_stage_attempts_stage_at~",
                table: "release_promotions");

            migrationBuilder.DropForeignKey(
                name: "FK_release_test_acceptances_release_pipeline_run_stages_pipeli~",
                table: "release_test_acceptances");

            migrationBuilder.DropForeignKey(
                name: "FK_release_test_acceptances_release_pipeline_stage_attempts_st~",
                table: "release_test_acceptances");

            migrationBuilder.DropForeignKey(
                name: "FK_release_verifications_release_pipeline_run_stages_pipeline_~",
                table: "release_verifications");

            migrationBuilder.DropForeignKey(
                name: "FK_release_verifications_release_pipeline_stage_attempts_stage~",
                table: "release_verifications");

            migrationBuilder.DropForeignKey(
                name: "FK_release_pipeline_stage_attempts_release_pipeline_run_stages~",
                table: "release_pipeline_stage_attempts");

            migrationBuilder.DropTable(
                name: "release_pipeline_events");

            migrationBuilder.DropTable(
                name: "release_pipeline_run_stages");

            migrationBuilder.DropTable(
                name: "release_pipeline_runs");

            migrationBuilder.DropTable(
                name: "release_pipeline_stage_attempts");

            migrationBuilder.DropTable(
                name: "release_pipeline_versions");

            migrationBuilder.DropTable(
                name: "release_pipelines");

            migrationBuilder.DropIndex(
                name: "IX_release_verifications_pipeline_run_stage_id_project_id_envi~",
                table: "release_verifications");

            migrationBuilder.DropIndex(
                name: "IX_release_verifications_stage_attempt_id_pipeline_run_stage_id",
                table: "release_verifications");

            migrationBuilder.DropCheckConstraint(
                name: "ck_release_verification_pipeline_context",
                table: "release_verifications");

            migrationBuilder.DropIndex(
                name: "IX_release_test_acceptances_pipeline_run_stage_id_project_id_s~",
                table: "release_test_acceptances");

            migrationBuilder.DropIndex(
                name: "IX_release_test_acceptances_stage_attempt_id_pipeline_run_stag~",
                table: "release_test_acceptances");

            migrationBuilder.DropCheckConstraint(
                name: "ck_release_test_acceptance_pipeline_context",
                table: "release_test_acceptances");

            migrationBuilder.DropIndex(
                name: "IX_release_promotions_pipeline_run_stage_id_project_id_target_~",
                table: "release_promotions");

            migrationBuilder.DropIndex(
                name: "IX_release_promotions_stage_attempt_id_pipeline_run_stage_id",
                table: "release_promotions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_release_promotion_pipeline_context",
                table: "release_promotions");

            migrationBuilder.DropIndex(
                name: "IX_project_delivery_policies_active_pipeline_version_id_projec~",
                table: "project_delivery_policies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_delivery_policy",
                table: "project_delivery_policies");

            migrationBuilder.DropColumn(
                name: "pipeline_run_stage_id",
                table: "release_verifications");

            migrationBuilder.DropColumn(
                name: "profile_hash",
                table: "release_verifications");

            migrationBuilder.DropColumn(
                name: "stage_attempt_id",
                table: "release_verifications");

            migrationBuilder.DropColumn(
                name: "pipeline_run_stage_id",
                table: "release_test_acceptances");

            migrationBuilder.DropColumn(
                name: "profile_hash",
                table: "release_test_acceptances");

            migrationBuilder.DropColumn(
                name: "stage_attempt_id",
                table: "release_test_acceptances");

            migrationBuilder.DropColumn(
                name: "gate_origin",
                table: "release_promotions");

            migrationBuilder.DropColumn(
                name: "pipeline_run_stage_id",
                table: "release_promotions");

            migrationBuilder.DropColumn(
                name: "stage_attempt_id",
                table: "release_promotions");

            migrationBuilder.DropColumn(
                name: "active_pipeline_version_id",
                table: "project_delivery_policies");

            migrationBuilder.AddCheckConstraint(
                name: "ck_delivery_policy",
                table: "project_delivery_policies",
                sql: "mode IN ('Legacy','PromotionRequired') AND source_environment_id <> target_environment_id AND verification_validity_minutes BETWEEN 1 AND 10080 AND revision >= 1 AND cardinality(required_test_types) BETWEEN 1 AND 3 AND required_test_types <@ ARRAY['InterfaceFunction','Integration','ContractCompatibility']::text[]");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SsoOidc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sso_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "varchar(128)", nullable: false),
                    provider_type = table.Column<string>(type: "varchar(24)", nullable: false, defaultValue: "oidc"),
                    issuer = table.Column<string>(type: "varchar(1024)", nullable: false),
                    client_id = table.Column<string>(type: "varchar(256)", nullable: false),
                    secret_ref = table.Column<string>(type: "varchar(512)", nullable: false),
                    scopes = table.Column<string>(type: "jsonb", nullable: false),
                    claim_mapping = table.Column<string>(type: "jsonb", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    auth_revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sso_providers", x => x.id);
                    table.CheckConstraint("ck_sso_provider_default", "NOT is_default OR enabled");
                    table.CheckConstraint("ck_sso_provider_json", "jsonb_typeof(scopes) = 'array' AND jsonb_typeof(claim_mapping) = 'object'");
                    table.CheckConstraint("ck_sso_provider_revision", "revision > 0 AND auth_revision > 0");
                    table.CheckConstraint("ck_sso_provider_type", "provider_type = 'oidc'");
                    table.ForeignKey(
                        name: "FK_sso_providers_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sso_login_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_revision = table.Column<long>(type: "bigint", nullable: false),
                    auth_revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    return_path = table.Column<string>(type: "varchar(2048)", nullable: false),
                    state = table.Column<string>(type: "varchar(24)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    failure_code = table.Column<string>(type: "varchar(64)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sso_login_attempts", x => x.id);
                    table.CheckConstraint("ck_sso_attempt_expiry", "expires_at > created_at");
                    table.CheckConstraint("ck_sso_attempt_revision", "provider_revision > 0 AND auth_revision > 0");
                    table.CheckConstraint("ck_sso_attempt_state", "state IN ('Pending','Processing','Succeeded','Failed')");
                    table.ForeignKey(
                        name: "FK_sso_login_attempts_sso_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "sso_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sso_provider_tests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_revision = table.Column<long>(type: "bigint", nullable: false),
                    tested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    status = table.Column<string>(type: "varchar(24)", nullable: false),
                    stages = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sso_provider_tests", x => x.id);
                    table.CheckConstraint("ck_sso_test_revision", "provider_revision > 0");
                    table.ForeignKey(
                        name: "FK_sso_provider_tests_sso_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "sso_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_external_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "varchar(1024)", nullable: false),
                    subject = table.Column<string>(type: "varchar(255)", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_external_identities", x => x.id);
                    table.CheckConstraint("ck_external_identity_revision", "revision > 0");
                    table.ForeignKey(
                        name: "FK_user_external_identities_sso_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "sso_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_external_identities_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sso_login_attempts_provider_id",
                table: "sso_login_attempts",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "IX_sso_login_attempts_state_expires_at",
                table: "sso_login_attempts",
                columns: new[] { "state", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_sso_provider_tests_provider_id_tested_at",
                table: "sso_provider_tests",
                columns: new[] { "provider_id", "tested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_sso_default_organization",
                table: "sso_providers",
                column: "organization_id",
                unique: true,
                filter: "organization_id IS NOT NULL AND is_default AND enabled");

            migrationBuilder.CreateIndex(
                name: "ux_sso_default_platform",
                table: "sso_providers",
                column: "is_default",
                unique: true,
                filter: "organization_id IS NULL AND is_default AND enabled");

            migrationBuilder.CreateIndex(
                name: "IX_user_external_identities_provider_id_issuer_subject",
                table: "user_external_identities",
                columns: new[] { "provider_id", "issuer", "subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_external_identities_user_id",
                table: "user_external_identities",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sso_login_attempts");

            migrationBuilder.DropTable(
                name: "sso_provider_tests");

            migrationBuilder.DropTable(
                name: "user_external_identities");

            migrationBuilder.DropTable(
                name: "sso_providers");
        }
    }
}

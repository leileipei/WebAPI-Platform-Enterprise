using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24LeaseIssuance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_security_lease_issuances",
                columns: table => new
                {
                    lease_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    checkpoint_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fresh_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    signing_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_security_lease_issuances", x => x.lease_id);
                    table.CheckConstraint("ck_security_lease_issuance", "lease_id<>'00000000-0000-0000-0000-000000000000'::uuid AND instance_id<>'00000000-0000-0000-0000-000000000000'::uuid AND epoch>=0 AND fresh_until>issued_at AND fresh_until<=issued_at+interval '30 seconds' AND checkpoint_hash ~ '^[a-f0-9]{64}$' AND payload_hash ~ '^[a-f0-9]{64}$'");
                    table.ForeignKey(
                        name: "FK_gateway_security_lease_issuances_environment_security_state~",
                        columns: x => new { x.environment_id, x.security_generation },
                        principalTable: "environment_security_states",
                        principalColumns: new[] { "environment_id", "security_generation" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_lease_issuances_environments_environment_i~",
                        columns: x => new { x.environment_id, x.project_id },
                        principalTable: "environments",
                        principalColumns: new[] { "id", "project_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_lease_issuances_gateway_security_leases_no~",
                        columns: x => new { x.node_id, x.instance_id },
                        principalTable: "gateway_security_leases",
                        principalColumns: new[] { "node_id", "instance_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gateway_security_lease_issuances_projects_project_id_organi~",
                        columns: x => new { x.project_id, x.organization_id },
                        principalTable: "projects",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_lease_issuances_environment_id_project_id",
                table: "gateway_security_lease_issuances",
                columns: new[] { "environment_id", "project_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_lease_issuances_environment_id_security_ge~",
                table: "gateway_security_lease_issuances",
                columns: new[] { "environment_id", "security_generation" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_lease_issuances_node_id_instance_id",
                table: "gateway_security_lease_issuances",
                columns: new[] { "node_id", "instance_id" });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_lease_issuances_project_id_organization_id",
                table: "gateway_security_lease_issuances",
                columns: new[] { "project_id", "organization_id" });

            // Existing mutable horizons are deliberately not backfilled: they
            // do not prove the exact signed claims of any historical lease.
            migrationBuilder.Sql("""
                CREATE FUNCTION gateway_lease_issuance_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  RAISE EXCEPTION 'gateway_lease_issuance_immutable' USING ERRCODE='23514';
                END $$;
                CREATE TRIGGER gateway_lease_issuance_immutable BEFORE UPDATE OR DELETE ON gateway_security_lease_issuances
                  FOR EACH ROW EXECUTE FUNCTION gateway_lease_issuance_immutable();
                CREATE TRIGGER gateway_lease_issuance_no_truncate BEFORE TRUNCATE ON gateway_security_lease_issuances
                  FOR EACH STATEMENT EXECUTE FUNCTION gateway_lease_issuance_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
        }
    }
}

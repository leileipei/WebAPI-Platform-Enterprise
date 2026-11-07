using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24AckVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_gateway_security_lease_issuances_gateway_security_leases_no~",
                table: "gateway_security_lease_issuances");

            migrationBuilder.DropIndex(
                name: "IX_gateway_security_lease_issuances_node_id_instance_id",
                table: "gateway_security_lease_issuances");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_security_operation_targets_operation_id_environment_id_node~",
                table: "security_operation_targets",
                columns: new[] { "operation_id", "environment_id", "node_id", "instance_guid", "challenge_nonce" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_gateway_security_leases_node_id_instance_id_environment_id_~",
                table: "gateway_security_leases",
                columns: new[] { "node_id", "instance_id", "environment_id", "project_id", "organization_id", "security_generation" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_gateway_security_lease_issuances_lease_id_node_id_instance_~",
                table: "gateway_security_lease_issuances",
                columns: new[] { "lease_id", "node_id", "instance_id", "environment_id", "security_generation", "epoch", "checkpoint_hash" });

            migrationBuilder.CreateTable(
                name: "security_ack_verifications",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    challenge_nonce = table.Column<Guid>(type: "uuid", nullable: false),
                    security_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    checkpoint_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_ack_verifications", x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid });
                    table.CheckConstraint("ck_security_ack_verification", "epoch>0 AND checkpoint_hash ~ '^[a-f0-9]{64}$' AND challenge_nonce<>'00000000-0000-0000-0000-000000000000'::uuid AND lease_id<>'00000000-0000-0000-0000-000000000000'::uuid");
                    table.ForeignKey(
                        name: "FK_security_ack_verifications_gateway_security_lease_issuances~",
                        columns: x => new { x.lease_id, x.node_id, x.instance_guid, x.environment_id, x.security_generation, x.epoch, x.checkpoint_hash },
                        principalTable: "gateway_security_lease_issuances",
                        principalColumns: new[] { "lease_id", "node_id", "instance_id", "environment_id", "security_generation", "epoch", "checkpoint_hash" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_ack_verifications_security_acks_operation_id_envir~",
                        columns: x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid },
                        principalTable: "security_acks",
                        principalColumns: new[] { "operation_id", "environment_id", "node_id", "instance_guid" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_ack_verifications_security_operation_targets_opera~",
                        columns: x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid, x.challenge_nonce },
                        principalTable: "security_operation_targets",
                        principalColumns: new[] { "operation_id", "environment_id", "node_id", "instance_guid", "challenge_nonce" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_lease_issuances_node_id_instance_id_enviro~",
                table: "gateway_security_lease_issuances",
                columns: new[] { "node_id", "instance_id", "environment_id", "project_id", "organization_id", "security_generation" });

            migrationBuilder.CreateIndex(
                name: "IX_security_ack_verifications_lease_id_node_id_instance_guid_e~",
                table: "security_ack_verifications",
                columns: new[] { "lease_id", "node_id", "instance_guid", "environment_id", "security_generation", "epoch", "checkpoint_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_security_ack_verifications_operation_id_environment_id_node~",
                table: "security_ack_verifications",
                columns: new[] { "operation_id", "environment_id", "node_id", "instance_guid", "challenge_nonce" });

            migrationBuilder.AddForeignKey(
                name: "FK_gateway_security_lease_issuances_gateway_security_leases_no~",
                table: "gateway_security_lease_issuances",
                columns: new[] { "node_id", "instance_id", "environment_id", "project_id", "organization_id", "security_generation" },
                principalTable: "gateway_security_leases",
                principalColumns: new[] { "node_id", "instance_id", "environment_id", "project_id", "organization_id", "security_generation" },
                onDelete: ReferentialAction.Restrict);

            // Old ACK rows remain unchanged and are not backfilled. A newly
            // authenticated node must reapply the frozen challenge and prove it
            // with an actual issued lease before this record can be created.
            migrationBuilder.Sql("""
                CREATE FUNCTION security_ack_verification_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  IF NOT EXISTS(SELECT 1 FROM gateway_security_lease_issuances i WHERE i.lease_id=NEW.lease_id
                    AND NEW.applied_at>=i.issued_at-interval '2 seconds' AND NEW.applied_at<i.fresh_until
                    AND NEW.received_at>=NEW.applied_at-interval '2 seconds')
                  THEN RAISE EXCEPTION 'security_ack_verification_time_invalid' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER security_ack_verification_guard BEFORE INSERT ON security_ack_verifications
                  FOR EACH ROW EXECUTE FUNCTION security_ack_verification_guard();
                CREATE FUNCTION security_ack_verification_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  RAISE EXCEPTION 'security_ack_verification_immutable' USING ERRCODE='23514';
                END $$;
                CREATE TRIGGER security_ack_verification_immutable BEFORE UPDATE OR DELETE ON security_ack_verifications
                  FOR EACH ROW EXECUTE FUNCTION security_ack_verification_immutable();
                CREATE TRIGGER security_ack_verification_no_truncate BEFORE TRUNCATE ON security_ack_verifications
                  FOR EACH STATEMENT EXECUTE FUNCTION security_ack_verification_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
    }
}

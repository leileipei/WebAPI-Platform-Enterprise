using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24AckRevalidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A previous experimental verifier did not distinguish a new
            // challenge from a replay. Do not silently promote those facts.
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS(SELECT 1 FROM security_ack_verifications) THEN RAISE EXCEPTION 'security_ack_verification_origin_unverified' USING ERRCODE='23514'; END IF; END $$;");
            migrationBuilder.AddColumn<Guid>(
                name: "revalidation_nonce",
                table: "security_ack_verifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "security_ack_revalidation_challenges",
                columns: table => new
                {
                    nonce = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_ack_revalidation_challenges", x => x.nonce);
                    table.CheckConstraint("ck_security_ack_revalidation_challenge", "nonce<>'00000000-0000-0000-0000-000000000000'::uuid AND instance_guid<>'00000000-0000-0000-0000-000000000000'::uuid AND expires_at>created_at AND expires_at<=created_at+interval '30 seconds'");
                    table.ForeignKey(
                        name: "FK_security_ack_revalidation_challenges_security_operation_tar~",
                        columns: x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid },
                        principalTable: "security_operation_targets",
                        principalColumns: new[] { "operation_id", "environment_id", "node_id", "instance_guid" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_ack_verifications_revalidation_nonce",
                table: "security_ack_verifications",
                column: "revalidation_nonce");

            migrationBuilder.CreateIndex(
                name: "IX_security_ack_revalidation_challenges_operation_id_environme~",
                table: "security_ack_revalidation_challenges",
                columns: new[] { "operation_id", "environment_id", "node_id", "instance_guid", "expires_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_security_ack_verifications_security_ack_revalidation_challe~",
                table: "security_ack_verifications",
                column: "revalidation_nonce",
                principalTable: "security_ack_revalidation_challenges",
                principalColumn: "nonce",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION security_ack_verification_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  IF NOT EXISTS(SELECT 1 FROM gateway_security_lease_issuances i WHERE i.lease_id=NEW.lease_id
                    AND NEW.applied_at>=i.issued_at-interval '2 seconds' AND NEW.applied_at<i.fresh_until
                    AND NEW.received_at>=i.issued_at AND NEW.received_at>=NEW.applied_at-interval '2 seconds')
                  THEN RAISE EXCEPTION 'security_ack_verification_time_invalid' USING ERRCODE='23514'; END IF;
                  IF NEW.revalidation_nonce IS NOT NULL AND NOT EXISTS(
                    SELECT 1 FROM security_ack_revalidation_challenges c JOIN gateway_security_lease_issuances i ON i.lease_id=NEW.lease_id
                    WHERE c.nonce=NEW.revalidation_nonce AND c.operation_id=NEW.operation_id AND c.environment_id=NEW.environment_id
                      AND c.node_id=NEW.node_id AND c.instance_guid=NEW.instance_guid AND i.issued_at>=c.created_at
                      AND NEW.applied_at>=c.created_at-interval '2 seconds' AND NEW.applied_at<c.expires_at+interval '2 seconds'
                      AND NEW.received_at>=c.created_at AND NEW.received_at<c.expires_at)
                  THEN RAISE EXCEPTION 'security_ack_revalidation_invalid' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE FUNCTION security_ack_revalidation_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  RAISE EXCEPTION 'security_ack_revalidation_immutable' USING ERRCODE='23514';
                END $$;
                CREATE TRIGGER security_ack_revalidation_immutable BEFORE UPDATE OR DELETE ON security_ack_revalidation_challenges
                  FOR EACH ROW EXECUTE FUNCTION security_ack_revalidation_immutable();
                CREATE TRIGGER security_ack_revalidation_no_truncate BEFORE TRUNCATE ON security_ack_revalidation_challenges
                  FOR EACH STATEMENT EXECUTE FUNCTION security_ack_revalidation_immutable();
                CREATE FUNCTION current_security_receipt_clock_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  IF NOT EXISTS(SELECT 1 FROM gateway_security_leases l WHERE l.node_id=NEW.node_id AND l.instance_id=NEW.instance_id
                    AND l.environment_id=NEW.environment_id AND NEW.received_at>=l.issued_at)
                  THEN RAISE EXCEPTION 'security_receipt_clock_regression' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER current_security_receipt_clock_guard BEFORE INSERT OR UPDATE ON gateway_security_current_receipts
                  FOR EACH ROW EXECUTE FUNCTION current_security_receipt_clock_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
    }
}

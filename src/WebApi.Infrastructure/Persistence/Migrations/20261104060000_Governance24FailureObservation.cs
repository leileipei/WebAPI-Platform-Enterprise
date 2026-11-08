using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24FailureObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "observation_sequence",
                table: "security_ack_attempts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reported_nonce",
                table: "security_ack_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "security_ack_failure_latest",
                columns: table => new
                {
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    reported_nonce = table.Column<Guid>(type: "uuid", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    last_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    latest_sequence = table.Column<long>(type: "bigint", nullable: false),
                    observation_count = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_ack_failure_latest", x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid });
                    table.CheckConstraint("ck_security_ack_failure_latest", "reported_nonce<>'00000000-0000-0000-0000-000000000000'::uuid AND latest_sequence>0 AND observation_count>0 AND error_code IN ('security_lkg_write_denied','security_material_invalid','security_generation_recovery_required','security_state_unavailable','security_full_checkpoint_required','security_epoch_conflict','security_tombstone_regression')");
                    table.ForeignKey(
                        name: "FK_security_ack_failure_latest_security_operation_targets_oper~",
                        columns: x => new { x.operation_id, x.environment_id, x.node_id, x.instance_guid },
                        principalTable: "security_operation_targets",
                        principalColumns: new[] { "operation_id", "environment_id", "node_id", "instance_guid" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Historical attempts keep a NULL reported_nonce; it would be
            // unsafe to invent which revalidation challenge they referred to.
            migrationBuilder.Sql("""
                CREATE FUNCTION public.security_failure_nonce_valid(p_operation uuid,p_environment uuid,p_node uuid,p_instance uuid,p_nonce uuid,p_at timestamptz)
                RETURNS boolean LANGUAGE sql VOLATILE SET search_path=pg_catalog,public AS $$
                  SELECT EXISTS(SELECT 1 FROM public.security_operation_targets t WHERE t.operation_id=p_operation AND t.environment_id=p_environment
                    AND t.node_id=p_node AND t.instance_guid=p_instance AND (t.challenge_nonce=p_nonce OR EXISTS(
                      SELECT 1 FROM public.security_ack_revalidation_challenges c WHERE c.nonce=p_nonce AND c.operation_id=p_operation
                        AND c.environment_id=p_environment AND c.node_id=p_node AND c.instance_guid=p_instance
                        AND c.created_at<=p_at AND c.expires_at>p_at AND c.created_at<=pg_catalog.clock_timestamp()
                        AND c.expires_at>pg_catalog.clock_timestamp())))
                $$;
                CREATE FUNCTION public.security_failure_attempt_nonce_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$ BEGIN
                  IF NEW.reported_nonce IS NULL OR NEW.observation_sequence IS NULL OR NEW.observation_sequence<=0 OR NEW.received_at>pg_catalog.clock_timestamp()
                    OR NOT public.security_failure_nonce_valid(NEW.operation_id,NEW.environment_id,NEW.node_id,NEW.instance_guid,NEW.reported_nonce,NEW.received_at)
                  THEN RAISE EXCEPTION 'security_failure_nonce_invalid' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER security_failure_attempt_nonce_guard BEFORE INSERT ON public.security_ack_attempts
                  FOR EACH ROW EXECUTE FUNCTION public.security_failure_attempt_nonce_guard();
                CREATE FUNCTION public.security_failure_latest_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$ BEGIN
                  IF TG_OP='DELETE' OR TG_OP='TRUNCATE' THEN RAISE EXCEPTION 'security_failure_latest_immutable_identity' USING ERRCODE='23514'; END IF;
                  IF TG_OP='INSERT' AND (NEW.observation_count<>1 OR NEW.latest_sequence<>1) THEN RAISE EXCEPTION 'security_failure_transition_invalid' USING ERRCODE='23514'; END IF;
                  IF TG_OP='UPDATE' AND (ROW(NEW.operation_id,NEW.environment_id,NEW.node_id,NEW.instance_guid) IS DISTINCT FROM ROW(OLD.operation_id,OLD.environment_id,OLD.node_id,OLD.instance_guid)
                    OR NEW.last_accepted_at<OLD.last_accepted_at OR NEW.latest_sequence<=OLD.latest_sequence OR NEW.latest_sequence-OLD.latest_sequence<>1 OR NEW.observation_count<>OLD.observation_count+1)
                  THEN RAISE EXCEPTION 'security_failure_transition_invalid' USING ERRCODE='23514'; END IF;
                  IF NEW.last_accepted_at>pg_catalog.clock_timestamp() OR EXISTS(SELECT 1 FROM public.security_ack_attempts a WHERE a.operation_id=NEW.operation_id
                    AND a.environment_id=NEW.environment_id AND a.node_id=NEW.node_id AND a.instance_guid=NEW.instance_guid AND a.received_at>NEW.last_accepted_at)
                  THEN RAISE EXCEPTION 'security_failure_time_invalid' USING ERRCODE='23514'; END IF;
                  IF EXISTS(SELECT 1 FROM public.security_ack_attempts a WHERE a.operation_id=NEW.operation_id
                    AND a.environment_id=NEW.environment_id AND a.node_id=NEW.node_id AND a.instance_guid=NEW.instance_guid
                    AND (a.observation_sequence>NEW.latest_sequence OR a.observation_sequence=NEW.latest_sequence
                      AND (a.error_code IS DISTINCT FROM NEW.error_code OR a.reported_nonce IS DISTINCT FROM NEW.reported_nonce)))
                  THEN RAISE EXCEPTION 'security_failure_sequence_invalid' USING ERRCODE='23514'; END IF;
                  IF TG_OP='UPDATE' THEN
                    IF EXISTS(SELECT 1 FROM public.security_ack_attempts a WHERE a.operation_id=OLD.operation_id
                      AND a.environment_id=OLD.environment_id AND a.node_id=OLD.node_id AND a.instance_guid=OLD.instance_guid
                      AND a.observation_sequence=OLD.latest_sequence
                      AND (a.error_code IS DISTINCT FROM OLD.error_code OR a.reported_nonce IS DISTINCT FROM OLD.reported_nonce))
                    THEN RAISE EXCEPTION 'security_failure_sequence_invalid' USING ERRCODE='23514'; END IF;
                  END IF;
                  IF NOT public.security_failure_nonce_valid(NEW.operation_id,NEW.environment_id,NEW.node_id,NEW.instance_guid,NEW.reported_nonce,NEW.last_accepted_at)
                  THEN RAISE EXCEPTION 'security_failure_nonce_invalid' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER security_failure_latest_guard BEFORE INSERT OR UPDATE OR DELETE ON public.security_ack_failure_latest
                  FOR EACH ROW EXECUTE FUNCTION public.security_failure_latest_guard();
                CREATE TRIGGER security_failure_latest_no_truncate BEFORE TRUNCATE ON public.security_ack_failure_latest
                  FOR EACH STATEMENT EXECUTE FUNCTION public.security_failure_latest_guard();
                CREATE FUNCTION public.security_failure_attempt_no_truncate() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$ BEGIN
                  RAISE EXCEPTION 'security_failure_attempt_immutable' USING ERRCODE='23514';
                END $$;
                CREATE TRIGGER security_failure_attempt_no_truncate BEFORE TRUNCATE ON public.security_ack_attempts
                  FOR EACH STATEMENT EXECUTE FUNCTION public.security_failure_attempt_no_truncate();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24DatabaseClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_database_clock_watermark",
                columns: table => new
                {
                    singleton = table.Column<bool>(type: "boolean", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_database_clock_watermark", x => x.singleton);
                    table.CheckConstraint("ck_security_clock_singleton", "singleton");
                });
            migrationBuilder.CreateIndex(name: "IX_gateway_security_current_receipts_received_at", table: "gateway_security_current_receipts", column: "received_at");
            migrationBuilder.CreateIndex(name: "IX_security_ack_verifications_received_at", table: "security_ack_verifications", column: "received_at");
            migrationBuilder.CreateIndex(name: "IX_security_ack_revalidation_challenges_created_at", table: "security_ack_revalidation_challenges", column: "created_at");
            migrationBuilder.Sql("""
                INSERT INTO public.security_database_clock_watermark(singleton,observed_at)
                SELECT true,GREATEST(pg_catalog.clock_timestamp(),
                  COALESCE((SELECT MAX(issued_at) FROM public.gateway_security_lease_issuances),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(issued_at) FROM public.gateway_security_leases),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(received_at) FROM public.security_acks),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(received_at) FROM public.gateway_security_current_receipts),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(received_at) FROM public.security_ack_verifications),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(created_at) FROM public.security_ack_revalidation_challenges),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(observed_at) FROM public.gateway_security_lease_expiry_observations),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(last_accepted_at) FROM public.security_ack_failure_latest),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(received_at) FROM public.security_ack_attempts),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(committed_at) FROM public.security_operations),'-infinity'::timestamptz),
                  COALESCE((SELECT MAX(occurred_at) FROM public.security_events),'-infinity'::timestamptz));
                CREATE FUNCTION public.security_database_clock_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
                DECLARE current_time_value timestamptz;
                BEGIN
                  IF TG_OP<>'UPDATE' THEN RAISE EXCEPTION 'security_database_clock_immutable' USING ERRCODE='23514'; END IF;
                  IF NEW.singleton IS DISTINCT FROM OLD.singleton OR NOT EXISTS(
                    SELECT 1 FROM pg_catalog.pg_locks WHERE locktype='advisory' AND pid=pg_catalog.pg_backend_pid()
                      AND classid=0 AND objid=8901202 AND objsubid=1 AND mode='ExclusiveLock' AND granted)
                  THEN RAISE EXCEPTION 'security_database_clock_lock_required' USING ERRCODE='23514'; END IF;
                  -- A session-level lock also appears as ExclusiveLock. Retain
                  -- our own transaction-level lock even if that session lock
                  -- is subsequently released before the transaction commits.
                  PERFORM pg_catalog.pg_advisory_xact_lock(8901202);
                  current_time_value:=pg_catalog.clock_timestamp();
                  IF current_time_value<OLD.observed_at THEN RAISE EXCEPTION 'security_database_clock_regressed' USING ERRCODE='23514'; END IF;
                  NEW.observed_at:=current_time_value;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER security_database_clock_guard BEFORE INSERT OR UPDATE OR DELETE ON public.security_database_clock_watermark
                  FOR EACH ROW EXECUTE FUNCTION public.security_database_clock_guard();
                CREATE TRIGGER security_database_clock_no_truncate BEFORE TRUNCATE ON public.security_database_clock_watermark
                  FOR EACH STATEMENT EXECUTE FUNCTION public.security_database_clock_guard();
                CREATE FUNCTION public.security_observe_clock() RETURNS timestamptz LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
                DECLARE observed timestamptz;
                BEGIN
                  UPDATE public.security_database_clock_watermark SET observed_at=observed_at WHERE singleton RETURNING observed_at INTO observed;
                  IF NOT FOUND THEN RAISE EXCEPTION 'security_database_clock_missing' USING ERRCODE='23514'; END IF;
                  RETURN observed;
                END $$;
                CREATE FUNCTION public.security_authoritative_fact_time_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
                DECLARE fact_time timestamptz;
                BEGIN
                  IF TG_TABLE_NAME='gateway_security_lease_issuances' THEN fact_time:=NEW.issued_at;
                  ELSIF TG_TABLE_NAME='security_ack_revalidation_challenges' THEN fact_time:=NEW.created_at;
                  ELSE fact_time:=NEW.received_at;
                  END IF;
                  IF fact_time>pg_catalog.clock_timestamp() THEN RAISE EXCEPTION 'security_authoritative_fact_in_future' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER security_issuance_not_future BEFORE INSERT ON public.gateway_security_lease_issuances
                  FOR EACH ROW EXECUTE FUNCTION public.security_authoritative_fact_time_guard();
                CREATE TRIGGER security_receipt_not_future BEFORE INSERT OR UPDATE ON public.gateway_security_current_receipts
                  FOR EACH ROW EXECUTE FUNCTION public.security_authoritative_fact_time_guard();
                CREATE TRIGGER security_ack_verification_not_future BEFORE INSERT ON public.security_ack_verifications
                  FOR EACH ROW EXECUTE FUNCTION public.security_authoritative_fact_time_guard();
                CREATE TRIGGER security_challenge_not_future BEFORE INSERT ON public.security_ack_revalidation_challenges
                  FOR EACH ROW EXECUTE FUNCTION public.security_authoritative_fact_time_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
    }
}

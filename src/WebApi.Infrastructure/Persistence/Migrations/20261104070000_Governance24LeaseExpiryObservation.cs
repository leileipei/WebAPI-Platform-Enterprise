using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24LeaseExpiryObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gateway_security_lease_expiry_observations",
                columns: table => new
                {
                    lease_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gateway_security_lease_expiry_observations", x => x.lease_id);
                    table.ForeignKey(
                        name: "FK_gateway_security_lease_expiry_observations_gateway_security~",
                        column: x => x.lease_id,
                        principalTable: "gateway_security_lease_issuances",
                        principalColumn: "lease_id",
                        onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_lease_expiry_observations_observed_at",
                table: "gateway_security_lease_expiry_observations",
                column: "observed_at");
            migrationBuilder.CreateIndex(
                name: "IX_gateway_security_lease_issuances_issued_at",
                table: "gateway_security_lease_issuances",
                column: "issued_at");
            migrationBuilder.Sql("""
                CREATE FUNCTION public.security_lease_expiry_observation_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$ BEGIN
                  IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'security_lease_expiry_observation_immutable' USING ERRCODE='23514'; END IF;
                  NEW.observed_at:=pg_catalog.clock_timestamp();
                  IF NOT EXISTS(SELECT 1 FROM public.gateway_security_lease_issuances i WHERE i.lease_id=NEW.lease_id AND NEW.observed_at>=i.fresh_until)
                  THEN RAISE EXCEPTION 'security_lease_expiry_observation_invalid' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER security_lease_expiry_observation_guard BEFORE INSERT OR UPDATE OR DELETE ON public.gateway_security_lease_expiry_observations
                  FOR EACH ROW EXECUTE FUNCTION public.security_lease_expiry_observation_guard();
                CREATE TRIGGER security_lease_expiry_observation_no_truncate BEFORE TRUNCATE ON public.gateway_security_lease_expiry_observations
                  FOR EACH STATEMENT EXECUTE FUNCTION public.security_lease_expiry_observation_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");
    }
}

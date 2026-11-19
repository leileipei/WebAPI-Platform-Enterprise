using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebApi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WebApiDbContext))]
[Migration(FormalRotationDecisionProjection.Id)]
public sealed class FormalRotationDecisionProjection : Migration
{
    internal const string Id = "20261119150000_FormalRotationDecision";

    internal const string DecisionConstraint = """
        id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND kind IN ('Complete', 'AbortRequested', 'AbortConfirmed')
        AND pg_catalog.isfinite(recorded_at)
        AND intent_hash COLLATE "C" ~ '^[0-9a-f]{64}$'
        """;

    internal const string CompetingKindFilter = "kind IN ('Complete', 'AbortRequested')";

    // Bounded varchar can truncate excess trailing spaces before the CHECK runs.
    internal const string InstallSql = $$"""
        CREATE TABLE public.formal_rotation_decisions (
            id uuid NOT NULL,
            schedule_id uuid NOT NULL,
            kind text COLLATE "C" NOT NULL,
            recorded_at timestamp with time zone NOT NULL,
            intent_hash text NOT NULL,
            CONSTRAINT "PK_formal_rotation_decisions" PRIMARY KEY (id),
            CONSTRAINT "FK_formal_rotation_decisions_schedule" FOREIGN KEY (schedule_id)
                REFERENCES public.formal_credential_rotations (id) ON DELETE RESTRICT,
            CONSTRAINT ck_formal_rotation_decision CHECK (
        {{DecisionConstraint}}
            )
        );
        CREATE UNIQUE INDEX "IX_formal_rotation_decisions_schedule_id_kind"
            ON public.formal_rotation_decisions (schedule_id, kind);
        CREATE UNIQUE INDEX "IX_formal_rotation_decisions_schedule_complete_or_abort"
            ON public.formal_rotation_decisions (schedule_id) WHERE {{CompetingKindFilter}};

        CREATE TRIGGER formal_rotation_decision_no_change BEFORE UPDATE OR DELETE ON public.formal_rotation_decisions
            FOR EACH ROW EXECUTE FUNCTION public.formal_credential_rotation_immutable();
        CREATE TRIGGER formal_rotation_decision_no_truncate BEFORE TRUNCATE ON public.formal_rotation_decisions
            FOR EACH STATEMENT EXECUTE FUNCTION public.formal_credential_rotation_immutable();
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(InstallSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $WEBAPI_FORMAL_ROTATION_DECISION_DOWN$ BEGIN
            RAISE EXCEPTION 'formal_rotation_decision_down_blocked' USING ERRCODE='23514';
        END $WEBAPI_FORMAL_ROTATION_DECISION_DOWN$;
        """);
}

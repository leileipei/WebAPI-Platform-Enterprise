using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebApi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WebApiDbContext))]
[Migration(FormalCredentialRotationSchedule.Id)]
public sealed class FormalCredentialRotationSchedule : Migration
{
    internal const string Id = "20261119140000_FormalCredentialRotation";

    internal const string ScheduleConstraint = """
        id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND credential_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND replacement_credential_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND environment_id <> '00000000-0000-0000-0000-000000000000'::uuid
        AND credential_id <> replacement_credential_id
        AND pg_catalog.isfinite(hard_deadline) AND pg_catalog.isfinite(recorded_at)
        AND hard_deadline > recorded_at
        AND intent_hash COLLATE "C" ~ '^[0-9a-f]{64}$'
        """;

    // Completion remains a fact of the governance journal and security ledger.
    internal const string InstallSql = $$"""
        CREATE TABLE public.formal_credential_rotations (
            id uuid NOT NULL,
            credential_id uuid NOT NULL,
            replacement_credential_id uuid NOT NULL,
            environment_id uuid NOT NULL,
            hard_deadline timestamp with time zone NOT NULL,
            recorded_at timestamp with time zone NOT NULL,
            intent_hash character varying(64) NOT NULL,
            CONSTRAINT "PK_formal_credential_rotations" PRIMARY KEY (id),
            CONSTRAINT "FK_formal_credential_rotations_old_credential" FOREIGN KEY (credential_id)
                REFERENCES public.application_credentials (id) ON DELETE RESTRICT,
            CONSTRAINT "FK_formal_credential_rotations_replacement_credential" FOREIGN KEY (replacement_credential_id)
                REFERENCES public.application_credentials (id) ON DELETE RESTRICT,
            CONSTRAINT "FK_formal_credential_rotations_environment" FOREIGN KEY (environment_id)
                REFERENCES public.environments (id) ON DELETE RESTRICT,
            CONSTRAINT ck_formal_credential_rotation CHECK (
        {{ScheduleConstraint}}
            )
        );
        CREATE INDEX "IX_formal_credential_rotations_credential_id"
            ON public.formal_credential_rotations (credential_id);
        CREATE INDEX "IX_formal_credential_rotations_replacement_credential_id"
            ON public.formal_credential_rotations (replacement_credential_id);
        CREATE INDEX "IX_formal_credential_rotations_hard_deadline_id"
            ON public.formal_credential_rotations (hard_deadline, id);
        CREATE INDEX "IX_formal_credential_rotations_environment_id_hard_deadline"
            ON public.formal_credential_rotations (environment_id, hard_deadline);

        CREATE FUNCTION public.formal_credential_rotation_immutable() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog AS $$
        BEGIN
            RAISE EXCEPTION 'formal_credential_rotation_immutable' USING ERRCODE='23514';
        END;
        $$;
        CREATE TRIGGER formal_credential_rotation_no_change BEFORE UPDATE OR DELETE ON public.formal_credential_rotations
            FOR EACH ROW EXECUTE FUNCTION public.formal_credential_rotation_immutable();
        CREATE TRIGGER formal_credential_rotation_no_truncate BEFORE TRUNCATE ON public.formal_credential_rotations
            FOR EACH STATEMENT EXECUTE FUNCTION public.formal_credential_rotation_immutable();
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(InstallSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $WEBAPI_FORMAL_ROTATION_DOWN$ BEGIN
            RAISE EXCEPTION 'formal_credential_rotation_down_blocked' USING ERRCODE='23514';
        END $WEBAPI_FORMAL_ROTATION_DOWN$;
        """);
}

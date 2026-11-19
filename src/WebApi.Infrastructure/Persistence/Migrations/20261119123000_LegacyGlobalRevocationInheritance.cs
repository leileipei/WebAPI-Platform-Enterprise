using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WebApi.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WebApiDbContext))]
[Migration(LegacyGlobalRevocationInheritance.Id)]
public sealed class LegacyGlobalRevocationInheritance : Migration
{
    internal const string Id = "20261119123000_LegacyGlobalRevocationInheritance";

    internal const string EventConstraint = """
        epoch > 0 AND length(previous_hash)=64 AND length(event_hash)=64 AND (
            (event_type='CredentialRevoked' AND credential_id IS NOT NULL AND policy_json IS NULL)
            OR (event_type='EnvironmentPolicyChanged' AND credential_id IS NULL AND policy_json IS NOT NULL)
            OR (event_type='CredentialRevocationInherited' AND credential_id IS NOT NULL AND policy_json IS NULL)
            OR (event_type='CredentialRevokedArchived' AND credential_id IS NOT NULL AND policy_json IS NOT NULL
                AND pg_catalog.jsonb_typeof(policy_json)='string'
                AND (policy_json #>> '{}') COLLATE "C" ~ '^[0-9a-f]{64}$')
        )
        """;

    internal const string InstallSql = $$"""
        ALTER TABLE public.security_events DROP CONSTRAINT ck_security_event;
        ALTER TABLE public.security_events ADD CONSTRAINT ck_security_event CHECK (
        {{EventConstraint}}
        );

        CREATE OR REPLACE FUNCTION public.credential_tombstone_matches_event() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog AS $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM public.security_events e
                    WHERE e.environment_id=NEW.environment_id AND e.security_generation=NEW.security_generation
                      AND e.epoch=NEW.epoch AND e.operation_id=NEW.operation_id AND e.credential_id=NEW.credential_id
                      AND e.organization_id=NEW.organization_id AND e.project_id=NEW.project_id AND e.occurred_at=NEW.revoked_at
                      AND e.event_type IN ('CredentialRevoked','CredentialRevocationInherited','CredentialRevokedArchived')) THEN
                RAISE EXCEPTION 'credential_tombstone_event_mismatch';
            END IF;
            RETURN NEW;
        END;
        $$;

        CREATE FUNCTION public.security_legacy_global_revocation_guard() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog AS $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE pid=pg_catalog.pg_backend_pid()
                    AND locktype='advisory' AND mode='ExclusiveLock' AND classid=0 AND objid=8901202 AND objsubid=1 AND granted) THEN
                RAISE EXCEPTION 'security_legacy_global_revocation_fence_required' USING ERRCODE='23514';
            END IF;
            PERFORM pg_catalog.pg_advisory_xact_lock(8901202);

            IF NOT EXISTS (SELECT 1
                    FROM public.security_operations o
                    JOIN public.application_credentials c ON c.id=o.credential_id AND c.application_id=o.application_id
                        AND c.organization_id=o.organization_id
                    JOIN public.applications a ON a.id=c.application_id AND a.organization_id=c.organization_id
                    WHERE o.id=NEW.operation_id AND o.organization_id=NEW.organization_id AND o.credential_id=NEW.credential_id
                      AND o.kind='CredentialRevoke' AND o.committed_at=NEW.occurred_at
                      AND c.binding_mode='LegacyGlobal' AND c.environment_id IS NULL
                      AND (c.project_id IS NULL OR c.project_id=NEW.project_id)
                      AND (a.project_id IS NULL OR a.project_id=NEW.project_id)) THEN
                RAISE EXCEPTION 'security_legacy_global_revocation_scope_mismatch' USING ERRCODE='23514';
            END IF;

            IF NEW.event_type='CredentialRevocationInherited' THEN
                IF NOT EXISTS (SELECT 1 FROM public.security_events origin
                        WHERE origin.operation_id=NEW.operation_id AND origin.organization_id=NEW.organization_id
                          AND origin.credential_id=NEW.credential_id AND origin.occurred_at=NEW.occurred_at
                          AND origin.environment_id<>NEW.environment_id
                          AND origin.event_type IN ('CredentialRevoked','CredentialRevokedArchived')) THEN
                    RAISE EXCEPTION 'security_legacy_global_revocation_origin_missing' USING ERRCODE='23514';
                END IF;
            ELSE
                -- A fresh statement snapshot is required after locking the environment
                -- and its nodes so an older snapshot cannot hide a newly registered node.
                IF pg_catalog.current_setting('transaction_isolation')<>'read committed' THEN
                    RAISE EXCEPTION 'security_archived_revocation_requires_read_committed' USING ERRCODE='23514';
                END IF;
                PERFORM id FROM public.environments
                    WHERE id=NEW.environment_id AND project_id=NEW.project_id AND status='Disabled' FOR UPDATE;
                IF NOT FOUND THEN
                    RAISE EXCEPTION 'security_archived_revocation_ingress_unsealed' USING ERRCODE='23514';
                END IF;
                PERFORM id FROM public.gateway_nodes WHERE environment_id=NEW.environment_id ORDER BY id FOR UPDATE;
                IF EXISTS (SELECT 1 FROM public.gateway_nodes WHERE environment_id=NEW.environment_id AND enabled) THEN
                    RAISE EXCEPTION 'security_archived_revocation_ingress_unsealed' USING ERRCODE='23514';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER security_legacy_global_revocation_event_guard BEFORE INSERT ON public.security_events
            FOR EACH ROW WHEN (NEW.event_type IN ('CredentialRevocationInherited','CredentialRevokedArchived'))
            EXECUTE FUNCTION public.security_legacy_global_revocation_guard();

        CREATE OR REPLACE FUNCTION public.gateway_registration_history_immutable() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog AS $$
        BEGIN
            IF TG_OP='TRUNCATE' THEN
                RAISE EXCEPTION 'gateway_registration_history_immutable' USING ERRCODE='23514';
            END IF;
            IF OLD.event_type='registered' THEN
                RAISE EXCEPTION 'gateway_registration_history_immutable' USING ERRCODE='23514';
            END IF;
            IF TG_OP='UPDATE' AND NEW.event_type='registered' THEN
                RAISE EXCEPTION 'gateway_registration_history_immutable' USING ERRCODE='23514';
            END IF;
            IF OLD.event_type='environment_archive_retired' OR
                    (TG_OP='UPDATE' AND NEW.event_type='environment_archive_retired') THEN
                RAISE EXCEPTION 'gateway_archive_retirement_immutable' USING ERRCODE='23514';
            END IF;
            IF TG_OP='DELETE' THEN RETURN OLD; END IF;
            RETURN NEW;
        END;
        $$;
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(InstallSql);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $WEBAPI_LEGACY_GLOBAL_REVOCATION_DOWN$ BEGIN
            RAISE EXCEPTION 'legacy_global_revocation_inheritance_down_blocked' USING ERRCODE='23514';
        END $WEBAPI_LEGACY_GLOBAL_REVOCATION_DOWN$;
        """);
}

using Microsoft.EntityFrameworkCore.Migrations;
namespace WebApi.Infrastructure.Persistence.Migrations;
// Guards reviewed from the governance source; route-mode backfill resolves the current mainline policy.
internal static class Governance24RouteGuards
{
    internal static void Install(MigrationBuilder m)=>m.Sql("""
        CREATE FUNCTION route_effective_auth_mode(route uuid) RETURNS text LANGUAGE sql STABLE AS $$
          SELECT COALESCE((SELECT p.config::jsonb->>'mode' FROM route_policy_bindings b JOIN policies p ON p.id=b.policy_id
            WHERE b.route_id=route AND p.type='authentication' AND p.enabled ORDER BY b.priority,p.id LIMIT 1),'ApiKey')
        $$;
        UPDATE api_routes SET authentication_mode=route_effective_auth_mode(id);
        CREATE FUNCTION route_authorization_guard() RETURNS trigger LANGUAGE plpgsql AS $$ DECLARE actual uuid; BEGIN
          SELECT api_id INTO actual FROM api_versions WHERE id=NEW.api_version_id;
          IF TG_OP='INSERT' AND NEW.api_id='00000000-0000-0000-0000-000000000000'::uuid THEN NEW.api_id=actual; END IF;
          IF NEW.api_id IS DISTINCT FROM actual THEN RAISE EXCEPTION 'route_api_mismatch' USING ERRCODE='23514'; END IF;
          IF TG_OP='UPDATE' THEN
            IF NEW.environment_id<>OLD.environment_id THEN RAISE EXCEPTION 'route_environment_immutable' USING ERRCODE='23514'; END IF;
            IF (NEW.path,NEW.normalized_path,NEW.methods,NEW.api_version_id,NEW.authentication_mode)
               IS DISTINCT FROM (OLD.path,OLD.normalized_path,OLD.methods,OLD.api_version_id,OLD.authentication_mode)
            THEN NEW.authorization_revision=OLD.authorization_revision+1;
            ELSIF NEW.authorization_revision<>OLD.authorization_revision THEN RAISE EXCEPTION 'route_authorization_revision_immutable' USING ERRCODE='23514'; END IF;
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER route_authorization_check BEFORE INSERT OR UPDATE ON api_routes FOR EACH ROW EXECUTE FUNCTION route_authorization_guard();
        CREATE FUNCTION route_auth_binding_guard() RETURNS trigger LANGUAGE plpgsql AS $$ DECLARE route uuid; actual text; BEGIN
          IF TG_TABLE_NAME='policies' THEN
            FOR route IN SELECT b.route_id FROM route_policy_bindings b WHERE b.policy_id=NEW.id LOOP
              actual=route_effective_auth_mode(route);
              UPDATE api_routes SET authentication_mode=actual,revision=revision+1 WHERE id=route AND authentication_mode<>actual;
            END LOOP;
          ELSE
            FOR route IN SELECT DISTINCT x FROM unnest(CASE WHEN TG_OP='INSERT' THEN ARRAY[NEW.route_id] WHEN TG_OP='DELETE' THEN ARRAY[OLD.route_id] ELSE ARRAY[NEW.route_id,OLD.route_id] END) x LOOP
              actual=route_effective_auth_mode(route);
              UPDATE api_routes SET authentication_mode=actual,revision=revision+1 WHERE id=route AND authentication_mode<>actual;
            END LOOP;
          END IF;
          RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER policy_route_auth_check AFTER UPDATE ON policies DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION route_auth_binding_guard();
        CREATE CONSTRAINT TRIGGER binding_route_auth_check AFTER INSERT OR UPDATE OR DELETE ON route_policy_bindings DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION route_auth_binding_guard();
        CREATE FUNCTION permission_parent_guard() RETURNS trigger LANGUAGE plpgsql AS $$ DECLARE actualproject uuid; actualorg uuid; BEGIN
          NEW.revision_transaction_id=txid_current();
          SELECT e.project_id,p.organization_id INTO actualproject,actualorg FROM environments e JOIN projects p ON p.id=e.project_id WHERE e.id=NEW.environment_id;
          IF TG_OP='INSERT' THEN
            IF NEW.project_id='00000000-0000-0000-0000-000000000000'::uuid THEN NEW.project_id=actualproject; END IF;
            IF NEW.organization_id='00000000-0000-0000-0000-000000000000'::uuid THEN NEW.organization_id=actualorg; END IF;
          END IF;
          IF (NEW.project_id,NEW.organization_id) IS DISTINCT FROM (actualproject,actualorg) THEN RAISE EXCEPTION 'permission_scope_mismatch' USING ERRCODE='23514'; END IF;
          IF TG_OP='UPDATE' THEN
            IF (NEW.application_id,NEW.environment_id,NEW.api_id,NEW.project_id,NEW.organization_id) IS DISTINCT FROM (OLD.application_id,OLD.environment_id,OLD.api_id,OLD.project_id,OLD.organization_id)
            THEN RAISE EXCEPTION 'permission_scope_immutable' USING ERRCODE='23514'; END IF;
            NEW.revision=GREATEST(NEW.revision,OLD.revision+1);
          END IF;
          IF NOT EXISTS(SELECT 1 FROM applications a JOIN apis api ON api.id=NEW.api_id JOIN environments e ON e.id=NEW.environment_id
              JOIN projects p ON p.id=e.project_id WHERE a.id=NEW.application_id AND a.organization_id=api.organization_id
              AND p.organization_id=api.organization_id AND api.project_id=e.project_id AND (a.project_id IS NULL OR a.project_id=api.project_id))
          THEN RAISE EXCEPTION 'permission_scope_mismatch' USING ERRCODE='23514'; END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER permission_parent_check BEFORE INSERT OR UPDATE ON application_api_permissions FOR EACH ROW EXECUTE FUNCTION permission_parent_guard();
        CREATE FUNCTION permission_application_scope_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF EXISTS(SELECT 1 FROM application_api_permissions g WHERE g.application_id=NEW.id AND (g.organization_id<>NEW.organization_id OR NEW.project_id IS NOT NULL AND g.project_id<>NEW.project_id))
          THEN RAISE EXCEPTION 'permission_application_scope_mismatch' USING ERRCODE='23514'; END IF;
          RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER permission_application_scope_check AFTER UPDATE ON applications DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION permission_application_scope_guard();
        CREATE FUNCTION route_grant_approval_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF TG_OP='UPDATE' AND (NEW.permission_id,NEW.environment_id,NEW.api_id,NEW.route_id,NEW.method)
              IS DISTINCT FROM (OLD.permission_id,OLD.environment_id,OLD.api_id,OLD.route_id,OLD.method)
          THEN RAISE EXCEPTION 'route_grant_identity_immutable' USING ERRCODE='23514'; END IF;
          IF NOT EXISTS(SELECT 1 FROM api_routes r WHERE r.id=NEW.route_id AND r.environment_id=NEW.environment_id AND r.api_id=NEW.api_id
            AND r.authorization_revision=NEW.authorization_revision AND NEW.method=ANY(r.methods))
          THEN RAISE EXCEPTION 'route_grant_unapproved_identity' USING ERRCODE='23514'; END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER route_grant_approval_check BEFORE INSERT OR UPDATE ON application_route_method_grants FOR EACH ROW EXECUTE FUNCTION route_grant_approval_guard();
        CREATE FUNCTION route_grant_parent_guard() RETURNS trigger LANGUAGE plpgsql AS $$ DECLARE target uuid; parentmode text; parentxid bigint; BEGIN
          target=CASE WHEN TG_OP='DELETE' THEN OLD.permission_id ELSE NEW.permission_id END;
          SELECT mode,revision_transaction_id INTO parentmode,parentxid FROM application_api_permissions WHERE id=target;
          IF FOUND THEN
            IF TG_OP<>'DELETE' AND parentmode<>'RouteMethod' THEN RAISE EXCEPTION 'route_grant_parent_mode' USING ERRCODE='23514'; END IF;
            IF parentxid<>txid_current() THEN RAISE EXCEPTION 'route_grant_parent_revision_transaction_required' USING ERRCODE='23514'; END IF;
          END IF;
          RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER route_grant_parent_check AFTER INSERT OR UPDATE OR DELETE ON application_route_method_grants
          DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION route_grant_parent_guard();
        CREATE FUNCTION permission_mode_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF EXISTS(SELECT 1 FROM application_api_permissions p JOIN application_route_method_grants g ON g.permission_id=p.id WHERE p.id=NEW.id AND p.mode<>'RouteMethod')
          THEN RAISE EXCEPTION 'route_grant_parent_mode' USING ERRCODE='23514'; END IF;
          RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER permission_mode_check AFTER INSERT OR UPDATE ON application_api_permissions
          DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION permission_mode_guard();
        """);
}

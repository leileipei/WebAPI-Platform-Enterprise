using Microsoft.EntityFrameworkCore.Migrations;

namespace WebApi.Infrastructure.Persistence.Migrations;

internal static class Governance24SecurityGuards
{
    internal static void Install(MigrationBuilder migration) => migration.Sql("""
        CREATE FUNCTION security_facts_immutable() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          RAISE EXCEPTION 'security_fact_immutable';
        END $$;
        CREATE TRIGGER security_event_immutable BEFORE UPDATE OR DELETE ON security_events FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
        CREATE TRIGGER credential_tombstone_immutable BEFORE UPDATE OR DELETE ON credential_revocations FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
        CREATE TRIGGER security_operation_immutable BEFORE UPDATE OR DELETE ON security_operations FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
        CREATE TRIGGER security_ack_immutable BEFORE UPDATE OR DELETE ON security_acks FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
        CREATE TRIGGER security_ack_attempt_immutable BEFORE UPDATE OR DELETE ON security_ack_attempts FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();
        CREATE TRIGGER security_alert_immutable BEFORE UPDATE OR DELETE ON security_operation_alerts FOR EACH ROW EXECUTE FUNCTION security_facts_immutable();

        CREATE FUNCTION security_initial_policy_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF TG_OP='INSERT' THEN
            IF NOT EXISTS (SELECT 1 FROM environment_security_initializations i
              WHERE i.environment_id=NEW.environment_id AND i.project_id=NEW.project_id AND i.organization_id=NEW.organization_id
                AND i.state IN ('FreshNonStrict','VerifiedLegacyNetwork','Governed'))
            THEN RAISE EXCEPTION 'security_environment_initialization_unverified'; END IF;
            IF NEW.epoch<>0 THEN RAISE EXCEPTION 'security_genesis_required'; END IF;
            IF NEW.initial_require_environment_credentials IS NOT NULL AND NEW.initial_require_environment_credentials<>NEW.require_environment_credentials
            THEN RAISE EXCEPTION 'security_genesis_mismatch'; END IF;
            NEW.initial_require_environment_credentials:=NEW.require_environment_credentials;
          ELSIF NEW.initial_require_environment_credentials IS DISTINCT FROM OLD.initial_require_environment_credentials THEN
            RAISE EXCEPTION 'security_initial_policy_immutable';
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER security_initial_policy_guard BEFORE INSERT OR UPDATE ON environment_security_states FOR EACH ROW EXECUTE FUNCTION security_initial_policy_guard();

        CREATE FUNCTION security_state_monotonic() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'security_state_delete_blocked'; END IF;
          IF NEW.environment_id<>OLD.environment_id OR NEW.organization_id<>OLD.organization_id OR NEW.project_id<>OLD.project_id
            OR NEW.security_generation<>OLD.security_generation OR NEW.epoch<OLD.epoch
            OR (NEW.epoch=OLD.epoch AND NEW.head_hash<>OLD.head_hash)
            OR NEW.revision<=OLD.revision
            OR (OLD.require_environment_credentials AND NOT NEW.require_environment_credentials)
            OR array_position(ARRAY['2.0','2.1','2.2','2.3','2.4'],NEW.minimum_snapshot_schema)
              < array_position(ARRAY['2.0','2.1','2.2','2.3','2.4'],OLD.minimum_snapshot_schema)
            OR ((NEW.require_environment_credentials,NEW.minimum_snapshot_schema,NEW.lease_seconds,NEW.lease_refresh_seconds,NEW.maximum_clock_skew_seconds)
              IS DISTINCT FROM (OLD.require_environment_credentials,OLD.minimum_snapshot_schema,OLD.lease_seconds,OLD.lease_refresh_seconds,OLD.maximum_clock_skew_seconds)
              AND NEW.epoch<=OLD.epoch)
          THEN RAISE EXCEPTION 'security_state_regression'; END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER security_state_guard BEFORE UPDATE OR DELETE ON environment_security_states FOR EACH ROW EXECUTE FUNCTION security_state_monotonic();

        CREATE FUNCTION credential_working_revocation_terminal() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF OLD.status='Revoked' AND (NEW.status<>'Revoked' OR NEW.revoked_at IS DISTINCT FROM OLD.revoked_at)
          THEN RAISE EXCEPTION 'credential_revocation_terminal'; END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER credential_working_revocation_terminal BEFORE UPDATE ON application_credentials FOR EACH ROW EXECUTE FUNCTION credential_working_revocation_terminal();

        CREATE FUNCTION security_target_frozen() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'security_target_delete_blocked'; END IF;
          IF NEW.operation_id<>OLD.operation_id OR NEW.environment_id<>OLD.environment_id OR NEW.node_id<>OLD.node_id
            OR NEW.instance_guid<>OLD.instance_guid OR NEW.challenge_nonce<>OLD.challenge_nonce OR NEW.created_at<>OLD.created_at
            OR NEW.protocol_supported_at_freeze<>OLD.protocol_supported_at_freeze THEN RAISE EXCEPTION 'security_target_identity_immutable'; END IF;
          IF OLD.last_lease_until IS NOT NULL AND (NEW.last_lease_until IS NULL OR NEW.last_lease_until<OLD.last_lease_until)
          THEN RAISE EXCEPTION 'security_target_lease_regression'; END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER security_target_frozen BEFORE UPDATE OR DELETE ON security_operation_targets FOR EACH ROW EXECUTE FUNCTION security_target_frozen();

        CREATE FUNCTION credential_tombstone_matches_event() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF NOT EXISTS(SELECT 1 FROM security_events e WHERE e.environment_id=NEW.environment_id AND e.security_generation=NEW.security_generation
            AND e.epoch=NEW.epoch AND e.operation_id=NEW.operation_id AND e.credential_id=NEW.credential_id
            AND e.organization_id=NEW.organization_id AND e.project_id=NEW.project_id AND e.occurred_at=NEW.revoked_at AND e.event_type='CredentialRevoked')
          THEN RAISE EXCEPTION 'credential_tombstone_event_mismatch'; END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER credential_tombstone_event_guard BEFORE INSERT ON credential_revocations FOR EACH ROW EXECUTE FUNCTION credential_tombstone_matches_event();

        CREATE FUNCTION gateway_security_lease_monotonic() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'security_lease_history_delete_blocked'; END IF;
          IF NEW.node_id<>OLD.node_id OR NEW.instance_id<>OLD.instance_id OR NEW.environment_id<>OLD.environment_id
            OR NEW.project_id<>OLD.project_id OR NEW.organization_id<>OLD.organization_id OR NEW.security_generation<>OLD.security_generation
            OR NEW.epoch<OLD.epoch OR NEW.issued_at<OLD.issued_at OR NEW.last_issued_lease_until<OLD.last_issued_lease_until
            OR NEW.last_issued_lease_until<NEW.fresh_until OR NEW.revision<=OLD.revision
          THEN RAISE EXCEPTION 'security_lease_history_regression'; END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER gateway_security_lease_monotonic BEFORE UPDATE OR DELETE ON gateway_security_leases FOR EACH ROW EXECUTE FUNCTION gateway_security_lease_monotonic();

        CREATE FUNCTION security_ack_matches_target() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF NOT EXISTS(SELECT 1 FROM security_operation_targets t WHERE t.operation_id=NEW.operation_id AND t.environment_id=NEW.environment_id
            AND t.node_id=NEW.node_id AND t.instance_guid=NEW.instance_guid AND t.challenge_nonce=NEW.challenge_nonce)
          THEN RAISE EXCEPTION 'security_ack_challenge_mismatch'; END IF;
          IF TG_TABLE_NAME='security_acks' THEN
            IF NOT EXISTS(SELECT 1 FROM security_events e JOIN environment_security_states s ON s.environment_id=e.environment_id
              WHERE e.operation_id=NEW.operation_id AND e.environment_id=NEW.environment_id AND e.security_generation=NEW.security_generation
                AND s.security_generation=NEW.security_generation AND e.epoch<=NEW.epoch AND NEW.epoch<=s.epoch)
            THEN RAISE EXCEPTION 'security_ack_epoch_mismatch'; END IF;
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER security_ack_matches_target BEFORE INSERT ON security_acks FOR EACH ROW EXECUTE FUNCTION security_ack_matches_target();
        CREATE TRIGGER security_ack_attempt_matches_target BEFORE INSERT ON security_ack_attempts FOR EACH ROW EXECUTE FUNCTION security_ack_matches_target();

        CREATE FUNCTION current_security_receipt_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
          IF TG_OP='DELETE' THEN RAISE EXCEPTION 'accepted_security_proof_immutable' USING ERRCODE='23514'; END IF;
          IF TG_OP='UPDATE' THEN
            IF ROW(NEW.node_id,NEW.instance_id,NEW.environment_id,NEW.project_id,NEW.organization_id,NEW.security_generation)
              IS DISTINCT FROM ROW(OLD.node_id,OLD.instance_id,OLD.environment_id,OLD.project_id,OLD.organization_id,OLD.security_generation)
              OR NEW.epoch<OLD.epoch OR NEW.received_at<OLD.received_at
              OR NEW.epoch=OLD.epoch AND (NEW.head_hash<>OLD.head_hash OR NEW.fresh_until<OLD.fresh_until
                OR NEW.fresh_until=OLD.fresh_until AND ROW(NEW.checkpoint_hash,NEW.lease_id) IS DISTINCT FROM ROW(OLD.checkpoint_hash,OLD.lease_id))
            THEN RAISE EXCEPTION 'accepted_security_proof_regression' USING ERRCODE='23514'; END IF;
          END IF;
          IF NOT EXISTS(SELECT 1 FROM gateway_security_leases l JOIN environment_security_states e ON e.environment_id=l.environment_id
            WHERE l.node_id=NEW.node_id AND l.instance_id=NEW.instance_id AND l.environment_id=NEW.environment_id AND l.project_id=NEW.project_id
              AND l.organization_id=NEW.organization_id AND l.security_generation=NEW.security_generation AND e.security_generation=NEW.security_generation
              AND e.epoch=NEW.epoch AND e.head_hash=NEW.head_hash AND l.epoch>=NEW.epoch AND l.last_issued_lease_until>=NEW.fresh_until
              AND NEW.received_at>=l.issued_at-interval '2 seconds')
          THEN RAISE EXCEPTION 'accepted_security_proof_scope_unproven' USING ERRCODE='23514'; END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER current_security_receipt_authority BEFORE INSERT OR UPDATE OR DELETE ON gateway_security_current_receipts
          FOR EACH ROW EXECUTE FUNCTION current_security_receipt_guard();
        """);
}

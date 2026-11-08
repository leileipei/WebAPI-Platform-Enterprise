using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Governance24TrustLedgerBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS(SELECT 1 FROM environment_security_states) OR EXISTS(SELECT 1 FROM security_events)
                    OR EXISTS(SELECT 1 FROM security_operations) OR EXISTS(SELECT 1 FROM credential_revocations)
                  THEN RAISE EXCEPTION 'trust_binding_requires_offline_review'; END IF;
                END $$;

                CREATE OR REPLACE FUNCTION environment_initialization_transition_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  IF TG_OP='DELETE' THEN
                    RAISE EXCEPTION 'environment_initialization_deletion_blocked' USING ERRCODE='23514';
                  END IF;
                  IF (NEW.environment_id,NEW.project_id,NEW.organization_id) IS DISTINCT FROM
                     (OLD.environment_id,OLD.project_id,OLD.organization_id) OR NEW.revision<>OLD.revision+1 THEN
                    RAISE EXCEPTION 'environment_initialization_identity_or_revision_invalid' USING ERRCODE='23514';
                  END IF;
                  IF (OLD.state='Unknown' AND NEW.state IN ('FreshNonStrict','Governed')) OR
                     (OLD.state='Governed' AND NEW.state<>'Governed') OR
                     (OLD.state IN ('FreshNonStrict','VerifiedLegacyNetwork') AND NEW.state NOT IN (OLD.state,'Governed')) THEN
                    RAISE EXCEPTION 'environment_initialization_regression' USING ERRCODE='23514';
                  END IF;
                  IF NEW.state='Governed' AND OLD.state<>'Governed' AND NOT EXISTS (
                    SELECT 1 FROM environment_security_states s WHERE s.environment_id=NEW.environment_id
                      AND s.project_id=NEW.project_id AND s.organization_id=NEW.organization_id)
                  THEN RAISE EXCEPTION 'environment_initialization_ledger_missing' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;

                CREATE FUNCTION environment_initialization_insert_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  IF NEW.state NOT IN ('Unknown','FreshNonStrict') OR
                    (NEW.state='FreshNonStrict' AND NEW.fresh_proof_id='00000000-0000-0000-0000-000000000000'::uuid)
                  THEN RAISE EXCEPTION 'environment_initialization_requires_verified_creator' USING ERRCODE='23514'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER environment_initialization_insert_check BEFORE INSERT ON environment_security_initializations
                  FOR EACH ROW EXECUTE FUNCTION environment_initialization_insert_guard();

                CREATE OR REPLACE FUNCTION security_initial_policy_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                  IF TG_OP='INSERT' THEN
                    IF NOT EXISTS (SELECT 1 FROM environment_security_initializations i
                      WHERE i.environment_id=NEW.environment_id AND i.project_id=NEW.project_id AND i.organization_id=NEW.organization_id
                        AND i.state IN ('FreshNonStrict','VerifiedLegacyNetwork','Governed'))
                    THEN RAISE EXCEPTION 'security_environment_initialization_unverified'; END IF;
                    IF NEW.epoch<>0 THEN RAISE EXCEPTION 'security_genesis_required'; END IF;
                    IF NEW.head_hash<>'4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945'
                    THEN RAISE EXCEPTION 'security_genesis_hash_mismatch'; END IF;
                    IF NEW.initial_require_environment_credentials IS NOT NULL AND NEW.initial_require_environment_credentials<>NEW.require_environment_credentials
                    THEN RAISE EXCEPTION 'security_genesis_mismatch'; END IF;
                    NEW.initial_require_environment_credentials:=NEW.require_environment_credentials;
                  ELSIF NEW.initial_require_environment_credentials IS DISTINCT FROM OLD.initial_require_environment_credentials THEN
                    RAISE EXCEPTION 'security_initial_policy_immutable';
                  END IF;
                  RETURN NEW;
                END $$;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'governance_downgrade_blocked: use verified forward recovery'; END $$;");

        }
    }
}

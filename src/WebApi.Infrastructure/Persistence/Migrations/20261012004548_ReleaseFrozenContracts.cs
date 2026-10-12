using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseFrozenContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "release_frozen_contracts",
                columns: table => new
                {
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    config_version = table.Column<long>(type: "bigint", nullable: false),
                    api_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    frozen_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_release_frozen_contracts", x => new { x.release_id, x.ordinal });
                    table.CheckConstraint("ck_frozen_contract_identity", "COALESCE(jsonb_typeof(frozen_json->'api')='object' AND jsonb_typeof(frozen_json->'version')='object' AND jsonb_typeof(frozen_json->'parameters')='array' AND jsonb_typeof(frozen_json->'schemas')='array' AND frozen_json->'api'->>'id'=api_id::text AND frozen_json->'version'->>'id'=api_version_id::text AND frozen_json->'version'->>'apiId'=api_id::text, false)");
                    table.CheckConstraint("ck_frozen_contract_version", "config_version > 0 AND ordinal > 0");
                    table.ForeignKey(
                        name: "FK_release_frozen_contracts_release_records_release_id_environ~",
                        columns: x => new { x.release_id, x.environment_id },
                        principalTable: "release_records",
                        principalColumns: new[] { "id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_frozen_contract_lookup",
                table: "release_frozen_contracts",
                columns: new[] { "environment_id", "api_id", "api_version_id", "config_version", "release_id", "ordinal" },
                descending: new[] { false, false, false, true, false, false });

            migrationBuilder.CreateIndex(
                name: "IX_release_frozen_contracts_release_id_environment_id",
                table: "release_frozen_contracts",
                columns: new[] { "release_id", "environment_id" });

            // Project one immutable API contract at a time. Preserve the original publish
            // eligibility, including failed deployments with an assigned config version.
            // This migration is transactional; bad legacy candidates block it without
            // rewriting the candidate or substituting mutable catalog definitions.
            migrationBuilder.Sql("""
                -- Frozen snapshot of the DTO type shapes: '?' allows null/missing;
                -- '~' allows missing optional constructor arguments with defaults only.
                -- The migration helper is created and dropped in this same transaction.
                CREATE FUNCTION public.webapi_frozen_shape_20261012004548(doc jsonb, kind text, shapes jsonb)
                RETURNS boolean LANGUAGE plpgsql AS $shape$
                DECLARE nullable boolean; optional boolean; field record; item jsonb; text_value text;
                BEGIN
                  nullable := right(kind,1)='?'; optional := right(kind,1)='~';
                  IF nullable OR optional THEN kind:=left(kind,length(kind)-1); END IF;
                  IF doc IS NULL THEN RETURN nullable OR optional; END IF;
                  IF jsonb_typeof(doc)='null' THEN RETURN nullable; END IF;
                  IF right(kind,2)='[]' THEN
                    IF jsonb_typeof(doc)<>'array' THEN RETURN false; END IF;
                    FOR item IN SELECT value FROM jsonb_array_elements(doc) LOOP
                      IF NOT public.webapi_frozen_shape_20261012004548(item,left(kind,length(kind)-2),shapes)
                        THEN RETURN false; END IF;
                    END LOOP;
                    RETURN true;
                  END IF;
                  IF shapes ? kind THEN
                    IF jsonb_typeof(doc)<>'object' THEN RETURN false; END IF;
                    FOR field IN SELECT key,value FROM jsonb_each_text(shapes->kind) LOOP
                      IF NOT public.webapi_frozen_shape_20261012004548(doc->field.key,field.value,shapes)
                        THEN RETURN false; END IF;
                    END LOOP;
                    RETURN true;
                  END IF;
                  text_value:=doc #>> '{}';
                  CASE kind
                    WHEN 'string' THEN RETURN jsonb_typeof(doc)='string';
                    WHEN 'boolean' THEN RETURN jsonb_typeof(doc)='boolean';
                    WHEN 'uuid' THEN
                      RETURN jsonb_typeof(doc)='string' AND text_value ~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';
                    WHEN 'int32' THEN
                      IF jsonb_typeof(doc)<>'number' OR text_value !~ '^-?[0-9]+$' THEN RETURN false; END IF;
                      PERFORM text_value::integer; RETURN true;
                    WHEN 'int64' THEN
                      IF jsonb_typeof(doc)<>'number' OR text_value !~ '^-?[0-9]+$' THEN RETURN false; END IF;
                      PERFORM text_value::bigint; RETURN true;
                    WHEN 'timestamp' THEN
                      IF jsonb_typeof(doc)<>'string' OR text_value !~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,16})?(Z|[+-]((0[0-9]|1[0-3]):[0-5][0-9]|14:00))$' THEN RETURN false; END IF;
                      PERFORM text_value::timestamptz; RETURN true;
                    ELSE RETURN false;
                  END CASE;
                EXCEPTION WHEN OTHERS THEN RETURN false;
                END $shape$;
                DO $backfill$
                DECLARE source record; candidate jsonb;
                  shapes jsonb := $types${"ApiDto":{"code":"string","description":"string?","groupId":"uuid?","id":"uuid","lifecycleStatus":"string","name":"string","organizationId":"uuid","ownerUserId":"uuid","projectId":"uuid","workingRevision":"int64"},"ApplicationDto":{"activation":"string~","code":"string","id":"uuid","name":"string","organizationId":"uuid","owner":"string","projectId":"uuid?","revision":"int64","status":"string"},"ApplicationPermissionDto":{"activation":"string~","apiId":"uuid","applicationId":"uuid","environmentId":"uuid","expiresAt":"timestamp?","id":"uuid","revision":"int64","validFrom":"timestamp"},"ClusterDto":{"destinations":"DestinationDto[]","environmentId":"uuid","healthCheckEnabled":"boolean","healthCheckIntervalSec":"int32","healthCheckPath":"string","id":"uuid","loadBalancingPolicy":"string","name":"string","projectId":"uuid","revision":"int64","status":"string"},"ComparisonCounts":{"added":"int32","breaking":"int32","changed":"int32","compatible":"int32","removed":"int32","unknown":"int32"},"ContractExampleOption":{"externalValue":"string?","json":"string?","name":"string","unverifiedReason":"string?"},"DestinationDto":{"address":"string","clusterId":"uuid","enabled":"boolean","id":"uuid","metadata":"string?","name":"string","revision":"int64","weight":"int32"},"FrozenApiVersion":{"api":"ApiDto","parameters":"ParameterDto[]","schemas":"SchemaDto[]","version":"VersionDto"},"FrozenApplication":{"application":"ApplicationDto","credentials":"FrozenCredential[]","permissions":"ApplicationPermissionDto[]"},"FrozenBinding":{"policyId":"uuid","priority":"int32","routeId":"uuid"},"FrozenCredential":{"accessKey":"string","expiresAt":"timestamp","id":"uuid","revision":"int64","secretHash":"string","secretLast4":"string","status":"string","validFrom":"timestamp"},"FrozenPolicy":{"config":"string","enabled":"boolean","id":"uuid","revision":"int64","sourcePolicyId":"uuid?","type":"string"},"FrozenReleaseCandidate":{"applications":"FrozenApplication[]","baselineConfigVersion":"int64","bindings":"FrozenBinding[]","clusters":"ClusterDto[]","environmentId":"uuid","organizationId":"uuid","policies":"FrozenPolicy[]","projectId":"uuid","resourceRevisions":"ResourceRevision[]","riskReviewReferences":"FrozenRiskReviewReference[]?","routes":"RouteDto[]","sharedCredentialImpact":"SharedCredentialImpact[]?","versionIds":"uuid[]","versions":"FrozenApiVersion[]"},"FrozenRiskReviewReference":{"apiId":"uuid","comparisonId":"uuid","engineVersion":"string","inputFingerprint":"string","reportHash":"string","reviewId":"uuid","summary":"RiskReviewSummaryDto","targetVersionId":"uuid"},"ParameterDto":{"apiVersionId":"uuid","dataType":"string","description":"string?","exampleJson":"string?","examples":"ContractExampleOption[]?","id":"uuid","location":"string","name":"string","required":"boolean","schema":"string?"},"ResourceRevision":{"id":"uuid","revision":"int64","type":"string"},"RiskReviewSummaryDto":{"actorId":"uuid","apiId":"uuid","comment":"string?","comparisonId":"uuid","counts":"ComparisonCounts","coverage":"string","createdAt":"timestamp","decision":"string","engineVersion":"string","fromVersion":"string","fromVersionId":"uuid","inputFingerprint":"string","reportHash":"string","reviewId":"uuid","toVersion":"string","toVersionId":"uuid"},"RouteDto":{"apiVersionId":"uuid","clusterId":"uuid","effectiveAuthenticationMode":"string?","effectiveTimeoutMs":"int32?","enabled":"boolean","environmentId":"uuid","id":"uuid","matchOrder":"int32","methods":"string[]","normalizedPath":"string","path":"string","priority":"int32","requireApiKey":"boolean","revision":"int64","routeName":"string","timeoutMs":"int32"},"SchemaDto":{"apiVersionId":"uuid","contentType":"string","exampleJson":"string?","examples":"ContractExampleOption[]?","id":"uuid","name":"string","schemaHash":"string?","schemaJson":"string","schemaType":"string","statusCode":"int32?"},"SharedCredentialImpact":{"acknowledged":"boolean","applicationId":"uuid","requiredCredentialIds":"uuid[]","retainedApiIds":"uuid[]","sharedAcrossEnvironments":"boolean~"},"VersionDto":{"apiId":"uuid","changeType":"string","createdAt":"timestamp","createdBy":"uuid","dialect":"string?","id":"uuid","openapiDocument":"string?","openapiSource":"string?","revision":"int64","schemaHash":"string?","sealedAt":"timestamp?","sourceFormat":"string?","status":"string","version":"string"}}$types$::jsonb;
                BEGIN
                  FOR source IN
                    SELECT id, environment_id, to_config_version, candidate_bytes
                    FROM release_records
                    WHERE release_type='publish' AND to_config_version>0
                      AND candidate_bytes IS NOT NULL AND approval_policy IS NOT NULL
                  LOOP
                    BEGIN
                      candidate := convert_from(source.candidate_bytes,'UTF8')::jsonb;
                      IF NOT public.webapi_frozen_shape_20261012004548(candidate,'FrozenReleaseCandidate',shapes)
                        OR jsonb_typeof(candidate) IS DISTINCT FROM 'object'
                        OR jsonb_typeof(candidate->'versions') IS DISTINCT FROM 'array'
                        OR (candidate->>'environmentId')::uuid IS DISTINCT FROM source.environment_id
                      THEN RAISE EXCEPTION 'Invalid frozen candidate'; END IF;
                      INSERT INTO release_frozen_contracts
                        (release_id, ordinal, environment_id, config_version, api_id, api_version_id, frozen_json)
                      SELECT source.id, v.ordinal::integer, source.environment_id, source.to_config_version,
                        (v.value->'api'->>'id')::uuid, (v.value->'version'->>'id')::uuid, v.value
                      FROM jsonb_array_elements(candidate->'versions') WITH ORDINALITY AS v(value,ordinal);
                    EXCEPTION WHEN OTHERS THEN
                      -- Never echo a candidate containing consumer or credential data.
                      RAISE EXCEPTION 'Invalid legacy frozen contract for release %',source.id
                        USING ERRCODE='22023';
                    END;
                  END LOOP;
                END $backfill$;
                DROP FUNCTION public.webapi_frozen_shape_20261012004548(jsonb,text,jsonb);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "release_frozen_contracts");
        }
    }
}

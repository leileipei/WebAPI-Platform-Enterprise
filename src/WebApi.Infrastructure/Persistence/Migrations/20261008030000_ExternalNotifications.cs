using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExternalNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $notification_upgrade$
                DECLARE item record; value jsonb; names text[];
                BEGIN
                  FOR item IN SELECT s.key, s.value FROM system_settings s WHERE s.key='system.notification' LOOP
                    value:=item.value;
                    IF jsonb_typeof(value) <> 'object' THEN RAISE EXCEPTION 'Invalid legacy notification settings: %',item.key; END IF;
                    SELECT array_agg(key ORDER BY key) INTO names FROM jsonb_object_keys(value) key;
                    IF names IS DISTINCT FROM ARRAY['fromEmail','smtpHost','smtpPort','smtpSecretRef','webhookSecretRef','webhookUrl']::text[] THEN RAISE EXCEPTION 'Invalid legacy notification settings fields: %',item.key; END IF;
                    IF EXISTS(SELECT 1 FROM jsonb_each(value) field WHERE field.key<>'smtpPort' AND jsonb_typeof(field.value) NOT IN ('string','null'))
                       OR jsonb_typeof(value->'smtpPort') NOT IN ('number','null')
                       OR EXISTS(SELECT 1 FROM jsonb_each_text(value) field WHERE field.key<>'smtpPort' AND (length(field.value)>2048 OR field.value ~ '[[:cntrl:]]')) THEN RAISE EXCEPTION 'Invalid legacy notification settings types: %',item.key; END IF;
                    -- Match legacy nullable text decoding only in the validation copy;
                    -- the update below preserves the original six JSON values exactly.
                    value:=value || jsonb_build_object('smtpHost',nullif(value->>'smtpHost',''),'fromEmail',nullif(value->>'fromEmail',''),'webhookUrl',nullif(value->>'webhookUrl',''));
                    IF (value->>'smtpHost' IS NULL) IS DISTINCT FROM (value->>'smtpPort' IS NULL)
                       OR (value->>'smtpHost' IS NULL) IS DISTINCT FROM (value->>'fromEmail' IS NULL)
                       OR (value->>'smtpHost' IS NULL) IS DISTINCT FROM (value->>'smtpSecretRef' IS NULL)
                       OR (value->>'webhookUrl' IS NULL) IS DISTINCT FROM (value->>'webhookSecretRef' IS NULL) THEN RAISE EXCEPTION 'Invalid legacy notification completeness: %',item.key; END IF;
                    IF value->>'smtpHost' IS NOT NULL THEN
                      IF length(value->>'smtpHost') NOT BETWEEN 1 AND 253 OR value->>'smtpHost' ~ '[[:space:]/@?#]'
                         OR value->>'smtpPort' !~ '^[0-9]+$' OR (value->>'smtpPort')::numeric NOT BETWEEN 1 AND 65535
                         OR length(value->>'fromEmail') NOT BETWEEN 3 AND 254 OR position('@' IN value->>'fromEmail')=0
                         OR value->>'smtpSecretRef' !~* '^vault://[^/@?#[:space:]]+/[^?#]*[^/?#][^?#]*$' THEN RAISE EXCEPTION 'Invalid legacy SMTP settings: %',item.key; END IF;
                    END IF;
                    IF value->>'webhookUrl' IS NOT NULL AND (value->>'webhookUrl' !~* '^https://[^/@?#[:space:]]+(/[^?#]*)?$' OR value->>'webhookSecretRef' !~* '^vault://[^/@?#[:space:]]+/[^?#]*[^/?#][^?#]*$') THEN RAISE EXCEPTION 'Invalid legacy Webhook settings: %',item.key; END IF;
                  END LOOP;
                  FOR item IN SELECT id, notification FROM alert_rules LOOP
                    value:=item.notification;
                    IF jsonb_typeof(value)<>'object' OR NOT(value ? 'inConsole' AND value ? 'requestedChannels')
                       OR value->'inConsole' IS DISTINCT FROM 'true'::jsonb OR jsonb_typeof(value->'requestedChannels')<>'array'
                       OR EXISTS(SELECT 1 FROM jsonb_object_keys(value) key WHERE key NOT IN ('inConsole','requestedChannels')) THEN RAISE EXCEPTION 'Invalid legacy rule notification: %',item.id; END IF;
                    IF jsonb_array_length(value->'requestedChannels')>3
                       OR EXISTS(SELECT 1 FROM jsonb_array_elements(value->'requestedChannels') channel WHERE channel NOT IN ('"Email"'::jsonb,'"Webhook"'::jsonb,'"EnterpriseIm"'::jsonb))
                       OR (SELECT count(*)<>count(DISTINCT channel) FROM jsonb_array_elements(value->'requestedChannels') channel) THEN RAISE EXCEPTION 'Invalid legacy rule channels: %',item.id; END IF;
                  END LOOP;
                END $notification_upgrade$;
                UPDATE system_settings SET value=value || '{"smtpEnabled":false,"webhookEnabled":false,"smtpSecurity":"StartTlsRequired"}'::jsonb WHERE key='system.notification';
                UPDATE alert_rules SET notification=notification || '{"externalEnabled":false,"emailRecipients":[],"notifyRecovery":true,"retryPolicy":{"maxAttempts":5,"baseDelaySeconds":30,"maxDelaySeconds":900,"expiresAfterMinutes":1440}}'::jsonb;
                """);
            migrationBuilder.AddColumn<string>(
                name: "frozen_notification",
                table: "alert_events",
                type: "jsonb",
                nullable: false,
                defaultValue: "{\"policy\":{\"inConsole\":true,\"requestedChannels\":[],\"externalEnabled\":false,\"emailRecipients\":[],\"notifyRecovery\":true,\"retryPolicy\":{\"maxAttempts\":5,\"baseDelaySeconds\":30,\"maxDelaySeconds\":900,\"expiresAfterMinutes\":1440}},\"emailProfileId\":null,\"webhookProfileId\":null}");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_alert_events_id_organization_id_project_id_environment_id",
                table: "alert_events",
                columns: new[] { "id", "organization_id", "project_id", "environment_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_alert_event_transitions_id_event_id",
                table: "alert_event_transitions",
                columns: new[] { "id", "event_id" });

            migrationBuilder.CreateTable(
                name: "notification_channel_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    configuration_hash = table.Column<string>(type: "text", nullable: false),
                    private_configuration = table.Column<string>(type: "jsonb", nullable: false),
                    protected_secret_fingerprint = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settings_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_channel_profiles", x => x.id);
                    table.UniqueConstraint("AK_notification_channel_profiles_id_channel", x => new { x.id, x.channel });
                    table.CheckConstraint("ck_notification_profile_channel", "channel IN ('Email','Webhook')");
                    table.CheckConstraint("ck_notification_profile_config", "configuration_hash ~ '^[a-f0-9]{64}$' AND jsonb_typeof(private_configuration)='object' AND octet_length(private_configuration::text) BETWEEN 2 AND 16384 AND length(protected_secret_fingerprint) BETWEEN 1 AND 4096 AND settings_revision > 0");
                    table.ForeignKey(
                        name: "FK_notification_channel_profiles_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_channel_states",
                columns: table => new
                {
                    channel = table.Column<string>(type: "text", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_channel_states", x => x.channel);
                    table.CheckConstraint("ck_notification_channel_state", "channel IN ('Email','Webhook') AND revision > 0 AND (NOT enabled OR profile_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_notification_channel_states_notification_channel_profiles_p~",
                        columns: x => new { x.profile_id, x.channel },
                        principalTable: "notification_channel_profiles",
                        principalColumns: new[] { "id", "channel" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    triggered_delivery_id = table.Column<Guid>(type: "uuid", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    channel = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    target_hash = table.Column<string>(type: "text", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    base_delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    max_delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    expires_after_minutes = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    lease_token = table.Column<long>(type: "bigint", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.id);
                    table.UniqueConstraint("AK_notification_deliveries_id_channel_target_hash", x => new { x.id, x.channel, x.target_hash });
                    table.CheckConstraint("ck_notification_delivery_budget", "max_attempts BETWEEN 1 AND 5 AND base_delay_seconds BETWEEN 1 AND 300 AND max_delay_seconds BETWEEN base_delay_seconds AND 3600 AND expires_after_minutes BETWEEN 5 AND 1440 AND attempt_count BETWEEN 0 AND max_attempts AND expires_at > created_at AND expires_at <= created_at + make_interval(mins => expires_after_minutes) AND revision > 0 AND lease_token >= 0 AND (kind <> 'Test' OR (max_attempts=1 AND expires_after_minutes=5))");
                    table.CheckConstraint("ck_notification_delivery_lease", "(status='Sending' AND lease_owner IS NOT NULL AND length(lease_owner) BETWEEN 1 AND 128 AND lease_until IS NOT NULL AND lease_token > 0 AND attempt_count > 0) OR (status<>'Sending' AND lease_owner IS NULL AND lease_until IS NULL)");
                    table.CheckConstraint("ck_notification_delivery_scope", "(kind='Alert' AND event_id IS NOT NULL AND transition_id IS NOT NULL AND organization_id IS NOT NULL AND project_id IS NOT NULL AND environment_id IS NOT NULL) OR (kind='Test' AND event_id IS NULL AND transition_id IS NULL AND triggered_delivery_id IS NULL AND organization_id IS NULL AND project_id IS NULL AND environment_id IS NULL AND created_by IS NOT NULL)");
                    table.CheckConstraint("ck_notification_delivery_status", "status IN ('Queued','Sending','RetryScheduled','Paused','Accepted','Failed','Suppressed','Expired') AND (profile_id IS NOT NULL OR (status IN ('Suppressed','Expired') AND reason IS NOT NULL)) AND (reason IS NULL OR reason ~ '^[A-Za-z][A-Za-z0-9]{0,63}$')");
                    table.CheckConstraint("ck_notification_delivery_target", "channel IN ('Email','Webhook') AND length(target) BETWEEN 1 AND 2048 AND (channel <> 'Email' OR length(target) <= 254) AND target_hash ~ '^[a-f0-9]{64}$' AND octet_length(payload) BETWEEN 1 AND 16384");
                    table.ForeignKey(
                        name: "FK_notification_deliveries_alert_event_transitions_transition_~",
                        columns: x => new { x.transition_id, x.event_id },
                        principalTable: "alert_event_transitions",
                        principalColumns: new[] { "id", "event_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_alert_events_event_id_organization_~",
                        columns: x => new { x.event_id, x.organization_id, x.project_id, x.environment_id },
                        principalTable: "alert_events",
                        principalColumns: new[] { "id", "organization_id", "project_id", "environment_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notification_channel_profiles_profi~",
                        columns: x => new { x.profile_id, x.channel },
                        principalTable: "notification_channel_profiles",
                        principalColumns: new[] { "id", "channel" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notification_deliveries_triggered_d~",
                        columns: x => new { x.triggered_delivery_id, x.channel, x.target_hash },
                        principalTable: "notification_deliveries",
                        principalColumns: new[] { "id", "channel", "target_hash" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_delivery_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    lease_token = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    code = table.Column<string>(type: "text", nullable: true),
                    protocol_status = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_delivery_attempts", x => x.id);
                    table.CheckConstraint("ck_notification_attempt_budget", "attempt_no BETWEEN 1 AND 5 AND lease_token > 0 AND (completed_at IS NULL OR completed_at >= started_at)");
                    table.CheckConstraint("ck_notification_attempt_outcome", "(completed_at IS NULL AND outcome IS NULL AND code IS NULL AND protocol_status IS NULL) OR (completed_at IS NOT NULL AND outcome IS NOT NULL AND outcome IN ('Accepted','TransientFailure','PermanentFailure','OutcomeUnknown') AND code IS NOT NULL AND code ~ '^[A-Za-z][A-Za-z0-9]{0,63}$' AND (protocol_status IS NULL OR protocol_status BETWEEN 100 AND 599))");
                    table.ForeignKey(
                        name: "FK_notification_delivery_attempts_notification_deliveries_deli~",
                        column: x => x.delivery_id,
                        principalTable: "notification_deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_alert_event_notification",
                table: "alert_events",
                sql: "jsonb_typeof(frozen_notification)='object' AND octet_length(frozen_notification::text) BETWEEN 2 AND 16384");

            migrationBuilder.CreateIndex(
                name: "IX_notification_channel_profiles_channel_created_at",
                table: "notification_channel_profiles",
                columns: new[] { "channel", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_channel_profiles_created_by",
                table: "notification_channel_profiles",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_notification_channel_states_profile_id_channel",
                table: "notification_channel_states",
                columns: new[] { "profile_id", "channel" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_created_by",
                table: "notification_deliveries",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_event_id_created_at_id",
                table: "notification_deliveries",
                columns: new[] { "event_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_event_id_organization_id_project_id~",
                table: "notification_deliveries",
                columns: new[] { "event_id", "organization_id", "project_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_event_id_transition_id_channel_targ~",
                table: "notification_deliveries",
                columns: new[] { "event_id", "transition_id", "channel", "target_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_kind_created_by_channel_created_at",
                table: "notification_deliveries",
                columns: new[] { "kind", "created_by", "channel", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_lease_until",
                table: "notification_deliveries",
                column: "lease_until",
                filter: "status = 'Sending'");

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_profile_id_channel",
                table: "notification_deliveries",
                columns: new[] { "profile_id", "channel" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_status_next_attempt_at_created_at",
                table: "notification_deliveries",
                columns: new[] { "status", "next_attempt_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_transition_id_event_id",
                table: "notification_deliveries",
                columns: new[] { "transition_id", "event_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_triggered_delivery_id_channel_targe~",
                table: "notification_deliveries",
                columns: new[] { "triggered_delivery_id", "channel", "target_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_attempts_delivery_id_attempt_no",
                table: "notification_delivery_attempts",
                columns: new[] { "delivery_id", "attempt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_attempts_delivery_id_lease_token",
                table: "notification_delivery_attempts",
                columns: new[] { "delivery_id", "lease_token" },
                unique: true);
            migrationBuilder.Sql("""
                INSERT INTO notification_channel_states(channel,enabled,revision) VALUES('Email',false,1),('Webhook',false,1);
                CREATE FUNCTION forbid_notification_profile_mutation() RETURNS trigger LANGUAGE plpgsql AS $immutable$
                BEGIN RAISE EXCEPTION 'Notification profiles are immutable' USING ERRCODE='23514'; END $immutable$;
                CREATE TRIGGER immutable_notification_profile BEFORE UPDATE OR DELETE ON notification_channel_profiles FOR EACH ROW EXECUTE FUNCTION forbid_notification_profile_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER immutable_notification_profile ON notification_channel_profiles; DROP FUNCTION forbid_notification_profile_mutation();");
            migrationBuilder.DropTable(
                name: "notification_channel_states");

            migrationBuilder.DropTable(
                name: "notification_delivery_attempts");

            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "notification_channel_profiles");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_alert_events_id_organization_id_project_id_environment_id",
                table: "alert_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_alert_event_notification",
                table: "alert_events");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_alert_event_transitions_id_event_id",
                table: "alert_event_transitions");

            migrationBuilder.DropColumn(
                name: "frozen_notification",
                table: "alert_events");
        }
    }
}

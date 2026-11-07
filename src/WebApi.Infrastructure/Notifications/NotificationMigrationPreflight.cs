using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Domain.Notifications;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Settings;

namespace WebApi.Infrastructure.Notifications;

public static class NotificationMigrationPreflight
{
    private const int PageSize = 16;

    public static async Task<NotificationValidationSummary> ValidateAsync(WebApiDbContext db, MigrationBudget budget, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();
        var legacyFormat = !(await GovernanceMigrationHistory.ReadAppliedAsync(db, ct))
            .Contains("20261008030000_ExternalNotifications", StringComparer.Ordinal);
        var encoding = await db.Database.SqlQueryRaw<string>("SELECT pg_catalog.current_setting('server_encoding') AS \"Value\"").SingleAsync(ct);
        if (!string.Equals(encoding, "UTF8", StringComparison.Ordinal))
            throw new InvalidOperationException("notification_migration_unsupported_encoding");
        var hasSettings = await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.system_settings') IS NOT NULL AS \"Value\"").SingleAsync(ct);
        var hasRules = await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.alert_rules') IS NOT NULL AS \"Value\"").SingleAsync(ct);
        var settingsPresent = false;
        var count = 0;
        long total = 0;

        if (hasSettings)
        {
            var rows = db.Database.SqlQueryRaw<NotificationSettingsProbeRow>("""
                    SELECT pg_catalog.octet_length(value::text) AS "ByteCount",
                           CASE WHEN pg_catalog.octet_length(value::text)<=@max_value
                                  AND pg_catalog.octet_length(value::text)<=@remaining
                                THEN value::text ELSE NULL::text END AS "Payload"
                    FROM public.system_settings WHERE key='system.notification'
                    """, new NpgsqlParameter("max_value", budget.SettingsMaxBytes), new NpgsqlParameter("remaining", budget.TotalMaxBytes));
            await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
            {
                if (settingsPresent) throw InvalidSettings();
                settingsPresent = true;
                if (!row.ByteCount.HasValue) throw InvalidSettings();
                var bytes = row.ByteCount.Value;
                if (bytes > budget.SettingsMaxBytes) throw new NotificationMigrationBudgetException("settings_too_large");
                if (bytes > budget.TotalMaxBytes) throw new NotificationMigrationBudgetException("total_bytes_exceeded");
                if (row.Payload is null) throw InvalidSettings();
                total = checked(total + bytes);
                try
                {
                    if (legacyFormat && !HasExactFields(row.Payload, ["smtpHost", "smtpPort", "fromEmail", "smtpSecretRef", "webhookUrl", "webhookSecretRef"]))
                        throw new InvalidOperationException();
                    SettingsValueCodec.Decode("notification", row.Payload);
                }
                catch (Exception error) when (error is ApiException or InvalidOperationException) { throw InvalidSettings(); }
            }
        }

        if (hasRules)
        {
            Guid last = default;
            var first = true;
            while (true)
            {
                var remainingCount = budget.RuleMaxCount - count;
                var remainingBytes = budget.TotalMaxBytes - total;
                var limit = Math.Min(PageSize, remainingCount + 1);
                var rows = db.Database.SqlQueryRaw<NotificationRuleProbeRow>("""
                        WITH page AS MATERIALIZED (
                          SELECT id,notification,pg_catalog.octet_length(notification::text) AS byte_count
                          FROM public.alert_rules
                          WHERE @first_page OR id>@last_id
                          ORDER BY id LIMIT @page_size
                        ), ranked AS (
                          SELECT id,notification,byte_count,
                            row_number() OVER (ORDER BY id) AS row_no,
                            sum(COALESCE(byte_count,0)::bigint) OVER (ORDER BY id) AS cumulative_bytes,
                            bool_and(byte_count IS NOT NULL AND byte_count<=@max_value) OVER (ORDER BY id) AS prefix_valid
                          FROM page
                        )
                        SELECT id AS "Id",byte_count AS "ByteCount",cumulative_bytes AS "CumulativeBytes",
                          CASE WHEN prefix_valid AND cumulative_bytes<=@remaining_bytes AND row_no<=@remaining_count
                               THEN notification::text ELSE NULL::text END AS "Payload"
                        FROM ranked ORDER BY id
                        """, new NpgsqlParameter("first_page", first), new NpgsqlParameter("last_id", last),
                    new NpgsqlParameter("page_size", limit), new NpgsqlParameter("max_value", budget.RuleMaxBytes),
                    new NpgsqlParameter("remaining_bytes", remainingBytes), new NpgsqlParameter("remaining_count", remainingCount));
                var pageCount = 0;
                await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
                {
                    pageCount++;
                    var id = row.Id;
                    if (count >= budget.RuleMaxCount) throw new NotificationMigrationBudgetException("rule_count_exceeded", id);
                    if (!row.ByteCount.HasValue) throw InvalidRule(id);
                    var bytes = row.ByteCount.Value;
                    if (bytes > budget.RuleMaxBytes) throw new NotificationMigrationBudgetException("rule_too_large", id);
                    if (row.CumulativeBytes > remainingBytes) throw new NotificationMigrationBudgetException("total_bytes_exceeded", id);
                    if (row.Payload is null) throw InvalidRule(id);
                    total = checked(total + bytes);
                    count++;
                    last = id;
                    first = false;
                    try
                    {
                        if (legacyFormat && !HasExactFields(row.Payload, ["inConsole", "requestedChannels"]))
                            throw new InvalidOperationException();
                        NotificationPolicyValidator.Parse(row.Payload);
                    }
                    catch (Exception error) when (error is ApiException or InvalidOperationException) { throw InvalidRule(id); }
                }
                if (pageCount < limit) break;
            }
        }

        return new NotificationValidationSummary(settingsPresent || count > 0 ? NotificationValidationStatus.Passed : NotificationValidationStatus.NotApplicable,
            settingsPresent, count, total);
    }

    private static InvalidOperationException InvalidSettings() => new("Invalid legacy notification settings: system.notification.");
    private static InvalidOperationException InvalidRule(Guid id) => new("Invalid legacy rule notification: " + id.ToString("D") + ".");

    private static bool HasExactFields(string? json, string[] expected)
    {
        if (json is null) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var fields = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            return fields.Length == expected.Length && fields.Distinct(StringComparer.Ordinal).Count() == expected.Length
                && fields.All(name => expected.Contains(name, StringComparer.Ordinal));
        }
        catch (JsonException) { return false; }
    }
}

internal sealed class NotificationSettingsProbeRow
{
    public int? ByteCount { get; set; }
    public string? Payload { get; set; }
}

internal sealed class NotificationRuleProbeRow
{
    public Guid Id { get; set; }
    public int? ByteCount { get; set; }
    public long CumulativeBytes { get; set; }
    public string? Payload { get; set; }
}

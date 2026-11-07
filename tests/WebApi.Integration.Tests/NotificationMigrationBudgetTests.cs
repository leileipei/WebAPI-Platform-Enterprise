using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Domain.Notifications;
using WebApi.Infrastructure.Notifications;
using WebApi.Infrastructure.Persistence;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[Collection("MigrationIsolation")]
public sealed class NotificationMigrationBudgetTests
{
    private static MigrationBudget Budget => new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864);
    private const string SmallRule = "{\"inConsole\":true,\"requestedChannels\":[]}";

    [Fact]
    public async Task SettingsAndRuleByteLimitsApplyBeforeMaterialization()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        var ids = await SeedAsync(db);

        var ruleBase = await db.Database.SqlQueryRaw<int>("""
            SELECT pg_catalog.octet_length(pg_catalog.jsonb_build_object('inConsole',true,'requestedChannels','[]'::jsonb,'pad','')::text) AS "Value"
            """).SingleAsync();
        var settingsBase = await db.Database.SqlQueryRaw<int>("""
            SELECT pg_catalog.octet_length(pg_catalog.jsonb_build_object('smtpHost','','smtpPort',NULL,'fromEmail',NULL,'smtpSecretRef',NULL,'webhookUrl',NULL,'webhookSecretRef',NULL)::text) AS "Value"
            """).SingleAsync();
        var ruleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var paddedRule = RuleJson(Padding(16384 - ruleBase, unicode: true));
        await InsertRuleAsync(db, ids, ruleId, paddedRule);
        var ruleBytes = await db.Database.SqlQuery<int>($"SELECT octet_length(notification::text) AS \"Value\" FROM public.alert_rules WHERE id={ruleId}").SingleAsync();
        var ruleText = await db.Database.SqlQuery<string>($"SELECT notification::text AS \"Value\" FROM public.alert_rules WHERE id={ruleId}").SingleAsync();
        Assert.Equal(16384, ruleBytes);
        Assert.Equal(ruleBytes, Encoding.UTF8.GetByteCount(ruleText));
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT pg_column_size(notification)<octet_length(notification::text) AS \"Value\" FROM public.alert_rules WHERE id={ruleId}").SingleAsync());
        Assert.Throws<ApiException>(() => NotificationPolicyValidator.Parse(ruleText));
        var atLimitRuleFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget, CancellationToken.None));
        Assert.IsNotType<NotificationMigrationBudgetException>(atLimitRuleFailure);
        Assert.Contains("Invalid legacy rule notification", atLimitRuleFailure.Message);

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.alert_rules SET notification={SmallRule}::jsonb WHERE id={ruleId}");
        var smallBytes = await db.Database.SqlQuery<int>($"SELECT octet_length(notification::text) AS \"Value\" FROM public.alert_rules WHERE id={ruleId}").SingleAsync();
        var accepted = await NotificationMigrationPreflight.ValidateAsync(db, Budget, CancellationToken.None);
        Assert.Equal(NotificationValidationStatus.Passed, accepted.Status);
        Assert.False(accepted.SettingsPresent);
        Assert.Equal(1, accepted.RuleCount);
        Assert.Equal(smallBytes, accepted.TotalBytes);

        var oversizedRule = RuleJson(Padding(16385 - ruleBase, unicode: true));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.alert_rules SET notification={oversizedRule}::jsonb WHERE id={ruleId}");
        var ruleFailure = await Assert.ThrowsAsync<NotificationMigrationBudgetException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget, CancellationToken.None));
        Assert.Equal("rule_too_large", ruleFailure.Reason);
        Assert.Equal(ruleId, ruleFailure.RuleId);
        Assert.DoesNotContain("😀", ruleFailure.ToString());
        Assert.Equal(16385, await db.Database.SqlQuery<int>($"SELECT octet_length(notification::text) AS \"Value\" FROM public.alert_rules WHERE id={ruleId}").SingleAsync());

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.alert_rules SET notification={SmallRule}::jsonb WHERE id={ruleId}");
        var atLimit = SettingsJson(Padding(65536 - settingsBase, unicode: false));
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{atLimit}::jsonb,{ids.User},now(),7)");
        Assert.Equal(65536, await db.Database.SqlQueryRaw<int>("SELECT octet_length(value::text) AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
        var invalidAtLimit = await Assert.ThrowsAsync<InvalidOperationException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget, CancellationToken.None));
        Assert.IsNotType<NotificationMigrationBudgetException>(invalidAtLimit);
        Assert.Contains("Invalid legacy notification settings", invalidAtLimit.Message);

        var overLimit = SettingsJson(Padding(65537 - settingsBase, unicode: false));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.system_settings SET value={overLimit}::jsonb WHERE key='system.notification'");
        var settingsFailure = await Assert.ThrowsAsync<NotificationMigrationBudgetException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget, CancellationToken.None));
        Assert.Equal("settings_too_large", settingsFailure.Reason);
        Assert.DoesNotContain(new string('x', 100), settingsFailure.ToString());
        Assert.Equal(65537, await db.Database.SqlQueryRaw<int>("SELECT octet_length(value::text) AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
    }

    [Fact]
    public async Task CountAndAggregateLimitsFailWithoutWrites()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        var ids = await SeedAsync(db);
        var ruleIds = new[] { Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"), Guid.Parse("00000000-0000-0000-0000-000000000003") };
        foreach (var id in ruleIds) await InsertRuleAsync(db, ids, id, SmallRule);
        var before = await FingerprintAsync(db);
        var bytes = await db.Database.SqlQuery<int>($"SELECT octet_length(notification::text) AS \"Value\" FROM public.alert_rules WHERE id={ruleIds[0]}").SingleAsync();

        var countError = await Assert.ThrowsAsync<NotificationMigrationBudgetException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget with { RuleMaxCount = 2 }, CancellationToken.None));
        Assert.Equal("rule_count_exceeded", countError.Reason);
        Assert.Equal(before, await FingerprintAsync(db));

        var totalError = await Assert.ThrowsAsync<NotificationMigrationBudgetException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget with { TotalMaxBytes = bytes * 2L - 1 }, CancellationToken.None));
        Assert.Equal("total_bytes_exceeded", totalError.Reason);
        Assert.Equal(ruleIds[1], totalError.RuleId);
        Assert.Equal(before, await FingerprintAsync(db));

        var overlong = RuleJson(new string('x', 17000));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.alert_rules SET notification={overlong}::jsonb WHERE id={ruleIds[2]}");
        var finalBefore = await FingerprintAsync(db);
        var finalError = await Assert.ThrowsAsync<NotificationMigrationBudgetException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget, CancellationToken.None));
        Assert.Equal("rule_too_large", finalError.Reason);
        Assert.Equal(ruleIds[2], finalError.RuleId);
        Assert.Equal(finalBefore, await FingerprintAsync(db));
    }

    [Fact]
    public async Task PaginatedRulesPreserveLimitsAndTotals()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        var ids = await SeedAsync(db);
        var rules = Enumerable.Range(1, 20).Select(index => Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}")).ToArray();
        foreach (var id in rules) await InsertRuleAsync(db, ids, id, SmallRule);
        const string settings = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":null,\"webhookSecretRef\":null}";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{settings}::jsonb,{ids.User},now(),7)");
        var ruleBytes = await db.Database.SqlQuery<int>($"SELECT octet_length(notification::text) AS \"Value\" FROM public.alert_rules WHERE id={rules[0]}").SingleAsync();
        var settingsBytes = await db.Database.SqlQueryRaw<int>("SELECT octet_length(value::text) AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync();
        var total = settingsBytes + ruleBytes * 20L;
        var result = await NotificationMigrationPreflight.ValidateAsync(db, Budget with { RuleMaxCount = 20, TotalMaxBytes = total });
        Assert.Equal(NotificationValidationStatus.Passed, result.Status);
        Assert.True(result.SettingsPresent);
        Assert.Equal(20, result.RuleCount);
        Assert.Equal(total, result.TotalBytes);
        var before = await FingerprintAsync(db);
        var countFailure = await Assert.ThrowsAsync<NotificationMigrationBudgetException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget with { RuleMaxCount = 19 }));
        Assert.Equal("rule_count_exceeded", countFailure.Reason);
        Assert.Equal(rules[19], countFailure.RuleId);
        var bytesFailure = await Assert.ThrowsAsync<NotificationMigrationBudgetException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget with { TotalMaxBytes = total - 1 }));
        Assert.Equal("total_bytes_exceeded", bytesFailure.Reason);
        Assert.Equal(rules[19], bytesFailure.RuleId);
        Assert.Equal(before, await FingerprintAsync(db));
    }

    [Fact]
    public async Task EmptyOrNullNotificationIsNotMisreportedAsPassed()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        var empty = await NotificationMigrationPreflight.ValidateAsync(db, Budget, CancellationToken.None);
        Assert.Equal(NotificationValidationStatus.NotApplicable, empty.Status);
        Assert.Equal(0, empty.RuleCount);
        Assert.Equal(0, empty.TotalBytes);
        var ids = await SeedAsync(db);
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await InsertRuleAsync(db, ids, id, SmallRule);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.alert_rules ALTER COLUMN notification DROP NOT NULL");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.alert_rules SET notification=NULL WHERE id={id}");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => NotificationMigrationPreflight.ValidateAsync(db, Budget, CancellationToken.None));
        Assert.Contains("Invalid legacy rule notification", failure.Message);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT notification IS NULL AS \"Value\" FROM public.alert_rules WHERE id={id}").SingleAsync());
    }

    private static string Padding(int bytes, bool unicode) => unicode
        ? string.Concat(Enumerable.Repeat("😀", bytes / 4)) + new string('x', bytes % 4)
        : new string('x', bytes);

    private static string RuleJson(string pad) => JsonSerializer.Serialize(new { inConsole = true, requestedChannels = Array.Empty<string>(), pad });
    private static string SettingsJson(string host) => JsonSerializer.Serialize(new { smtpHost = host, smtpPort = (int?)null, fromEmail = (string?)null, smtpSecretRef = (string?)null, webhookUrl = (string?)null, webhookSecretRef = (string?)null });

    private static async Task<(Guid User, Guid Org, Guid Project, Guid Environment)> SeedAsync(WebApiDbContext db)
    {
        await db.Database.MigrateAsync();
        var ids = (User: Guid.NewGuid(), Org: Guid.NewGuid(), Project: Guid.NewGuid(), Environment: Guid.NewGuid());
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({ids.User},{ids.User.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({ids.Org},'KEEP','Keep','Active',now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES({ids.Project},{ids.Org},'KEEP','Keep','Active',now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status,deployment_sequence,revision) VALUES({ids.Environment},{ids.Project},'KEEP','Keep',false,0,'Active',0,1)");
        return ids;
    }

    private static Task<int> InsertRuleAsync(WebApiDbContext db, (Guid User, Guid Org, Guid Project, Guid Environment) ids, Guid rule, string json) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO alert_rules(id,organization_id,project_id,environment_id,name,normalized_name,metric,expression,severity,enabled,for_seconds,target_type,window_seconds,notification,revision,logic_revision,created_by,updated_by,created_at,updated_at) VALUES({rule},{ids.Org},{ids.Project},{ids.Environment},{rule.ToString()},{rule.ToString()},'request_rps','request_rps > 10','Warning',true,0,'Environment',60,{json}::jsonb,7,3,{ids.User},{ids.User},now(),now())");

    private static Task<string> FingerprintAsync(WebApiDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(pg_catalog.string_agg(id::text || ':' || COALESCE(notification::text,'NULL') || ':' || revision::text,',' ORDER BY id),'UTF8')),'hex') AS "Value"
        FROM public.alert_rules
        """).SingleAsync();
}

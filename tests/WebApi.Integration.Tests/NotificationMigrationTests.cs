using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WebApi.Integration.Tests.Support;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using Npgsql;
using WebApi.Infrastructure.Settings;
using WebApi.Infrastructure.Notifications;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class NotificationMigrationTests
{
    [Fact] public async Task OldSixFieldsAndIntentSurviveMigration()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261008020000_ApprovalInboxIndexes");
        const string old="{\"smtpHost\":\"mail.example.test\",\"smtpPort\":587,\"fromEmail\":\"Ops@example.test\",\"smtpSecretRef\":\"vault://local/smtp\",\"webhookUrl\":null,\"webhookSecretRef\":null}";
        var actor=Guid.NewGuid();await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({actor},{actor.ToString()},'Keep','hash','stamp','Active','local',now(),now())");await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{old}::jsonb,{actor},now(),7)");
        var (rule,eventId)=await SeedLegacyEvent(db,actor);
        await migrator.MigrateAsync();var next=await db.Database.SqlQueryRaw<string>("SELECT value::text AS \"Value\" FROM system_settings WHERE key='system.notification'").SingleAsync();using var before=JsonDocument.Parse(old);using var after=JsonDocument.Parse(next);
        foreach(var field in before.RootElement.EnumerateObject())Assert.Equal(field.Value.GetRawText(),after.RootElement.GetProperty(field.Name).GetRawText());Assert.True(after.RootElement.TryGetProperty("smtpEnabled",out var smtp));Assert.False(smtp.GetBoolean());Assert.False(after.RootElement.GetProperty("webhookEnabled").GetBoolean());Assert.Equal("StartTlsRequired",after.RootElement.GetProperty("smtpSecurity").GetString());Assert.Equal(7,await db.Database.SqlQueryRaw<long>("SELECT revision AS \"Value\" FROM system_settings").SingleAsync());
        Assert.Equal(4,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('notification_channel_profiles','notification_channel_states','notification_deliveries','notification_delivery_attempts')").SingleAsync());Assert.False(db.Database.HasPendingModelChanges());
        var saved=await db.Set<AlertRule>().SingleAsync(x=>x.Id==rule);using var intent=JsonDocument.Parse(saved.Notification);Assert.Equal("Email",intent.RootElement.GetProperty("requestedChannels")[0].GetString());Assert.False(intent.RootElement.GetProperty("externalEnabled").GetBoolean());Assert.Empty(intent.RootElement.GetProperty("emailRecipients").EnumerateArray());Assert.Equal(7,saved.Revision);Assert.Equal(3,saved.LogicRevision);
        var occurrence=await db.Set<AlertEvent>().SingleAsync(x=>x.Id==eventId);using var frozen=JsonDocument.Parse(occurrence.FrozenNotification);Assert.False(frozen.RootElement.GetProperty("policy").GetProperty("externalEnabled").GetBoolean());Assert.Equal("Keep original event",occurrence.Message);Assert.Empty(await db.Set<NotificationDelivery>().ToArrayAsync());Assert.All(await db.Set<NotificationChannelState>().ToArrayAsync(),state=>{Assert.False(state.Enabled);Assert.Null(state.ProfileId);});
    }
    private static async Task<(Guid Rule,Guid Event)> SeedLegacyEvent(WebApiDbContext db,Guid actor)
    {
        var org=Guid.NewGuid();var project=Guid.NewGuid();var env=Guid.NewGuid();var rule=Guid.NewGuid();var eventId=Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({org},'KEEP','Keep','Active',now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES({project},{org},'KEEP','Keep','Active',now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status,deployment_sequence,revision) VALUES({env},{project},'KEEP','Keep',false,0,'Active',0,1)");
        const string intent="{\"inConsole\":true,\"requestedChannels\":[\"Email\"]}";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO alert_rules(id,organization_id,project_id,environment_id,name,normalized_name,metric,expression,severity,enabled,for_seconds,target_type,window_seconds,notification,revision,logic_revision,created_by,updated_by,created_at,updated_at) VALUES({rule},{org},{project},{env},'Keep','KEEP','request_rps','request_rps > 10','Warning',true,0,'Environment',60,{intent}::jsonb,7,3,{actor},{actor},now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO alert_events(id,rule_id,rule_revision,logic_revision,organization_id,project_id,environment_id,resource_key,resource_type,occurrence_no,status,severity,message,rule_summary,started_at,condition_started_at,evaluation_state,revision) VALUES({eventId},{rule},7,3,{org},{project},{env},'Environment','Environment',1,'Open','Warning','Keep original event','Keep',now(),now(),'Known',1)");return(rule,eventId);
    }
    [Theory][InlineData("{}")] [InlineData("{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":null,\"webhookSecretRef\":null,\"unknown\":1}")]
    [InlineData("{\"smtpHost\":\"mail.example.test\",\"smtpPort\":0,\"fromEmail\":\"ops@example.test\",\"smtpSecretRef\":\"vault://local/mail\",\"webhookUrl\":null,\"webhookSecretRef\":null}")]
    public async Task InvalidLegacySettingsAreNotSilentlyRepaired(string json)
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261008020000_ApprovalInboxIndexes");var actor=new UserRecord{Username=Guid.NewGuid().ToString("N")};db.Add(actor);await db.SaveChangesAsync();await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{json}::jsonb,{actor.Id},now(),1)");
        var error=await Assert.ThrowsAsync<PostgresException>(()=>migrator.MigrateAsync());Assert.Contains("legacy",error.Message);Assert.DoesNotContain("vault://",error.Message);
        using var expected=JsonDocument.Parse(json);using var actual=JsonDocument.Parse(await db.Database.SqlQueryRaw<string>("SELECT value::text AS \"Value\" FROM system_settings").SingleAsync());Assert.Equal(expected.RootElement.EnumerateObject().OrderBy(p=>p.Name).Select(p=>(p.Name,p.Value.ToString())),actual.RootElement.EnumerateObject().OrderBy(p=>p.Name).Select(p=>(p.Name,p.Value.ToString())));Assert.DoesNotContain("20261008030000_ExternalNotifications",await db.Database.GetAppliedMigrationsAsync());
    }
    [Fact] public async Task InvalidLegacyRuleReportsOnlyRowId()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261008020000_ApprovalInboxIndexes");var actor=new UserRecord{Username=Guid.NewGuid().ToString("N")};db.Add(actor);await db.SaveChangesAsync();var (rule,_)=await SeedLegacyEvent(db,actor.Id);await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE alert_rules SET notification='{{\"inConsole\":true,\"requestedChannels\":[\"Email\",\"Email\"]}}'::jsonb WHERE id={rule}");
        var error=await Assert.ThrowsAsync<PostgresException>(()=>migrator.MigrateAsync());Assert.Contains(rule.ToString(),error.Message);Assert.DoesNotContain("requestedChannels",error.Message);Assert.DoesNotContain("20261008030000_ExternalNotifications",await db.Database.GetAppliedMigrationsAsync());
    }
    [Theory]
    [InlineData("{\"smtpHost\":\"\",\"smtpPort\":null,\"fromEmail\":\"\",\"smtpSecretRef\":null,\"webhookUrl\":\"\",\"webhookSecretRef\":null}")]
    [InlineData("{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"HTTPS://hook.example.test/path with space\",\"webhookSecretRef\":\"VAULT://local/path with space\"}")]
    public async Task LegalLegacyEmptyAndEscapedUriValuesRemainExact(string json)
    {
        Assert.IsType<NotificationSettings>(SettingsValueCodec.Decode("notification",json));await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261008020000_ApprovalInboxIndexes");var actor=new UserRecord{Username=Guid.NewGuid().ToString("N")};db.Add(actor);await db.SaveChangesAsync();await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{json}::jsonb,{actor.Id},now(),1)");await NotificationMigrationPreflight.ValidateAsync(db);await migrator.MigrateAsync();
        using var before=JsonDocument.Parse(json);using var after=JsonDocument.Parse(await db.Database.SqlQueryRaw<string>("SELECT value::text AS \"Value\" FROM system_settings").SingleAsync());foreach(var field in before.RootElement.EnumerateObject()){Assert.Equal(field.Value.ValueKind,after.RootElement.GetProperty(field.Name).ValueKind);Assert.Equal(field.Value.ToString(),after.RootElement.GetProperty(field.Name).ToString());}Assert.False(after.RootElement.GetProperty("smtpEnabled").GetBoolean());Assert.False(after.RootElement.GetProperty("webhookEnabled").GetBoolean());
    }
    [Fact] public async Task ProfilesAreImmutableAndDeliveryConstraintsAreReal()
    {
        await using var f=new ApiFixture();await f.InitializeAsync();await using var db=f.Context();var profile=new NotificationChannelProfile{Channel="Email",ConfigurationHash=new string('a',64),PrivateConfiguration="{}",ProtectedSecretFingerprint="fixture",SettingsRevision=1,CreatedBy=f.User.Id};db.Add(profile);await db.SaveChangesAsync();
        profile.SettingsRevision=2;var immutable=await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());Assert.Equal(PostgresErrorCodes.CheckViolation,((PostgresException)immutable.InnerException!).SqlState);db.ChangeTracker.Clear();
        var now=DateTimeOffset.UtcNow;var delivery=new NotificationDelivery{Kind="Test",CreatedBy=f.User.Id,Channel="Email",Target="ops@example.test",TargetHash=new string('b',64),ProfileId=profile.Id,Payload=[1],MaxAttempts=1,ExpiresAfterMinutes=5,CreatedAt=now,ExpiresAt=now.AddMinutes(5)};db.Add(delivery);await db.SaveChangesAsync();
        db.Add(new NotificationDeliveryAttempt{DeliveryId=delivery.Id,AttemptNo=1,LeaseToken=1,StartedAt=now});await db.SaveChangesAsync();db.Add(new NotificationDeliveryAttempt{DeliveryId=delivery.Id,AttemptNo=1,LeaseToken=2,StartedAt=now});var duplicate=await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());Assert.Equal(PostgresErrorCodes.UniqueViolation,((PostgresException)duplicate.InnerException!).SqlState);db.ChangeTracker.Clear();
        var row=await db.Set<NotificationDelivery>().SingleAsync();row.AttemptCount=2;var budget=await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());Assert.Equal(PostgresErrorCodes.CheckViolation,((PostgresException)budget.InnerException!).SqlState);db.ChangeTracker.Clear();
        row=await db.Set<NotificationDelivery>().SingleAsync();row.ProfileId=null;var absent=await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());Assert.Equal(PostgresErrorCodes.CheckViolation,((PostgresException)absent.InnerException!).SqlState);
    }
    [Fact] public async Task MigrationPreflightUsesExactSettingsDecoderWithoutEcho()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261008020000_ApprovalInboxIndexes");var actor=new UserRecord{Username=Guid.NewGuid().ToString("N")};db.Add(actor);await db.SaveChangesAsync();const string invalid="{\"smtpHost\":\"mail.example.test\",\"smtpPort\":587,\"fromEmail\":\"bad@bad@bad\",\"smtpSecretRef\":\"vault://local/mail\",\"webhookUrl\":null,\"webhookSecretRef\":null}";await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{invalid}::jsonb,{actor.Id},now(),1)");
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>NotificationMigrationPreflight.ValidateAsync(db));Assert.Contains("system.notification",error.Message);Assert.DoesNotContain("bad@bad@bad",error.Message);Assert.DoesNotContain("vault://",error.Message);
    }
}

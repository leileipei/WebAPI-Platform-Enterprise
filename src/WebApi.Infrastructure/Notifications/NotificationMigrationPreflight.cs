using WebApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Domain.Notifications;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Settings;
namespace WebApi.Infrastructure.Notifications;
public static class NotificationMigrationPreflight
{
    public static async Task ValidateAsync(WebApiDbContext db,CancellationToken ct=default)
    {
        if((await db.Database.GetAppliedMigrationsAsync(ct)).Contains("20261008030000_ExternalNotifications",StringComparer.Ordinal))return;
        if(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.system_settings') IS NOT NULL AS \"Value\"").SingleAsync(ct))
        {
            var json=await db.Set<SystemSetting>().AsNoTracking().Where(x=>x.Key=="system.notification").Select(x=>x.Value).SingleOrDefaultAsync(ct);
            if(json is not null)try{SettingsValueCodec.Decode("notification",json);}catch(ApiException){throw new InvalidOperationException("Invalid legacy notification settings: system.notification.");}
        }
        if(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.alert_rules') IS NOT NULL AS \"Value\"").SingleAsync(ct))
            await foreach(var rule in db.Set<AlertRule>().AsNoTracking().Select(x=>new{x.Id,x.Notification}).AsAsyncEnumerable().WithCancellation(ct))
                try{NotificationPolicyValidator.Parse(rule.Notification);}catch(ApiException){throw new InvalidOperationException("Invalid legacy rule notification: "+rule.Id.ToString("D")+".");}
    }
}

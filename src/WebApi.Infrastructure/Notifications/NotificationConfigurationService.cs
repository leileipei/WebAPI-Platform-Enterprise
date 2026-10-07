using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Settings;
namespace WebApi.Infrastructure.Notifications;
// This private snapshot contains logical references, never secret material.
internal sealed record NotificationProfileConfiguration(string Channel,string? Host,int? Port,string? FromEmail,string? Security,string? Url,string Reference);
public sealed class NotificationConfigurationService(WebApiDbContext db,NotificationDeploymentSettings settings,INotificationSecretResolver secrets,NotificationSecretVersion versions,TimeProvider clock)
{
    internal static NotificationProfileConfiguration Configuration(NotificationSettings value,NotificationChannel channel)=>channel switch
    {
        NotificationChannel.Email when value.SmtpHost is not null&&value.SmtpPort is not null&&value.FromEmail is not null&&value.SmtpSecretRef is not null=>new("Email",value.SmtpHost,value.SmtpPort,value.FromEmail,value.SmtpSecurity,null,value.SmtpSecretRef),
        NotificationChannel.Webhook when value.WebhookUrl is not null&&value.WebhookSecretRef is not null=>new("Webhook",null,null,null,null,value.WebhookUrl,value.WebhookSecretRef),
        _=>throw new ApiException(422,"notification_configuration_incomplete","通知渠道尚未配置完整。")
    };
    internal static string Hash(NotificationProfileConfiguration configuration)=>Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(configuration)));
    internal void ValidateTarget(NotificationProfileConfiguration value)
    {
        if(value.Channel=="Email")settings.RequireAllowedEndpoint(NotificationChannel.Email,value.Host!,value.Port!.Value);
        else{var url=new Uri(value.Url!);settings.RequireAllowedEndpoint(NotificationChannel.Webhook,url.IdnHost,url.Port,url);}
    }
    internal async Task<NotificationChannelProfile> CreateProfileAsync(NotificationSettings value,NotificationChannel channel,long revision,Guid actor,CancellationToken ct)
    {
        var configuration=Configuration(value,channel);ValidateTarget(configuration);var secret=await secrets.ResolveAsync(configuration.Reference,channel,ct);
        var profile=new NotificationChannelProfile{Channel=channel.ToString(),PrivateConfiguration=Encoding.UTF8.GetString(CanonicalJson.Serialize(configuration)),ConfigurationHash=Hash(configuration),CreatedBy=actor,CreatedAt=clock.GetUtcNow(),SettingsRevision=revision};profile.ProtectedSecretFingerprint=versions.Pin(profile.Id,secret);db.Add(profile);return profile;
    }
    public async Task ApplyAsync(NotificationSettings before,NotificationSettings after,long settingsRevision,ActorContext actor,CancellationToken ct=default)
    {
        if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Notification settings require a governance transaction.");
        var explicitSameValue=before==after;
        foreach(var channel in new[]{NotificationChannel.Email,NotificationChannel.Webhook})
        {
            var name=channel.ToString();var state=await db.Set<NotificationChannelState>().SingleOrDefaultAsync(x=>x.Channel==name,ct);
            if(state is null){state=new(){Channel=name};db.Add(state);}
            var oldConfiguration=TryConfiguration(before,channel);var configuration=TryConfiguration(after,channel);
            var changed=oldConfiguration!=configuration;var enabled=channel==NotificationChannel.Email?after.SmtpEnabled:after.WebhookEnabled;var wasEnabled=channel==NotificationChannel.Email?before.SmtpEnabled:before.WebhookEnabled;
            var oldProfileId=state.ProfileId;var oldEnabled=state.Enabled;
            if(!enabled){if(changed)state.ProfileId=null;state.Enabled=false;}
            else if(changed||!wasEnabled||state.ProfileId is null||explicitSameValue)
            {
                if(configuration is null)throw new ApiException(422,"notification_configuration_incomplete","通知渠道尚未配置完整。");
                ValidateTarget(configuration);var secret=await secrets.ResolveAsync(configuration.Reference,channel,ct);NotificationChannelProfile? current=null;
                if(state.ProfileId is {} id)current=await db.Set<NotificationChannelProfile>().SingleAsync(x=>x.Id==id,ct);
                if(current is null||current.ConfigurationHash!=Hash(configuration)||!versions.Matches(current.Id,current.ProtectedSecretFingerprint,secret))
                {
                    var profile=new NotificationChannelProfile{Channel=name,ConfigurationHash=Hash(configuration),PrivateConfiguration=Encoding.UTF8.GetString(CanonicalJson.Serialize(configuration)),CreatedBy=actor.UserId,CreatedAt=clock.GetUtcNow(),SettingsRevision=settingsRevision};profile.ProtectedSecretFingerprint=versions.Pin(profile.Id,secret);db.Add(profile);state.ProfileId=profile.Id;
                }
                state.Enabled=true;
            }
            if(oldProfileId!=state.ProfileId||oldEnabled!=state.Enabled)state.Revision++;
        }
    }
    private static NotificationProfileConfiguration? TryConfiguration(NotificationSettings value,NotificationChannel channel)
    {if(channel==NotificationChannel.Email&&value.SmtpHost is null||channel==NotificationChannel.Webhook&&value.WebhookUrl is null)return null;return Configuration(value,channel);}
    internal static NotificationProfileConfiguration Decode(NotificationChannelProfile profile)=>JsonSerializer.Deserialize<NotificationProfileConfiguration>(profile.PrivateConfiguration,CanonicalJson.Options)??throw new ApiException(503,"notification_profile_invalid","通知档案不可用。");
}

using System.Text.Json;
using WebApi.Infrastructure.Notifications;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Settings;
public abstract record SettingsValues;
public sealed record SecuritySettings(int SessionTtlMinutes,int PasswordMinLength,string PasswordComplexity,string[] AllowedOrigins):SettingsValues;
public sealed record ReleaseSettings(int ProductionApprovalLevels,int SnapshotRetentionCount):SettingsValues;
public sealed record GatewaySettings(int DefaultRouteTimeoutMs,int MaxRequestBodyMb,int ConfigRefreshIntervalSeconds):SettingsValues;
public sealed record AuditSettings(int AuditRetentionDays,bool AuditExportEnabled):SettingsValues;
public sealed record NotificationSettings(string? SmtpHost,int? SmtpPort,string? FromEmail,string? SmtpSecretRef,string? WebhookUrl,string? WebhookSecretRef):SettingsValues;
public sealed record ValidatedSettings(SettingsValues Value,string CanonicalValueJson,string ValueHash,IReadOnlyList<string> ChangedFields);
public sealed record SettingsStoredValue(SettingsValues Value,long Revision,bool IsSaved);
public static class SettingsValueCodec
{
    public static SettingsValues Default(string group)=>group switch {
        "security"=>new SecuritySettings(480,16,"LengthOnly",[]),"release"=>new ReleaseSettings(2,50),"gateway"=>new GatewaySettings(30000,20,2),"audit"=>new AuditSettings(365,true),"notification"=>new NotificationSettings(null,null,null,null,null,null),_=>throw new ApiException(404,"settings_group_missing","设置分组不存在。")};
    public static JsonElement Json(SettingsValues value)=>JsonSerializer.SerializeToElement(value,value.GetType(),CanonicalJson.Options);
    public static SettingsValues Decode(string group,string json,CompatibilityNotificationMode? compatibility=null)
    {
        Default(group);
        try
        {
            using var document=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=16});
            var properties=document.RootElement.EnumerateObject().ToArray();var expected=SystemSettingsValidator.Fields[group];
            if(group=="notification"&&compatibility?.IsReadOnly==true&&properties.Length==expected.Length+3)
            {
                var allowed=expected.Concat(new[]{"smtpEnabled","webhookEnabled","smtpSecurity"}).ToArray();
                if(properties.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=allowed.Length||properties.Any(p=>!allowed.Contains(p.Name)))throw new JsonException();
                if(document.RootElement.GetProperty("smtpEnabled").ValueKind is not(JsonValueKind.True or JsonValueKind.False)||document.RootElement.GetProperty("webhookEnabled").ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new JsonException();
                var security=document.RootElement.GetProperty("smtpSecurity");if(security.ValueKind!=JsonValueKind.String||security.GetString() is not("StartTlsRequired" or "TlsOnConnect"))throw new JsonException();
                properties=properties.Where(p=>expected.Contains(p.Name)).ToArray();
            }
            if(properties.Length!=expected.Length||properties.Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=expected.Length||properties.Any(p=>!expected.Contains(p.Name)))throw new JsonException();
            var input=properties.ToDictionary(p=>p.Name,p=>(object?)p.Value.Clone());
            if(group=="notification")foreach(var key in new[]{"smtpSecretRef","webhookSecretRef"})
            {var value=document.RootElement.GetProperty(key);input[key]=value.ValueKind==JsonValueKind.Null?new Dictionary<string,object?>{{"operation","Clear"}}:new Dictionary<string,object?>{{"operation","Replace"},{"reference",value.GetString()}};}
            return SystemSettingsValidator.Resolve(group,new(JsonSerializer.SerializeToElement(input,CanonicalJson.Options)),Default(group)).Value;
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or ApiException or ArgumentException)
        {throw new ApiException(503,"stored_settings_invalid","已保存的平台配置无效，暂时停止相关操作，请联系管理员。");}
    }
}

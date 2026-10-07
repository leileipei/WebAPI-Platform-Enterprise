using System.Text.Json;
using System.Globalization;
using System.Net.Mail;
using System.Text;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
namespace WebApi.Domain.Notifications;
public static class NotificationPolicyValidator
{
    private static ApiException Invalid()=>new(422,"invalid_notification_policy","通知策略的渠道、目标或预算不合法。");
    public static void ValidateRetry(NotificationRetryPolicy value)
    {
        if(value is null||value.MaxAttempts is <1 or >5||value.BaseDelaySeconds is <1 or >300||value.MaxDelaySeconds<value.BaseDelaySeconds||value.MaxDelaySeconds>3600||value.ExpiresAfterMinutes is <5 or >1440)throw Invalid();
    }
    public static NotificationIntent Normalize(NotificationIntent value)
    {
        if(value is null||!value.InConsole||value.RequestedChannels is null||value.RequestedChannels.Count>3||value.RequestedChannels.Any(c=>c is not("Email" or "Webhook" or "EnterpriseIm")))throw Invalid();
        if(value.RequestedChannels.Distinct(StringComparer.Ordinal).Count()!=value.RequestedChannels.Count)throw Invalid();
        var channels=value.RequestedChannels.Order(StringComparer.Ordinal).ToArray();var raw=value.EmailRecipients??[];if(raw.Count>20)throw Invalid();var recipients=raw.Select(NormalizeEmail).ToArray();
        if(recipients.Distinct(StringComparer.Ordinal).Count()!=recipients.Length||value.ExternalEnabled&&(!channels.Any(c=>c is "Email" or "Webhook")||channels.Contains("Email")&&recipients.Length==0))throw Invalid();
        var retry=value.RetryPolicy??NotificationRetryPolicy.Default;ValidateRetry(retry);
        return value with{RequestedChannels=Array.AsReadOnly(channels),EmailRecipients=Array.AsReadOnly(recipients),RetryPolicy=retry};
    }
    public static NotificationIntent Parse(string json)
    {
        if(json is null||Encoding.UTF8.GetByteCount(json)>16384)throw Invalid();
        try{return Normalize(JsonSerializer.Deserialize<NotificationIntent>(json,CanonicalJson.Options)??throw Invalid());}catch(JsonException){throw Invalid();}
    }
    public static string NormalizeEmail(string value)
    {
        if(string.IsNullOrEmpty(value)||value.Length>254||value.Any(char.IsControl)||!MailAddress.TryCreate(value,out var address)||address.DisplayName.Length!=0||address.Address!=value)throw Invalid();
        try
        {
            var domain=new IdnMapping{UseStd3AsciiRules=true}.GetAscii(address.Host).ToLowerInvariant();var at=value.LastIndexOf('@');var local=value[..at];
            if(Encoding.UTF8.GetByteCount(local)>64||domain.Length>253||domain.Split('.').Any(label=>label.Length is <1 or >63||label.StartsWith('-')||label.EndsWith('-')))throw Invalid();
            var result=local+"@"+domain;if(result.Length>254)throw Invalid();return result;
        }
        catch(ArgumentException){throw Invalid();}
    }
}

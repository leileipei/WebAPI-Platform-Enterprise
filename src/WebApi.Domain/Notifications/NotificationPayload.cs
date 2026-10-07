using WebApi.Contracts.Common;
using WebApi.Contracts.Notifications;
using WebApi.Domain.Alerts;
using System.Globalization;
namespace WebApi.Domain.Notifications;
public static class NotificationPayload
{
    public static byte[] Serialize(NotificationMessageV1 value)
    {
        if(value.SchemaVersion!=1||!Uri.TryCreate(value.ConsoleLink,UriKind.Absolute,out var uri)||uri.Scheme is not("https" or "http")||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw Invalid();
        if(value.Kind=="Test")
        {if(value.EventId is not null||value.OccurrenceNo is not null||value.EnvironmentId is not null||value.Severity is not null||value.MetricCondition is not null||value.Transition is not null)throw Invalid();}
        else if(value.Kind=="Alert")
        {
            if(value.EventId is null||value.EventId==Guid.Empty||value.EnvironmentId is null||value.EnvironmentId==Guid.Empty||value.OccurrenceNo is null or <1||value.Severity is not("Info" or "Warning" or "Critical")||value.Transition is not("Triggered" or "Resolved")||value.MetricCondition is null)throw Invalid();
            try
            {
                var metric=value.MetricCondition.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if(metric is null)throw Invalid();
                var rule=AlertExpressionParser.Parse(metric,value.MetricCondition);
                value=value with {MetricCondition=rule.Metric+" "+rule.Operator+" "+rule.Threshold.ToString("R",CultureInfo.InvariantCulture)};
            }
            catch(ArgumentException){throw Invalid();}
        }
        else throw Invalid();
        var bytes=CanonicalJson.Serialize(value);
        if(bytes.Length>16384)throw new ApiException(422,"payload_too_large","通知消息超过允许大小。");
        return bytes;
    }
    public static byte[] Alert(Guid eventId,long occurrenceNo,Guid environmentId,string severity,string metric,string expression,string transition,DateTimeOffset time,Uri consoleBaseUrl)
    {
        var rule=AlertExpressionParser.Parse(metric,expression);var normalized=rule.Metric+" "+rule.Operator+" "+rule.Threshold.ToString("R",CultureInfo.InvariantCulture);
        return Serialize(new(1,"Alert",eventId,occurrenceNo,environmentId,severity,normalized,transition,time,Link(consoleBaseUrl,"observability/alerts?eventId="+eventId.ToString("D"))));
    }
    public static byte[] Test(Uri consoleBaseUrl,DateTimeOffset time)=>Serialize(new(1,"Test",null,null,null,null,null,null,time,Link(consoleBaseUrl,"settings/system?tab=notification")));
    private static string Link(Uri value,string route)
    {if(!value.IsAbsoluteUri||value.Scheme is not("https" or "http")||value.UserInfo.Length!=0||value.Query.Length!=0||value.Fragment.Length!=0)throw Invalid();return value.AbsoluteUri.TrimEnd('/')+"/"+route;}
    private static ApiException Invalid()=>new(422,"invalid_notification_payload","通知消息字段不合法。");
}

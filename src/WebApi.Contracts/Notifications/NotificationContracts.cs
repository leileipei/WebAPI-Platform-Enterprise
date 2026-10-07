using System.Text.Json;
using System.Text.Json.Serialization;
using WebApi.Contracts.Alerts;
namespace WebApi.Contracts.Notifications;

[JsonConverter(typeof(NotificationChannelJsonConverter))] public enum NotificationChannel { Email, Webhook }
[JsonConverter(typeof(DeliveryStatusJsonConverter))] public enum DeliveryStatus { Queued, Sending, RetryScheduled, Paused, Accepted, Failed, Suppressed, Expired }
[JsonConverter(typeof(DeliveryOutcomeJsonConverter))] public enum DeliveryOutcome { Accepted, TransientFailure, PermanentFailure, OutcomeUnknown }
public sealed class NotificationChannelJsonConverter():JsonStringEnumConverter<NotificationChannel>(null,false);
public sealed class DeliveryStatusJsonConverter():JsonStringEnumConverter<DeliveryStatus>(null,false);
public sealed class DeliveryOutcomeJsonConverter():JsonStringEnumConverter<DeliveryOutcome>(null,false);
public sealed record NotificationRetryPolicy(int MaxAttempts,int BaseDelaySeconds,int MaxDelaySeconds,int ExpiresAfterMinutes)
{
    public static readonly NotificationRetryPolicy Default=new(5,30,900,1440);
}
public sealed record TransportResult(DeliveryOutcome Outcome,string Code,int? ProtocolStatus=null,TimeSpan? RetryAfter=null);
public sealed record RetryDecision(DateTimeOffset? NextAt,DeliveryStatus Status,string? Reason);
public sealed record NotificationDeliveryDto(Guid Id,string Kind,Guid? EventId,NotificationChannel Channel,string MaskedTarget,DeliveryStatus Status,string? Reason,int AttemptCount,int MaxAttempts,DateTimeOffset CreatedAt,DateTimeOffset ExpiresAt,DateTimeOffset? NextAttemptAt,bool CanRetry,long Revision);
public sealed record NotificationAttemptDto(int AttemptNo,DateTimeOffset StartedAt,DateTimeOffset? CompletedAt,DeliveryOutcome? Outcome,string? Code,int? ProtocolStatus);
public sealed record NotificationMessageV1(int SchemaVersion,string Kind,Guid? EventId,long? OccurrenceNo,Guid? EnvironmentId,string? Severity,string? MetricCondition,string? Transition,DateTimeOffset OccurredAt,string ConsoleLink);

// Reject duplicate/unknown notification fields at every JSON boundary, including
// the original typed rule endpoint. Web naming aliases remain compatible.
public sealed class NotificationIntentJsonConverter:JsonConverter<NotificationIntent>
{
    private static Dictionary<string,JsonElement> Fields(JsonElement element,IEnumerable<string> allowed,JsonSerializerOptions options)
    {
        if(element.ValueKind!=JsonValueKind.Object)throw new JsonException("通知配置必须为对象。");
        var comparer=options.PropertyNameCaseInsensitive?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        var names=new HashSet<string>(allowed,comparer);var values=new Dictionary<string,JsonElement>(comparer);
        foreach(var field in element.EnumerateObject())if(!names.Contains(field.Name)||!values.TryAdd(field.Name,field.Value))throw new JsonException("通知配置含重复或未知字段。");
        return values;
    }
    private static string[] Strings(JsonElement element)
    {
        if(element.ValueKind!=JsonValueKind.Array)throw new JsonException("通知列表必须为数组。");
        return element.EnumerateArray().Select(item=>item.ValueKind==JsonValueKind.String?item.GetString()!:throw new JsonException("通知列表包含非法值。")).ToArray();
    }
    public override NotificationIntent Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)
    {
        try
        {
            using var document=JsonDocument.ParseValue(ref reader);var fields=Fields(document.RootElement,["inConsole","requestedChannels","externalEnabled","emailRecipients","notifyRecovery","retryPolicy"],options);
            if(!fields.TryGetValue("inConsole",out var console)||!fields.TryGetValue("requestedChannels",out var channels))throw new JsonException("通知配置缺少原必填字段。");
            NotificationRetryPolicy? retry=null;
            if(fields.TryGetValue("retryPolicy",out var retryJson)&&retryJson.ValueKind!=JsonValueKind.Null)
            {
                var values=Fields(retryJson,["maxAttempts","baseDelaySeconds","maxDelaySeconds","expiresAfterMinutes"],options);
                if(values.Count!=4)throw new JsonException("通知重试配置缺少字段。");
                retry=new(values["maxAttempts"].GetInt32(),values["baseDelaySeconds"].GetInt32(),values["maxDelaySeconds"].GetInt32(),values["expiresAfterMinutes"].GetInt32());
            }
            return new(console.GetBoolean(),Strings(channels),fields.TryGetValue("externalEnabled",out var external)&&external.GetBoolean(),fields.TryGetValue("emailRecipients",out var recipients)?Strings(recipients):[],!fields.TryGetValue("notifyRecovery",out var recovery)||recovery.GetBoolean(),retry);
        }
        catch(Exception e)when(e is InvalidOperationException or FormatException or OverflowException){throw new JsonException("通知字段类型不合法。");}
    }
    public override void Write(Utf8JsonWriter writer,NotificationIntent value,JsonSerializerOptions options)
    {
        writer.WriteStartObject();writer.WriteBoolean("inConsole",value.InConsole);writer.WritePropertyName("requestedChannels");JsonSerializer.Serialize(writer,value.RequestedChannels,options);
        writer.WriteBoolean("externalEnabled",value.ExternalEnabled);writer.WritePropertyName("emailRecipients");JsonSerializer.Serialize(writer,value.EmailRecipients??[],options);
        writer.WriteBoolean("notifyRecovery",value.NotifyRecovery);writer.WritePropertyName("retryPolicy");JsonSerializer.Serialize(writer,value.RetryPolicy??NotificationRetryPolicy.Default,options);writer.WriteEndObject();
    }
}

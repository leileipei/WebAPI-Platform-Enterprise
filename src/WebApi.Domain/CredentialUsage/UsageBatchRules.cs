using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.CredentialUsage;
namespace WebApi.Domain.CredentialUsage;

public static class UsageBatchRules
{
    public const int MaximumBytes=256*1024;
    public static UsageBatchRequest Parse(ReadOnlyMemory<byte> utf8,DateTimeOffset now,int maxAgeHours)
    {
        if(utf8.Length>MaximumBytes)throw new ApiException(413,"usage_batch_too_large","Credential统计批次超过允许大小。");
        if(maxAgeHours is <1 or >24)throw Invalid();
        try
        {
            using var document=JsonDocument.Parse(utf8,new JsonDocumentOptions{MaxDepth=8});
            RejectDuplicateFields(document.RootElement);
            var request=document.RootElement.Deserialize<UsageBatchRequest>(UsageProtocolJson.Options)??throw Invalid();
            Validate(request,now.ToUniversalTime(),maxAgeHours);return request;
        }
        catch(Exception error) when(error is JsonException or ArgumentException or OverflowException)
        {throw Invalid();}
    }
    private static void RejectDuplicateFields(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in value.EnumerateObject()){if(!names.Add(field.Name))throw Invalid();RejectDuplicateFields(field.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())RejectDuplicateFields(item);
    }
    private static void Validate(UsageBatchRequest request,DateTimeOffset now,int maxAgeHours)
    {
        var core=request.Core;
        if(request.SenderInstanceId==Guid.Empty||core is null||core.Protocol!="1.0"||core.ProducerInstanceId==Guid.Empty||core.CollectorId==Guid.Empty||core.Sequence<1||core.Items is null||core.Intervals is null||core.Gaps is null||core.Items.Count>1000)throw Invalid();
        var oldest=now.AddHours(-maxAgeHours);var future=now.AddMinutes(2);
        void Time(DateTimeOffset value)
        {
            if(value>future)throw new ApiException(422,"usage_clock_skew","统计来源时钟超出允许偏差。");
            if(value<oldest)throw Invalid();
        }
        Time(core.SealedAt);Time(core.DurableThrough);if(core.DurableThrough>core.SealedAt)throw Invalid();
        foreach(var interval in core.Intervals)
        {
            if(interval is null||interval.From>=interval.To||interval.To>core.DurableThrough)throw Invalid();
            Time(interval.From);Time(interval.To);
        }
        foreach(var gap in core.Gaps)
        {
            if(gap is null||gap.From>=gap.To||gap.Reason is null||gap.Reason.Length is <1 or >64||gap.Reason.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('_' or '-')))throw Invalid();
            Time(gap.From);Time(gap.To);
        }
        var sources=new HashSet<UsageSource>();var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var item in core.Items)
        {
            if(item is null||item.ApplicationId==Guid.Empty||item.CredentialId==Guid.Empty||item.ApiId==Guid.Empty||item.Source is null||item.Count is <1 or >1000000)throw Invalid();
            var source=item.Source;
            if(source.ReleaseId==Guid.Empty||source.ConfigVersion<1||source.DeploymentSequence<1||source.PayloadHash is null||source.PayloadHash.Length!=64||source.PayloadHash.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))throw Invalid();
            Time(item.FirstObservedAt);Time(item.LastObservedAt);
            if(item.FirstObservedAt>item.LastObservedAt||item.LastObservedAt>core.DurableThrough||item.FirstObservedAt<item.GrantValidFrom||item.GrantExpiresAt is {} expires&&(expires<=item.GrantValidFrom||item.LastObservedAt>=expires))throw Invalid();
            var first=item.FirstObservedAt.UtcDateTime;var hour=new DateTimeOffset(first.Year,first.Month,first.Day,first.Hour,0,0,TimeSpan.Zero);
            if(item.HourStart!=hour||item.LastObservedAt>=hour.AddHours(1))throw Invalid();
            if(!keys.Add(ItemKey(item)))throw Invalid();sources.Add(source);if(sources.Count>2)throw Invalid();
        }
    }
    private static string ItemKey(UsageItem item)
    {
        var expiry=item.GrantExpiresAt is {} value?value.UtcTicks.ToString(CultureInfo.InvariantCulture):"open";
        return FormattableString.Invariant($"{item.ApplicationId:N}|{item.CredentialId:N}|{item.ApiId:N}|{item.GrantValidFrom.UtcTicks}|{expiry}|{item.Source.ReleaseId:N}|{item.Source.ConfigVersion}|{item.Source.DeploymentSequence}|{item.Source.PayloadHash}|{item.HourStart.UtcTicks}");
    }
    public static string CoreHash(UsageBatchCore core)
    {
        var normalized=core with
        {
            Items=core.Items.OrderBy(ItemKey,StringComparer.Ordinal).ToArray(),
            Intervals=core.Intervals.OrderBy(x=>x.From.UtcTicks).ThenBy(x=>x.To.UtcTicks).ToArray(),
            Gaps=core.Gaps.OrderBy(x=>x.From.UtcTicks).ThenBy(x=>x.To.UtcTicks).ThenBy(x=>x.Reason,StringComparer.Ordinal).ToArray()
        };
        var element=JsonSerializer.SerializeToElement(normalized,UsageProtocolJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(element)));
    }
    private static ApiException Invalid()=>new(422,"invalid_usage_batch","Credential统计批次不合法。");
}

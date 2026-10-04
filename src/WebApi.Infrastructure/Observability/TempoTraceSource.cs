using System.Globalization;using System.Text.Json;using WebApi.Contracts.Common;using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed record TraceCandidateBatch(IReadOnlyList<string> TraceIds,bool Truncated);
public sealed record TraceSpanBatch(IReadOnlyList<SourceSpan> Spans,bool Rejected);
public sealed class TempoTraceSource(ObservationSourceClient client,ObservationSourceSettings settings)
{
    public const int MaximumCandidates=200,MaximumSpans=1000;
    public static string TraceId(string value){var id=Identifier(value,16,false);return id??throw new ApiException(422,"invalid_trace_id","TraceId须为32位非全零十六进制W3C标识。");}
    internal static string? Identifier(string? value,int bytes,bool allowBase64=true)
    {if(value is null)return null;if(value.Length==bytes*2&&value.All(Uri.IsHexDigit)&&value.Any(c=>c!='0'))return value.ToLowerInvariant();if(!allowBase64)return null;try{var data=Convert.FromBase64String(value);return data.Length==bytes&&data.Any(x=>x!=0)?Convert.ToHexStringLower(data):null;}catch(FormatException){return null;}}
    public async Task<TraceCandidateBatch> SearchAsync(TrustedObservationScope scope,TimeRange range,TraceFilter filter,CancellationToken ct)
    {
        if(filter.TraceId is not null)return new([TraceId(filter.TraceId)],false);
        var environments=string.Join(" || ",scope.EnvironmentIds.Select(id=>$"resource.webapi.environment.id = \"{id}\""));
        var query="{ ("+environments+")"+(filter.ApiId is Guid api?$" && span.webapi.api.id = \"{api}\"":"")+" } with (most_recent=true)";
        var parameters=Range(range);parameters["q"]=query;parameters["limit"]=(MaximumCandidates+1).ToString(CultureInfo.InvariantCulture);
        using var doc=await client.GetAsync(settings.TempoUrl,"api/search",parameters,ct);
        try{var rows=doc.RootElement.GetProperty("traces");if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>10000)throw ObservationSourceSettings.Unavailable("traces");var ids=new List<string>();var rejected=false;
            foreach(var row in rows.EnumerateArray()){var id=Identifier(row.GetProperty("traceID").GetString(),16);if(id is null){rejected=true;continue;}if(!ids.Contains(id))ids.Add(id);}
            return new(ids.Take(MaximumCandidates).ToArray(),rejected||rows.GetArrayLength()>MaximumCandidates);
        }catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException){throw ObservationSourceSettings.Unavailable("traces");}
    }
    public async Task<TraceSpanBatch> DetailAsync(string traceId,TimeRange range,CancellationToken ct)
    {
        using var doc=await client.GetAsync(settings.TempoUrl,"api/traces/"+TraceId(traceId),Range(range),ct,true);return Parse(doc.RootElement,traceId);
    }
    internal static Dictionary<string,string> Range(TimeRange range)=>new(){{"start",range.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)},{"end",((range.End.UtcTicks-DateTimeOffset.UnixEpoch.UtcTicks-1)/TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture)}};
    public static TraceSpanBatch Parse(JsonElement root,string traceId)
    {
        try{
            var batches=root.TryGetProperty("batches",out var b)?b:root.GetProperty("resourceSpans");var result=new List<SourceSpan>();var rejected=false;var count=0;
            foreach(var batch in batches.EnumerateArray()){
                var resource=Attributes(batch.GetProperty("resource"));Guid? env=Guid.TryParse(resource.GetValueOrDefault("webapi.environment.id"),out var environment)?environment:null;
                var scopes=batch.TryGetProperty("scopeSpans",out var s)?s:batch.GetProperty("instrumentationLibrarySpans");
                foreach(var scope in scopes.EnumerateArray())foreach(var span in scope.GetProperty("spans").EnumerateArray()){
                    if(++count>MaximumSpans){rejected=true;continue;}
                    var tid=Identifier(span.GetProperty("traceId").GetString(),16);var id=Identifier(span.GetProperty("spanId").GetString(),8);var rawParent=span.TryGetProperty("parentSpanId",out var p)?p.GetString():null;var parent=Identifier(rawParent,8);
                    if(tid!=traceId||id is null||(rawParent is {Length:>0}&&parent is null&&rawParent.Any(c=>c!='0')&&rawParent!="AAAAAAAAAAA=")){rejected=true;continue;}
                    if(!long.TryParse(span.GetProperty("startTimeUnixNano").GetString(),out var start)||!long.TryParse(span.GetProperty("endTimeUnixNano").GetString(),out var end)||start<0||end<start){rejected=true;continue;}
                    var startTime=DateTimeOffset.UnixEpoch.AddTicks(start/100);var duration=(end-start)/1_000_000d;var kindValue=span.TryGetProperty("kind",out var k)?k.ToString():"";
                    var kind=kindValue switch{"2" or "SPAN_KIND_SERVER"=>"Server","3" or "SPAN_KIND_CLIENT"=>"Client","1" or "SPAN_KIND_INTERNAL"=>"Internal","4" or "SPAN_KIND_PRODUCER"=>"Producer","5" or "SPAN_KIND_CONSUMER"=>"Consumer",_=>"Unspecified"};
                    var code=span.TryGetProperty("status",out var status)&&status.TryGetProperty("code",out var c)?c.ToString():"0";var state=code switch{"2" or "STATUS_CODE_ERROR"=>"Error","1" or "STATUS_CODE_OK"=>"Ok",_=>"Unset"};
                    result.Add(new(env,id,parent,span.GetProperty("name").GetString()??"",startTime,duration,kind,state,Attributes(span)));
                }
            }
            return new(result,rejected);
        }catch(Exception e)when(e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException){throw ObservationSourceSettings.Unavailable("traces");}
    }
    private static Dictionary<string,string> Attributes(JsonElement owner)
    {
        var result=new Dictionary<string,string>();if(!owner.TryGetProperty("attributes",out var rows))return result;
        if(rows.GetArrayLength()>256)throw ObservationSourceSettings.Unavailable("traces");
        foreach(var attr in rows.EnumerateArray()){var key=attr.GetProperty("key").GetString()??"";var value=attr.GetProperty("value");if(key.Length>128)continue;string? text=value.TryGetProperty("stringValue",out var str)?str.GetString():value.TryGetProperty("intValue",out var number)?number.ToString():null;
            if(text is not null&&text.Length<=512&&!text.Any(char.IsControl)&&!result.TryAdd(key,text))throw ObservationSourceSettings.Unavailable("traces");}
        return result;
    }
}

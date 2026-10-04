using System.Globalization;using System.Net;using System.Security.Cryptography;using System.Text;using System.Text.Json;using System.Text.RegularExpressions;using WebApi.Contracts.Common;using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed record PreparedLogFilter(LogFilter Filter,string? IpHmac,string Hash);
public sealed record SourceLog(long Nanoseconds,AccessLogDto Value);
public sealed record SourceLogBatch(IReadOnlyList<SourceLog> Rows,bool Capped,long? OldestNanoseconds,bool RejectedRows);
public sealed class LokiLogSource(ObservationSourceClient client,ObservationSourceSettings settings)
{
    public const int ProviderLimit=5000;
    public PreparedLogFilter Prepare(LogFilter filter)
    {
        if(filter.Status is not null&&!Regex.IsMatch(filter.Status,"^(?:[1-5][0-9]{2}|[1-5]xx|ClientAborted)$",RegexOptions.CultureInvariant)||filter.MinDurationMs is <0||filter.MaxDurationMs is <0||filter.MinDurationMs is double a&&!double.IsFinite(a)||filter.MaxDurationMs is double b&&!double.IsFinite(b)||filter.MinDurationMs>filter.MaxDurationMs||filter.Keyword is {Length:>256}||filter.Keyword?.Any(char.IsControl)==true||filter.TraceId is not null&&(!Regex.IsMatch(filter.TraceId,"^[a-fA-F0-9]{32}$")||filter.TraceId.All(x=>x=='0')))throw Invalid();
        string? hmac=null;if(filter.Ip is not null){if(!IPAddress.TryParse(filter.Ip,out var ip))throw Invalid();if(ip.IsIPv4MappedToIPv6)ip=ip.MapToIPv4();try{var key=Convert.FromBase64String(File.ReadAllText(settings.IpHmacSecretFile!).Trim());if(key.Length<32)throw new FormatException();hmac=Convert.ToHexStringLower(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(ip.ToString())));}catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException or FormatException){throw ObservationSourceSettings.Unavailable("logs");}}
        var clean=filter with{Ip=null,Cursor=null,TraceId=filter.TraceId?.ToLowerInvariant()};return new(clean,hmac,ObservationCursorCodec.Hash(new{Filter=clean,IpHmac=hmac}));
    }
    public async Task<SourceLogBatch> QueryAsync(TrustedObservationScope scope,TimeRange range,PreparedLogFilter filter,CursorBoundary? cursor,CancellationToken ct,Guid? logId=null)
    {
        var end=cursor is null?Nano(range.End):Math.Min(Nano(range.End),checked(cursor.Nanoseconds+1));
        using var document=await client.GetAsync(settings.LokiUrl,"loki/api/v1/query_range",new Dictionary<string,string>{{"query",Query(scope,filter,logId)},{"start",Nano(range.Start).ToString(CultureInfo.InvariantCulture)},{"end",end.ToString(CultureInfo.InvariantCulture)},{"limit",ProviderLimit.ToString(CultureInfo.InvariantCulture)},{"direction","backward"}},ct);
        try{
            var root=document.RootElement;if(root.GetProperty("status").GetString()!="success"||root.GetProperty("data").GetProperty("resultType").GetString()!="streams")throw ObservationSourceSettings.Unavailable("logs");
            var streams=root.GetProperty("data").GetProperty("result");var rows=new List<SourceLog>();var count=0;long? oldest=null;var rejected=false;
            foreach(var stream in streams.EnumerateArray()){
                var labels=stream.GetProperty("stream");var allowed=labels.TryGetProperty("service_name",out var service)&&service.GetString()=="webapi-gateway"&&labels.TryGetProperty("webapi_environment_id",out var env)&&Guid.TryParse(env.GetString(),out var environment)&&scope.EnvironmentIds.Contains(environment);
                foreach(var row in stream.GetProperty("values").EnumerateArray()){
                    if(++count>ProviderLimit)throw ObservationSourceSettings.Unavailable("logs");var ns=long.Parse(row[0].GetString()!,CultureInfo.InvariantCulture);oldest=oldest is null?ns:Math.Min(oldest.Value,ns);
                    if(!allowed||ns<Nano(range.Start)||ns>=Nano(range.End)||row.GetArrayLength()<2){rejected=true;continue;}
                    var environmentId=Guid.Parse(labels.GetProperty("webapi_environment_id").GetString()!);var meta=row.GetArrayLength()>=3?row[2]:labels;var dto=Project(meta,ns,environmentId,scope);if(dto is null){rejected=true;continue;}
                    if(logId is Guid id&&dto.Id!=id||!Matches(dto,meta,filter)||cursor is not null&&(ns>cursor.Nanoseconds||ns==cursor.Nanoseconds&&cursor.BoundaryIds.Contains(dto.Id)))continue;
                    rows.Add(new(ns,dto));
                }
            }
            return new(rows.GroupBy(x=>x.Value.Id).Select(x=>x.OrderByDescending(r=>r.Nanoseconds).First()).OrderByDescending(x=>x.Nanoseconds).ThenByDescending(x=>x.Value.Id).ToArray(),count==ProviderLimit,oldest,rejected);
        }catch(Exception e)when(e is JsonException or FormatException or OverflowException or ArgumentOutOfRangeException or InvalidOperationException or KeyNotFoundException){throw ObservationSourceSettings.Unavailable("logs");}
    }
    private static AccessLogDto? Project(JsonElement m,long ns,Guid environment,TrustedObservationScope scope)
    {
        string? Text(string key,int limit=128){if(!m.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.String)return null;var s=v.GetString();return s is not null&&s.Length<=limit&&!s.Any(char.IsControl)?s:null;}
        Guid? Id(string key)=>Guid.TryParse(Text(key),out var id)?id:null;
        var id=Id("webapi_log_id");if(id is null)return null;
        var environmentText=Text("webapi_environment_id");if(environmentText is not null&&environmentText!=environment.ToString())return null;
        var apiKey=Text("webapi_api_id");var api=Id("webapi_api_id");if(api is Guid a&&!scope.ApiNames.ContainsKey(a)||api is null&&apiKey!="Unmatched")return null;
        var app=Text("webapi_application_id");if(app is null)return null;if(Guid.TryParse(app,out var appId)){if(!scope.ApplicationNames.ContainsKey(appId))return null;}else if(app is not("Unknown" or "Anonymous"))return null;
        var destinationKey=Text("webapi_destination_id");var destination=Id("webapi_destination_id");if(destination is Guid d&&(!scope.Destinations.TryGetValue(d,out var target)||target.EnvironmentId!=environment)||destination is null&&destinationKey!="None")return null;
        var path=Text("url_template",512);if(path is null||path!="[unmatched]"&&(!path.StartsWith('/')||path.Any(c=>c is '?' or '#')))return null;
        var method=Text("http_request_method",16);if(method is null||!method.All(char.IsAsciiLetter))return null;
        if(!double.TryParse(Text("webapi_duration_ms"),NumberStyles.Float,CultureInfo.InvariantCulture,out var duration)||!double.IsFinite(duration)||duration<0)return null;
        var statusText=Text("http_response_status_code");int? status=int.TryParse(statusText,NumberStyles.None,CultureInfo.InvariantCulture,out var statusNumber)&&statusNumber is >=100 and <=599?statusNumber:null;if(status is null&&statusText!="None")return null;
        var trace=Text("trace_id",32);if(trace is not null&&(!Regex.IsMatch(trace,"^[0-9a-fA-F]{32}$")||trace.All(x=>x=='0')))trace=null;
        var masked=Text("webapi_client_ip_masked",80)??"Unknown";if(!Regex.IsMatch(masked,"^([0-9]{1,3}\\.){3}xxx$")&&!(masked.EndsWith("/48",StringComparison.Ordinal)&&IPAddress.TryParse(masked[..^3],out var maskedAddress)&&maskedAddress.GetAddressBytes().Length==16&&maskedAddress.GetAddressBytes().Skip(6).All(x=>x==0)))masked="Unknown";
        long? Number(string key)=>long.TryParse(Text(key),NumberStyles.None,CultureInfo.InvariantCulture,out var n)&&n>=0?n:null;
        var outcome=Text("webapi_outcome",32);if(outcome is not("Completed" or "ClientAborted" or "Timeout" or "ProxyError" or "Unauthorized" or "Forbidden" or "NotFound" or "Unavailable"))return null;
        return new(id.Value,new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks+ns/100,TimeSpan.Zero),environment,api,app,method,path,status,duration,outcome,Text("webapi_request_id")??"Unknown",trace?.ToLowerInvariant(),masked,Text("webapi_node_name")??"Unknown",Number("webapi_config_version"),Number("webapi_deployment_sequence"),destination);
    }
    private static string Query(TrustedObservationScope scope,PreparedLogFilter prepared,Guid? logId)
    {
        var f=prepared.Filter;var q="{service_name=\"webapi-gateway\",webapi_environment_id=~"+Quote(string.Join('|',scope.EnvironmentIds))+"}";
        if(f.ApiId is Guid api)q+=" | webapi_api_id="+Quote(api.ToString());if(f.ApplicationId is Guid app)q+=" | webapi_application_id="+Quote(app.ToString());if(f.DestinationId is Guid destination)q+=" | webapi_destination_id="+Quote(destination.ToString());if(logId is Guid id)q+=" | webapi_log_id="+Quote(id.ToString());
        if(f.Status is {Length:>0} status)q+=status=="ClientAborted"?" | webapi_outcome=\"ClientAborted\"":status.EndsWith("xx",StringComparison.Ordinal)?" | http_response_status_code=~"+Quote(status[0]+"[0-9]{2}"):" | http_response_status_code="+Quote(status);
        if(f.MinDurationMs is double min)q+=" | webapi_duration_ms >= "+min.ToString("R",CultureInfo.InvariantCulture);if(f.MaxDurationMs is double max)q+=" | webapi_duration_ms <= "+max.ToString("R",CultureInfo.InvariantCulture);
        if(prepared.IpHmac is not null)q+=" | webapi_client_ip_hmac="+Quote(prepared.IpHmac);if(f.TraceId is not null)q+=" | trace_id="+Quote(f.TraceId);
        if(f.Keyword is {Length:>0} word){q+=" | line_format \"{{.url_template}} {{.http_request_method}} {{.webapi_request_id}} {{.webapi_node_name}} {{.webapi_outcome}} {{.webapi_application_id}}\" |= "+Quote(word);}return q;
    }
    private static bool Matches(AccessLogDto d,JsonElement meta,PreparedLogFilter p)
    {var f=p.Filter;return(f.ApiId is null||d.ApiId==f.ApiId)&&(f.ApplicationId is null||d.ApplicationKey==f.ApplicationId.ToString())&&(f.DestinationId is null||d.DestinationId==f.DestinationId)&&(f.TraceId is null||d.TraceId==f.TraceId)&&(p.IpHmac is null||meta.TryGetProperty("webapi_client_ip_hmac",out var hash)&&hash.GetString()==p.IpHmac)&&(f.MinDurationMs is null||d.DurationMs>=f.MinDurationMs)&&(f.MaxDurationMs is null||d.DurationMs<=f.MaxDurationMs)&&(f.Status is null||f.Status=="ClientAborted"&&d.Outcome=="ClientAborted"||f.Status.EndsWith("xx",StringComparison.Ordinal)&&d.Status/100==f.Status[0]-'0'||f.Status==d.Status?.ToString(CultureInfo.InvariantCulture))&&(f.Keyword is null||string.Join(' ',d.PathTemplate,d.Method,d.RequestId,d.NodeName,d.Outcome,d.ApplicationKey).Contains(f.Keyword,StringComparison.Ordinal));}
    private static string Quote(string value)=>JsonSerializer.Serialize(value);
    public static long Nano(DateTimeOffset time)=>checked((time.UtcTicks-DateTimeOffset.UnixEpoch.UtcTicks)*100);
    private static ApiException Invalid()=>new(422,"invalid_observation_query","日志筛选参数不合法。");
}

using System.Globalization;
using System.Text.Json;
using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed record PrometheusSeries(IReadOnlyDictionary<string,string> Labels,double Value);
public sealed record PrometheusMatrix(IReadOnlyDictionary<string,string> Labels,IReadOnlyList<(DateTimeOffset Time,double Value)> Points);
public sealed class PrometheusMetricSource(ObservationSourceClient client,ObservationSourceSettings settings,ObservationCoverageService coverage)
{
    public static readonly string[] KpiKeys=["request_count","request_rps","success_ratio","error_4xx_count","error_4xx_ratio","error_5xx_count","error_5xx_ratio","latency_p50_ms","latency_p95_ms","latency_p99_ms","unhealthy_destinations","cancelled_count"];
    private const string ResourceLabels="webapi_environment_id,webapi_api_id,webapi_application_id,webapi_destination_id";
    public async Task<ObservationEnvelope<MetricsDto>> QueryAsync(TrustedObservationScope scope,TimeRange range,MetricFilter filter,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try{return await QueryCoreAsync(scope,range,filter,timeout.Token);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw ObservationSourceSettings.Unavailable();}
        catch(Exception error)when(error is JsonException or FormatException or OverflowException or ArgumentOutOfRangeException or InvalidOperationException or KeyNotFoundException){throw ObservationSourceSettings.Unavailable();}
    }
    private async Task<ObservationEnvelope<MetricsDto>> QueryCoreAsync(TrustedObservationScope scope,TimeRange range,MetricFilter filter,CancellationToken ct)
    {
        if(scope.EnvironmentIds.Count==0)return Empty(scope,range,filter);
        var seconds=(range.End-range.Start).TotalSeconds;var window=Math.Ceiling(seconds).ToString(CultureInfo.InvariantCulture)+"s";
        var step=Math.Max(15,(int)Math.Ceiling(seconds/599));var bucket=Math.Max(30,step)+"s";
        var all=Matcher(scope,null);var matcher=Matcher(scope,filter);
        var end=Seconds(range.End.AddTicks(-1));
        Task<IReadOnlyList<PrometheusSeries>> V(string query)=>VectorAsync(query,end,scope,ct);
        Task<IReadOnlyList<PrometheusMatrix>> M(string query)=>MatrixAsync(query,range,step,scope,ct);
        var latestTask=V("webapi_telemetry_last_observed_timestamp_seconds"+all);
        var firstTask=V("min_over_time(webapi_telemetry_last_observed_timestamp_seconds"+all+"["+window+"])");
        var requestTask=V($"sum by ({ResourceLabels},http_response_status_code,webapi_outcome,webapi_success) (clamp_min(increase(webapi_gateway_requests_total{matcher}[{window}]),0))");
        var histogramTask=V($"sum by ({ResourceLabels},le) (clamp_min(increase(webapi_gateway_request_duration_seconds_bucket{matcher}[{window}]),0))");
        var healthTask=V("webapi_destination_health"+all);
        var droppedTask=V($"sum by (webapi_environment_id) (increase(webapi_telemetry_dropped_total{Matcher(scope,null,"signal=\"metrics\"")}[{window}]))");
        var failedTask=V($"sum by (webapi_environment_id) (increase(webapi_telemetry_export_failures_total{Matcher(scope,null,"signal=\"metrics\"")}[{window}]))");
        var ratesTask=M($"sum by (webapi_environment_id,http_response_status_code,webapi_outcome,webapi_success) (clamp_min(rate(webapi_gateway_requests_total{matcher}[{bucket}]),0))");
        var latencyTrendTask=M($"sum by (webapi_environment_id,le) (clamp_min(rate(webapi_gateway_request_duration_seconds_bucket{matcher}[{bucket}]),0))");
        await Task.WhenAll(latestTask,firstTask,requestTask,histogramTask,healthTask,droppedTask,failedTask,ratesTask,latencyTrendTask);
        var requests=(await requestTask).Where(x=>ResourcesValid(x.Labels,scope)&&Matches(x.Labels,filter)).ToArray();
        var histogram=(await histogramTask).Where(x=>ResourcesValid(x.Labels,scope)&&Matches(x.Labels,filter)).ToArray();
        var health=await healthTask;
        var observed=coverage.Evaluate(scope,range,await latestTask,await firstTask,(await droppedTask).Sum(x=>x.Value)+(await failedTask).Sum(x=>x.Value));
        var missing=observed.State is SourceState.Partial or SourceState.Stale||scope.ExpectedNodes.Count==0;
        var values=Values(requests,histogram,seconds,Unhealthy(scope,health,filter),missing,observed.State);
        var groups=new List<MetricGroupDto>();
        if(filter.GroupBy!="None")
        {
            var key=filter.GroupBy switch{"Api"=>"webapi_api_id","Application"=>"webapi_application_id","Status"=>"http_response_status_code",_=>"webapi_destination_id"};
            foreach(var grouping in requests.GroupBy(x=>x.Labels.GetValueOrDefault(key)??"Unknown"))
            {
                var name=filter.GroupBy=="Status"?(grouping.Key=="0"?"未发送响应状态":"HTTP "+grouping.Key):Name(scope,filter.GroupBy,grouping.Key);
                groups.Add(new(grouping.Key,name,Values(grouping.ToArray(),filter.GroupBy=="Status"?[]:histogram.Where(x=>x.Labels.GetValueOrDefault(key)==grouping.Key).ToArray(),seconds,null,missing,observed.State)));
            }
        }
        var ordered=groups.OrderByDescending(x=>x.Values.First(v=>v.Metric==filter.SortBy).Value??double.NegativeInfinity).ThenBy(x=>x.Key,StringComparer.Ordinal).ToArray();
        var trends=Trends(await ratesTask,await latencyTrendTask,missing);
        var nodes=NodeHealth(scope,health,observed.Coverage.MissingNodes);
        var total=requests.Sum(x=>x.Value);var state=observed.State==SourceState.Available&&total==0?SourceState.NoData:observed.State;
        return new(state,range,observed.ObservedAt,observed.Coverage,new(settings.TraceSampleRatio,"ParentBasedTraceIdRatioBased"),new(values,trends,ordered.Skip((filter.Page-1)*filter.PageSize).Take(filter.PageSize).ToArray(),ordered.Length,filter.Page,filter.PageSize,nodes));
    }
    private static IReadOnlyList<MetricValueDto> Values(IReadOnlyList<PrometheusSeries> requests,IReadOnlyList<PrometheusSeries> histogram,double seconds,double? unhealthy,bool missing,SourceState state)
    {
        var count=requests.Sum(x=>x.Value);var success=requests.Where(x=>x.Labels.GetValueOrDefault("webapi_success")=="true").Sum(x=>x.Value);
        double Status(int lower,int upper)=>requests.Where(x=>int.TryParse(x.Labels.GetValueOrDefault("http_response_status_code"),out var s)&&s>=lower&&s<upper).Sum(x=>x.Value);
        var four=Status(400,500);var five=Status(500,600);var cancelled=requests.Where(x=>x.Labels.GetValueOrDefault("webapi_outcome")=="ClientAborted").Sum(x=>x.Value);
        var samples=(long)Math.Min(long.MaxValue,Math.Round(count));
        MetricValueDto Value(string key,double? value,string unit)=>new(key,missing?null:value,unit,samples,missing?state:value is null?SourceState.NoData:SourceState.Available);
        return[Value("request_count",count,"requests"),Value("request_rps",count/seconds,"requests/s"),Value("success_ratio",count>0?success/count:null,"ratio"),
            Value("error_4xx_count",four,"requests"),Value("error_4xx_ratio",count>0?four/count:null,"ratio"),Value("error_5xx_count",five,"requests"),Value("error_5xx_ratio",count>0?five/count:null,"ratio"),
            Value("latency_p50_ms",Quantile(histogram,0.5),"ms"),Value("latency_p95_ms",Quantile(histogram,0.95),"ms"),Value("latency_p99_ms",Quantile(histogram,0.99),"ms"),Value("unhealthy_destinations",unhealthy,"destinations"),Value("cancelled_count",cancelled,"requests")];
    }
    private static double? Quantile(IEnumerable<PrometheusSeries> rows,double quantile)
    {
        var buckets=new SortedDictionary<double,double>();foreach(var row in rows)
        {
            if(!row.Labels.TryGetValue("le",out var text))continue;var bound=text=="+Inf"?double.PositiveInfinity:double.Parse(text,CultureInfo.InvariantCulture);
            buckets[bound]=buckets.GetValueOrDefault(bound)+row.Value;
        }
        if(!buckets.TryGetValue(double.PositiveInfinity,out var total)||total<=0)return null;
        var rank=quantile*total;double lower=0,previous=0;
        foreach(var (upper,count) in buckets)
        {
            if(count<previous)return null;
            if(rank<=count)return double.IsPositiveInfinity(upper)?lower*1000:(lower+(upper-lower)*(rank-previous)/(count-previous))*1000;
            lower=upper;previous=count;
        }
        return null;
    }
    private static IReadOnlyDictionary<string,IReadOnlyList<MetricPointDto>> Trends(IReadOnlyList<PrometheusMatrix> rates,IReadOnlyList<PrometheusMatrix> histogram,bool missing)
    {
        var traffic=rates.SelectMany(x=>x.Points.Select(p=>(x.Labels,p.Time,p.Value))).GroupBy(x=>x.Time).OrderBy(x=>x.Key).Take(600).ToArray();
        var rps=traffic.Select(x=>new MetricPointDto(x.Key,missing?null:x.Sum(p=>p.Value))).ToArray();
        var errors=traffic.Select(x=>new MetricPointDto(x.Key,missing||x.Sum(p=>p.Value)==0?null:x.Where(p=>int.TryParse(p.Labels.GetValueOrDefault("http_response_status_code"),out var s)&&s>=500&&s<600).Sum(p=>p.Value)/x.Sum(p=>p.Value))).ToArray();
        var latency=histogram.SelectMany(x=>x.Points.Select(p=>(x.Labels,p.Time,p.Value))).GroupBy(x=>x.Time).OrderBy(x=>x.Key).Take(600).Select(x=>new MetricPointDto(x.Key,missing?null:Quantile(x.Select(p=>new PrometheusSeries(p.Labels,p.Value)),0.95))).ToArray();
        return new Dictionary<string,IReadOnlyList<MetricPointDto>>{{"request_rps",rps},{"error_5xx_ratio",errors},{"latency_p95_ms",latency}};
    }
    private static double? Unhealthy(TrustedObservationScope scope,IReadOnlyList<PrometheusSeries> health,MetricFilter filter)
    {
        var applicable=scope.Destinations.Values.Where(x=>x.Enabled&&(filter.DestinationId is null||x.Id==filter.DestinationId)).ToArray();
        var count=0;foreach(var destination in applicable)
        {
            var expected=scope.ExpectedNodes.Where(x=>x.EnvironmentId==destination.EnvironmentId).ToArray();
            if(expected.Length==0)return null;
            var rows=health.Where(x=>x.Labels.GetValueOrDefault("webapi_destination_id")==destination.Id.ToString()&&x.Labels.GetValueOrDefault("webapi_environment_id")==destination.EnvironmentId.ToString()).ToArray();
            if(rows.Any(x=>x.Value==0)){count++;continue;}
            if(expected.Any(node=>!rows.Any(x=>x.Labels.GetValueOrDefault("service_instance_id")==node.NodeName&&x.Value==1)))return null;
        }
        return count;
    }
    private static IReadOnlyList<NodeHealthDto> NodeHealth(TrustedObservationScope scope,IReadOnlyList<PrometheusSeries> health,IReadOnlyList<string> missing)=>scope.ExpectedNodes.Select(node=>new NodeHealthDto(node.NodeName,missing.Contains(node.NodeName)?"Stale":"Available",scope.Destinations.Values.Where(d=>d.EnvironmentId==node.EnvironmentId).Select(d=>{
        var row=health.FirstOrDefault(x=>x.Labels.GetValueOrDefault("webapi_environment_id")==node.EnvironmentId.ToString()&&x.Labels.GetValueOrDefault("service_instance_id")==node.NodeName&&x.Labels.GetValueOrDefault("webapi_destination_id")==d.Id.ToString());return new DestinationHealthDto(d.ClusterId,d.Id,!d.Enabled?"Disabled":row?.Value switch{1=>"Healthy",0=>"Unhealthy",_=>"Unknown"});
    }).ToArray(),node.EnvironmentId)).ToArray();
    private static string Name(TrustedObservationScope scope,string dimension,string key)
    {
        if(!Guid.TryParse(key,out var id))return key switch{"Unmatched"=>"未匹配 API","Unknown"=>"未知应用","Anonymous"=>"匿名请求","None"=>"未选择后端",_=>"未知资源"};
        return dimension switch{"Api"=>scope.ApiNames.GetValueOrDefault(id)??"未知 API","Application"=>scope.ApplicationNames.GetValueOrDefault(id)??"未知应用",_=>scope.Destinations.GetValueOrDefault(id)?.Name??"未知后端"};
    }
    private static string Matcher(TrustedObservationScope scope,MetricFilter? filter,string? extra=null)
    {
        var labels=new List<string>{"webapi_environment_id=~\""+string.Join('|',scope.EnvironmentIds.Select(x=>x.ToString()))+"\""};
        if(filter?.ApiId is Guid api)labels.Add("webapi_api_id=\""+api+"\"");if(filter?.ApplicationId is Guid app)labels.Add("webapi_application_id=\""+app+"\"");if(filter?.DestinationId is Guid destination)labels.Add("webapi_destination_id=\""+destination+"\"");if(extra is not null)labels.Add(extra);return"{"+string.Join(',',labels)+"}";
    }
    private static bool Matches(IReadOnlyDictionary<string,string> labels,MetricFilter filter)=>(filter.ApiId is null||labels.GetValueOrDefault("webapi_api_id")==filter.ApiId.ToString())&&(filter.ApplicationId is null||labels.GetValueOrDefault("webapi_application_id")==filter.ApplicationId.ToString())&&(filter.DestinationId is null||labels.GetValueOrDefault("webapi_destination_id")==filter.DestinationId.ToString());
    private static bool ResourcesValid(IReadOnlyDictionary<string,string> labels,TrustedObservationScope scope)
    {
        bool Valid(string label,IEnumerable<Guid> ids,params string[] system)=>labels.TryGetValue(label,out var value)&&(system.Contains(value)||Guid.TryParse(value,out var id)&&ids.Contains(id));
        if(!Valid("webapi_api_id",scope.ApiNames.Keys,"Unmatched")||!Valid("webapi_application_id",scope.ApplicationNames.Keys,"Unknown","Anonymous")||!Valid("webapi_destination_id",scope.Destinations.Keys,"None"))return false;
        return !Guid.TryParse(labels.GetValueOrDefault("webapi_destination_id"),out var destination)||scope.Destinations[destination].EnvironmentId.ToString()==labels.GetValueOrDefault("webapi_environment_id");
    }
    private async Task<IReadOnlyList<PrometheusSeries>> VectorAsync(string query,string time,TrustedObservationScope scope,CancellationToken ct)
    {
        using var document=await client.GetAsync(settings.PrometheusUrl,"api/v1/query",new Dictionary<string,string>{{"query",query},{"time",time}},ct);var rows=Result(document,"vector");var result=new List<PrometheusSeries>();
        foreach(var row in rows.EnumerateArray()){var labels=Labels(row,scope);if(labels is null)continue;var value=Number(row.GetProperty("value")[1]);result.Add(new(labels,value));}return result;
    }
    private async Task<IReadOnlyList<PrometheusMatrix>> MatrixAsync(string query,TimeRange range,int step,TrustedObservationScope scope,CancellationToken ct)
    {
        using var document=await client.GetAsync(settings.PrometheusUrl,"api/v1/query_range",new Dictionary<string,string>{{"query",query},{"start",Seconds(range.Start)},{"end",Seconds(range.End.AddTicks(-1))},{"step",step.ToString(CultureInfo.InvariantCulture)}},ct);var rows=Result(document,"matrix");var result=new List<PrometheusMatrix>();
        foreach(var row in rows.EnumerateArray())
        {
            var labels=Labels(row,scope);if(labels is null)continue;var points=new List<(DateTimeOffset,double)>();
            foreach(var point in row.GetProperty("values").EnumerateArray())
            {var time=DateTimeOffset.FromUnixTimeMilliseconds((long)(point[0].GetDouble()*1000));if(time<range.Start||time>=range.End)continue;var value=Number(point[1]);if(value<0)throw ObservationSourceSettings.Unavailable();points.Add((time,value));if(points.Count>600)throw ObservationSourceSettings.Unavailable();}
            result.Add(new(labels,points));
        }
        return result;
    }
    private static JsonElement Result(JsonDocument document,string type)
    {var root=document.RootElement;if(root.GetProperty("status").GetString()!="success"||root.GetProperty("data").GetProperty("resultType").GetString()!=type)throw ObservationSourceSettings.Unavailable();var result=root.GetProperty("data").GetProperty("result");if(result.GetArrayLength()>10000)throw ObservationSourceSettings.Unavailable();return result;}
    private static IReadOnlyDictionary<string,string>? Labels(JsonElement row,TrustedObservationScope scope)
    {
        var labels=row.GetProperty("metric").EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetString()??"");
        if(!Guid.TryParse(labels.GetValueOrDefault("webapi_environment_id"),out var environment)||!scope.EnvironmentIds.Contains(environment))return null;
        return labels;
    }
    private static double Number(JsonElement item)
    {if(!double.TryParse(item.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))throw ObservationSourceSettings.Unavailable();return value;}
    private static string Seconds(DateTimeOffset time)=>((decimal)(time.UtcTicks-DateTimeOffset.UnixEpoch.UtcTicks)/TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
    private ObservationEnvelope<MetricsDto> Empty(TrustedObservationScope scope,TimeRange range,MetricFilter filter)=>new(SourceState.NoData,range,null,new(false,[],"no_accessible_environments",false),new(settings.TraceSampleRatio,"ParentBasedTraceIdRatioBased"),new(Values([],[],(range.End-range.Start).TotalSeconds,null,true,SourceState.NoData),new Dictionary<string,IReadOnlyList<MetricPointDto>>(),[],0,filter.Page,filter.PageSize,[]));
}

using System.Globalization;using System.Text.Json;using WebApi.Contracts.Common;using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed class ObservationSignalCoverage(ObservationSourceClient client,ObservationSourceSettings settings,ObservationCoverageService coverage)
{
    public async Task<ObservationCoverage> QueryAsync(TrustedObservationScope scope,TimeRange range,string signal,CancellationToken ct)
    {
        if(signal is not("logs" or "traces"))throw new ArgumentException("Unsupported telemetry signal.",nameof(signal));
        if(scope.ExpectedNodes.Count==0)return coverage.Evaluate(scope,range,[],[],0);
        var match="{webapi_environment_id=~\""+string.Join('|',scope.EnvironmentIds)+"\"}";var diagnostics="{webapi_environment_id=~\""+string.Join('|',scope.EnvironmentIds)+"\",signal=\""+signal+"\"}";
        var window=Math.Ceiling((range.End-range.Start).TotalSeconds).ToString(CultureInfo.InvariantCulture)+"s";var end=((decimal)(range.End.UtcTicks-DateTimeOffset.UnixEpoch.UtcTicks-1)/TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
        async Task<IReadOnlyList<PrometheusSeries>> Fetch(string query){using var doc=await client.GetAsync(settings.PrometheusUrl,"api/v1/query",new Dictionary<string,string>{{"query",query},{"time",end}},ct);var root=doc.RootElement;
            if(root.GetProperty("status").GetString()!="success"||root.GetProperty("data").GetProperty("resultType").GetString()!="vector")throw ObservationSourceSettings.Unavailable();var rows=root.GetProperty("data").GetProperty("result");if(rows.GetArrayLength()>10000)throw ObservationSourceSettings.Unavailable();var result=new List<PrometheusSeries>();
            foreach(var row in rows.EnumerateArray()){var labels=row.GetProperty("metric").EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.GetString()??"");if(!Guid.TryParse(labels.GetValueOrDefault("webapi_environment_id"),out var env)||!scope.EnvironmentIds.Contains(env))continue;
                if(!double.TryParse(row.GetProperty("value")[1].GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value)||value<0)throw ObservationSourceSettings.Unavailable();result.Add(new(labels,value));}return result;}
        try{
            var latest=Fetch("webapi_telemetry_last_observed_timestamp_seconds"+match);var first=Fetch("min_over_time(webapi_telemetry_last_observed_timestamp_seconds"+match+"["+window+"])");
            var lost=Fetch("sum by (webapi_environment_id) (increase(webapi_telemetry_dropped_total"+diagnostics+"["+window+"]))");var failed=Fetch("sum by (webapi_environment_id) (increase(webapi_telemetry_export_failures_total"+diagnostics+"["+window+"]))");
            await Task.WhenAll(latest,first,lost,failed);var result=coverage.Evaluate(scope,range,await latest,await first,(await lost).Sum(x=>x.Value)+(await failed).Sum(x=>x.Value));
            return result.Coverage.Reason=="known_metrics_collection_gap"?result with{Coverage=result.Coverage with{Reason="known_"+signal+"_collection_gap"}}:result;
        }catch(ApiException){return new(SourceState.Partial,null,new(false,scope.ExpectedNodes.Select(x=>x.NodeName).ToArray(),"collection_coverage_source_unavailable",false));}
        catch(Exception e)when(e is JsonException or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException){return new(SourceState.Partial,null,new(false,[],"collection_coverage_source_unavailable",false));}
    }
}

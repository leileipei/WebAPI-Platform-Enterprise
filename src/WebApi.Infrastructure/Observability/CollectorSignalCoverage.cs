using System.Globalization;
using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
// A shared Collector cannot attribute its internal failures to an environment.
// Conservatively invalidate that signal's coverage without exposing its raw labels.
public sealed class CollectorSignalCoverage(ObservationSourceClient client,ObservationSourceSettings settings)
{
    public async Task<bool> HasGapAsync(TimeRange range,string signal,CancellationToken ct)
    {
        var suffix=signal switch{"logs"=>"log_records","traces"=>"spans","metrics"=>"metric_points",_=>throw new ArgumentException("Unsupported signal.",nameof(signal))};
        var window=Math.Ceiling((range.End-range.Start).TotalSeconds).ToString(CultureInfo.InvariantCulture)+"s";
        var failures="{job=\"webapi-collector-diagnostics\",__name__=~\"otelcol_(exporter_(send_failed|enqueue_failed)|receiver_refused)_"+suffix+"\"}";
        var up="up{job=\"webapi-collector-diagnostics\"}";
        // New nonzero counter series have no earlier baseline: increase alone is zero.
        var query="(1 - (min("+up+") or vector(0))) + (1 - (min(min_over_time("+up+"["+window+"])) or vector(0))) + (sum(increase("+failures+"["+window+"])) or vector(0)) + (sum("+failures+" unless "+failures+" offset "+window+") or vector(0))";
        var end=((decimal)(range.End.UtcTicks-DateTimeOffset.UnixEpoch.UtcTicks-1)/TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
        using var doc=await client.GetAsync(settings.PrometheusUrl,"api/v1/query",new Dictionary<string,string>{{"query",query},{"time",end}},ct);
        var root=doc.RootElement;
        if(root.GetProperty("status").GetString()!="success"||root.GetProperty("data").GetProperty("resultType").GetString()!="vector")throw ObservationSourceSettings.Unavailable();
        var rows=root.GetProperty("data").GetProperty("result");
        if(rows.GetArrayLength()!=1||!double.TryParse(rows[0].GetProperty("value")[1].GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value)||value<0)throw ObservationSourceSettings.Unavailable();
        return value>0;
    }
}

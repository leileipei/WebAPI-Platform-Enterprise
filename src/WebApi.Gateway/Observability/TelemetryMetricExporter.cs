using OpenTelemetry;
using OpenTelemetry.Metrics;
namespace WebApi.Gateway.Observability;
public sealed class TelemetryMetricExporter(BoundedTelemetryBuffer buffer,TelemetryDropTracker tracker) : BaseExporter<Metric>
{
    public override ExportResult Export(in Batch<Metric> batch)
    {
        try
        {
            foreach(var metric in batch)
            {
                foreach(ref readonly var point in metric.GetMetricPoints())
                {
                    var attributes=new List<object>();foreach(var tag in point.Tags)attributes.Add(TelemetryAttributes.Attribute(tag.Key,tag.Value));
                    if(attributes.Any()&&point.Tags.AnyOverflow())tracker.Record("metrics",1);
                    var data=new Dictionary<string,object>{["attributes"]=attributes,["startTimeUnixNano"]=TelemetryAttributes.Nano(point.StartTime),["timeUnixNano"]=TelemetryAttributes.Nano(point.EndTime)};
                    var value=new Dictionary<string,object>{["name"]=metric.Name,["unit"]=metric.Unit??""};
                    switch(metric.MetricType)
                    {
                        case MetricType.LongSum:data["asInt"]=point.GetSumLong().ToString();value["sum"]=new{aggregationTemporality=2,isMonotonic=true,dataPoints=new[]{data}};break;
                        case MetricType.DoubleSum:data["asDouble"]=point.GetSumDouble();value["sum"]=new{aggregationTemporality=2,isMonotonic=true,dataPoints=new[]{data}};break;
                        case MetricType.LongGauge:data["asInt"]=point.GetGaugeLastValueLong().ToString();value["gauge"]=new{dataPoints=new[]{data}};break;
                        case MetricType.DoubleGauge:data["asDouble"]=point.GetGaugeLastValueDouble();value["gauge"]=new{dataPoints=new[]{data}};break;
                        case MetricType.Histogram:
                            data["count"]=point.GetHistogramCount().ToString();data["sum"]=point.GetHistogramSum();var counts=new List<string>();var bounds=new List<double>();
                            foreach(var bucket in point.GetHistogramBuckets()){counts.Add(bucket.BucketCount.ToString());if(double.IsFinite(bucket.ExplicitBound))bounds.Add(bucket.ExplicitBound);}
                            data["bucketCounts"]=counts;data["explicitBounds"]=bounds;value["histogram"]=new{aggregationTemporality=2,dataPoints=new[]{data}};break;
                        default:continue;
                    }
                    buffer.TryWrite("metrics",TelemetryAttributes.Copy(value));
                }
            }
            return ExportResult.Success;
        }
        catch(Exception){tracker.Failed("metrics",1);return ExportResult.Failure;}
    }
}
internal static class OverflowTagExtensions
{
    public static bool AnyOverflow(this ReadOnlyTagCollection tags)
    {foreach(var tag in tags)if(tag.Key=="otel.metric.overflow"&&tag.Value is true)return true;return false;}
}

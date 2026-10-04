using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed record ObservationCoverage(SourceState State,DateTimeOffset? ObservedAt,CoverageDto Coverage);
public sealed class ObservationCoverageService
{
    public ObservationCoverage Evaluate(TrustedObservationScope scope,TimeRange range,IReadOnlyList<PrometheusSeries> latest,IReadOnlyList<PrometheusSeries> first,double gaps)
    {
        if(scope.ExpectedNodes.Count==0)return new(SourceState.NoData,null,new(false,[],"no_enabled_collection_nodes",false));
        var missing=new List<string>();var dates=new List<DateTimeOffset>();var historyMissing=false;
        var emptyEnvironments=scope.EnvironmentIds.Where(id=>!scope.ExpectedNodes.Any(node=>node.EnvironmentId==id)).ToArray();
        foreach(var node in scope.ExpectedNodes)
        {
            bool Same(PrometheusSeries row)=>row.Labels.GetValueOrDefault("webapi_environment_id")==node.EnvironmentId.ToString()&&row.Labels.GetValueOrDefault("service_instance_id")==node.NodeName;
            var row=latest.FirstOrDefault(Same);
            if(row is null||(range.End.ToUnixTimeMilliseconds()/1000d-row.Value)>45||row.Value>range.End.ToUnixTimeMilliseconds()/1000d+30){missing.Add(node.NodeName);continue;}
            dates.Add(DateTimeOffset.FromUnixTimeMilliseconds((long)(row.Value*1000)));
            var earliest=first.FirstOrDefault(Same);if(earliest is null||earliest.Value>range.Start.ToUnixTimeMilliseconds()/1000d+45)historyMissing=true;
        }
        var state=missing.Count>0||emptyEnvironments.Length>0?SourceState.Partial:historyMissing||gaps>0?SourceState.Partial:SourceState.Available;
        var reason=missing.Count>0?"missing_or_stale_nodes":emptyEnvironments.Length>0?"environments_without_collection_nodes":historyMissing?"time_window_coverage_incomplete":gaps>0?"known_metrics_collection_gap":null;
        return new(state,dates.Count>0?dates.Min():null,new(state==SourceState.Available,missing,reason,false));
    }
}

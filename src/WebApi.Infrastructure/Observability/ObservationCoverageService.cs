using WebApi.Contracts.Observability;
namespace WebApi.Infrastructure.Observability;
public sealed record ObservationCoverage(SourceState State, DateTimeOffset? ObservedAt, CoverageDto Coverage,
    IReadOnlySet<ExpectedObservationNode> MissingNodes)
{
    public static ObservationCoverage Create(TrustedObservationScope scope, SourceState state, DateTimeOffset? observedAt,
        IReadOnlyList<ExpectedObservationNode> missing, string? reason)
    {
        var coverage = new CoverageDto(state == SourceState.Available, missing.Select(node => node.NodeName).ToArray(), reason, false);
        if (missing.Count > 0 && scope.EnvironmentIds.Count > 1)
        {
            var expectedByName = scope.ExpectedNodes.ToLookup(node => node.NodeName, StringComparer.Ordinal);
            if (missing.Any(node => expectedByName[node.NodeName].Any(other => node.EnvironmentId != other.EnvironmentId)))
                coverage = coverage with { MissingNodeIdentities = missing.Select(node => new ObservationNodeIdentityDto(node.EnvironmentId, node.NodeName)).ToArray() };
        }
        return new(state, observedAt, coverage, missing.ToHashSet());
    }
}
public sealed class ObservationCoverageService
{
    public ObservationCoverage Evaluate(TrustedObservationScope scope,TimeRange range,IReadOnlyList<PrometheusSeries> latest,IReadOnlyList<PrometheusSeries> first,double gaps,IReadOnlyList<PrometheusSeries>? maxAge=null)
    {
        if(scope.ExpectedNodes.Count==0)return ObservationCoverage.Create(scope,SourceState.NoData,null,[],"no_enabled_collection_nodes");
        var missing=new List<ExpectedObservationNode>();var dates=new List<DateTimeOffset>();var historyMissing=false;
        var emptyEnvironments=scope.EnvironmentIds.Where(id=>!scope.ExpectedNodes.Any(node=>node.EnvironmentId==id)).ToArray();
        foreach(var node in scope.ExpectedNodes)
        {
            bool Same(PrometheusSeries row)=>row.Labels.GetValueOrDefault("webapi_environment_id")==node.EnvironmentId.ToString()&&row.Labels.GetValueOrDefault("service_instance_id")==node.NodeName;
            var row=latest.FirstOrDefault(Same);
            if(row is null||(range.End.ToUnixTimeMilliseconds()/1000d-row.Value)>45||row.Value>range.End.ToUnixTimeMilliseconds()/1000d+30){missing.Add(node);continue;}
            dates.Add(DateTimeOffset.FromUnixTimeMilliseconds((long)(row.Value*1000)));
            var earliest=first.FirstOrDefault(Same);if(earliest is null||earliest.Value>range.Start.ToUnixTimeMilliseconds()/1000d+45)historyMissing=true;
            if(maxAge is not null){var age=maxAge.FirstOrDefault(Same);if(age is null||age.Value>45||age.Value< -30)historyMissing=true;}
        }
        var state=missing.Count>0||emptyEnvironments.Length>0?SourceState.Partial:historyMissing||gaps>0?SourceState.Partial:SourceState.Available;
        var reason=missing.Count>0?"missing_or_stale_nodes":emptyEnvironments.Length>0?"environments_without_collection_nodes":historyMissing?"time_window_coverage_incomplete":gaps>0?"known_metrics_collection_gap":null;
        return ObservationCoverage.Create(scope,state,dates.Count>0?dates.Min():null,missing,reason);
    }
}

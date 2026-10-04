namespace WebApi.Contracts.Observability;

public sealed record MetricFilter(Guid? ApiId, Guid? ApplicationId, Guid? DestinationId, string GroupBy = "None",
    int Page = 1, int PageSize = 50, string SortBy = "request_count");
public sealed record MetricValueDto(string Metric, double? Value, string Unit, long SampleCount,
    SourceState State = SourceState.Available);
public sealed record MetricPointDto(DateTimeOffset Time, double? Value);
public sealed record MetricGroupDto(string Key, string Name, IReadOnlyList<MetricValueDto> Values);
public sealed record DestinationHealthDto(Guid ClusterId, Guid DestinationId, string Health);
public sealed record NodeHealthDto(string NodeName, string Health, IReadOnlyList<DestinationHealthDto> Destinations);
public sealed record MetricsDto(IReadOnlyList<MetricValueDto> Kpis,
    IReadOnlyDictionary<string, IReadOnlyList<MetricPointDto>> Trends, IReadOnlyList<MetricGroupDto> Groups,
    int TotalGroups, int Page, int PageSize, IReadOnlyList<NodeHealthDto> NodeHealth);

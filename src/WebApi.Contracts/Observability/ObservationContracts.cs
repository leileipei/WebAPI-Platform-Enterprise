namespace WebApi.Contracts.Observability;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SourceState>))]
public enum SourceState { Available, NoData, Partial, Unavailable, Stale, NotApplicable }

public sealed record CoverageDto(bool Complete, IReadOnlyList<string> MissingNodes, string? Reason, bool Truncated);
public sealed record SamplingDto(double Ratio, string Mode);
public sealed record ObservationEnvelope<T>(SourceState SourceState, TimeRange Range, DateTimeOffset? ObservedAt,
    CoverageDto Coverage, SamplingDto? Sampling, T? Data);
public sealed record ObservationScopeRequest(Guid OrganizationId, Guid ProjectId, Guid? EnvironmentId, bool AllAccessibleEnvironments);

public sealed record TimeRange
{
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public TimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        Start = start.ToUniversalTime();
        End = end.ToUniversalTime();
    }
}

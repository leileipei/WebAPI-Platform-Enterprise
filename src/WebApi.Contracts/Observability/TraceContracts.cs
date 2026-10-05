using WebApi.Contracts.Policies;
namespace WebApi.Contracts.Observability;

public sealed record TraceFilter(string? TraceId = null, Guid? ApiId = null, double? MinDurationMs = null,
    string? Outcome = null, int Limit = 50, string? Cursor = null,Guid? PolicyId=null,string? PolicyDecision=null);
public sealed record TraceSummaryDto(string TraceId, DateTimeOffset Start, double DurationMs, string Outcome);
public sealed record TraceSpanDto(string SpanId, string? ParentSpanId, string Name, DateTimeOffset Start,
    double DurationMs, string Kind, string Status, IReadOnlyDictionary<string, string> Tags,IReadOnlyList<PolicyDecisionDto>? PolicyDecisions=null)
{public IReadOnlyList<PolicyDecisionDto> PolicyDecisions {get;init;}=PolicyDecisions??[];}
public sealed record TraceDetailDto(string TraceId, bool PartialTrace, IReadOnlyList<TraceSpanDto> Spans);

using WebApi.Contracts.Policies;
using System.Text.Json.Serialization;
namespace WebApi.Contracts.Observability;

public sealed record LogFilter(Guid? ApiId = null, Guid? ApplicationId = null, string? Status = null,
    double? MinDurationMs = null, double? MaxDurationMs = null, string? Ip = null, string? Keyword = null,
    string? TraceId = null, int Limit = 50, string? Cursor = null, Guid? DestinationId = null,Guid? PolicyId=null,string? PolicyDecision=null);
public sealed record AccessLogDto(Guid Id, DateTimeOffset Time, Guid EnvironmentId, Guid? ApiId,
    string ApplicationKey, string Method, string PathTemplate, int? Status, double DurationMs, string Outcome,
    string RequestId, string? TraceId, string MaskedIp, string NodeName, long? ConfigVersion,
    long? DeploymentSequence, Guid? DestinationId,IReadOnlyList<PolicyDecisionDto>? PolicyDecisions=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]int? AttemptCount=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? CacheDisposition=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]IReadOnlyList<ForwardAttemptObservation>? ForwardAttempts=null)
{public IReadOnlyList<PolicyDecisionDto> PolicyDecisions {get;init;}=PolicyDecisions??[];}
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool Truncated);

namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class AlertEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public long RuleRevision { get; set; }
    public long LogicRevision { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public Guid? ResourceId { get; set; }
    public long OccurrenceNo { get; set; }
    public string Status { get; set; } = "Open";
    public string Severity { get; set; } = "Warning";
    public string Message { get; set; } = string.Empty;
    public string RuleSummary { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ConditionStartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? AckedBy { get; set; }
    public DateTimeOffset? AckedAt { get; set; }
    public Guid? SilencedBy { get; set; }
    public DateTimeOffset? SilencedUntil { get; set; }
    public string? SilenceReason { get; set; }
    public Guid? ResolvedBy { get; set; }
    public string? ResolveReason { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
    public double? LastValue { get; set; }
    public bool? LastCondition { get; set; }
    public string EvaluationState { get; set; } = "Unknown";
    public long Revision { get; set; } = 1;
}

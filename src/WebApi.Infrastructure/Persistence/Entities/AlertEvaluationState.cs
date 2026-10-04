namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class AlertEvaluationState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public long LogicRevision { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public Guid? ResourceId { get; set; }
    public string Phase { get; set; } = "Inactive";
    public DateTimeOffset? PendingSince { get; set; }
    public DateTimeOffset? LastEvaluatedSlot { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public bool? LastCondition { get; set; }
    public Guid? LastEventId { get; set; }
    public long NextOccurrenceNo { get; set; } = 1;
    public string EvaluationState { get; set; } = "Unknown";
    public DateTimeOffset? SuppressedAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long LeaseToken { get; set; }
    public long Revision { get; set; } = 1;
}

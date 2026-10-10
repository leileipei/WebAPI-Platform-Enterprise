namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleasePipelineEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? StageId { get; set; }
    public Guid? AttemptId { get; set; }
    public string FromStatus { get; set; } = string.Empty;
    public string ToStatus { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public Guid? ActorId { get; set; }
    public Guid? RelatedId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleasePipelineStageAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunStageId { get; set; }
    public Guid RunId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public int AttemptNo { get; set; }
    public Guid? OriginAttemptId { get; set; }
    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DeadlineAt { get; set; }
    public string Status { get; set; } = "Active";
    public string ContextJson { get; set; } = "{}";
    public Guid? ArtifactId { get; set; }
    public Guid? AcceptanceId { get; set; }
    public Guid? PromotionId { get; set; }
    public Guid? ActualReleaseId { get; set; }
    public long Revision { get; set; } = 1;
}

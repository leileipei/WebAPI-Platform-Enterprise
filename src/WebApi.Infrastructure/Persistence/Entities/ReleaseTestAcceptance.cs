namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleaseTestAcceptance
{
    public Guid? PipelineRunStageId { get; set; }
    public Guid? StageAttemptId { get; set; }
    public string? ProfileHash { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid SourceEnvironmentId { get; set; }
    public Guid[] VerificationIds { get; set; } = [];
    public string EvidenceHash { get; set; } = string.Empty;
    public long PolicyRevision { get; set; }
    public string Status { get; set; } = "Requested";
    public long Revision { get; set; } = 1;
    public Guid RequestedBy { get; set; }
    public Guid? ActedBy { get; set; }
    public string Comment { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ActedAt { get; set; }
}

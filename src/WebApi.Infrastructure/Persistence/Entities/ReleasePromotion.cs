namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleasePromotion
{
    public Guid? PipelineRunStageId { get; set; }
    public Guid? StageAttemptId { get; set; }
    public string GateOrigin { get; set; } = "ProjectConnection";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid SourceEnvironmentId { get; set; }
    public Guid TargetEnvironmentId { get; set; }
    public Guid SourceReleaseId { get; set; }
    public Guid? TargetReleaseId { get; set; }
    public Guid? AcceptanceId { get; set; }
    public string Status { get; set; } = "Draft";
    public long BaselineConfigVersion { get; set; } = 0;
    public long MappingRevision { get; set; } = 0;
    public string? CandidateHash { get; set; }
    public string ResourceRevisionsJson { get; set; } = "[]";
    public string FrozenPolicyJson { get; set; } = "{}";
    public string? PrecheckJson { get; set; }
    public long TargetAccessAddressRevision { get; set; } = 0;
    public string? VerificationContextJson { get; set; }
    public long Revision { get; set; } = 1;
    public Guid RequestedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

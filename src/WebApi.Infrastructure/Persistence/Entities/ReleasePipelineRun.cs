namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleasePipelineRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PipelineVersionId { get; set; }
    public string DefinitionHash { get; set; } = string.Empty;
    public Guid RootArtifactId { get; set; }
    public string RootArtifactHash { get; set; } = string.Empty;
    public Guid SourceEnvironmentId { get; set; }
    public Guid SourceReleaseId { get; set; }
    public long SourceConfigVersion { get; set; }
    public long SourceDeploymentSequence { get; set; }
    public long PolicyRevision { get; set; }
    public string Status { get; set; } = "Active";
    public int CurrentStageOrder { get; set; } = 1;
    public long Revision { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProjectionCheckedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

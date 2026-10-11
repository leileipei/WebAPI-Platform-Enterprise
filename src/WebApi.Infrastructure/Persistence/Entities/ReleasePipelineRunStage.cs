namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleasePipelineRunStage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid ProjectId { get; set; }
    public int StageOrder { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid? SourceStageId { get; set; }
    public Guid? StageArtifactId { get; set; }
    public Guid? CurrentAttemptId { get; set; }
    public string ProfileJson { get; set; } = "{}";
    public string ProfileHash { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";
    public long Revision { get; set; } = 1;
}

namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleaseVerification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid? PromotionId { get; set; }
    public Guid ReleaseId { get; set; }
    public Guid EnvironmentId { get; set; }
    public long ConfigVersion { get; set; }
    public long DeploymentSequence { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public long AccessAddressRevision { get; set; }
    public string AccessContextJson { get; set; } = "{}";
    public long PolicyRevision { get; set; }
    public string Phase { get; set; } = "SourceTest";
    public string Type { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public bool IsManual { get; set; } = true;
    public Guid? ReportId { get; set; }
    public string? ReportHash { get; set; }
    public string Comment { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

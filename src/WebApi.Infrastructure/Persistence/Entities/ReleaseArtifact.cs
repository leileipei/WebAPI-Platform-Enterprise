namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleaseArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SourceEnvironmentId { get; set; }
    public Guid SourceReleaseId { get; set; }
    public string CanonicalContent { get; set; } = "{}";
    public string ArtifactHash { get; set; } = string.Empty;
    public string SourceSnapshotHash { get; set; } = string.Empty;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class GatewayConfigVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvironmentId { get; set; }
    public long VersionNo { get; set; } = 1;
    public string Status { get; set; } = "Active";
    public string? SnapshotKey { get; set; }
    public string? SnapshotHash { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}

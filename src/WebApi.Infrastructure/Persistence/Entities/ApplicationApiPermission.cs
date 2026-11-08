namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApplicationApiPermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public Guid ApiId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string Mode { get; set; } = "RouteMethod";
    public DateTimeOffset ValidFrom { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long ExpiryRevision { get; set; } = 1;
    public long Revision { get; set; } = 1;
}

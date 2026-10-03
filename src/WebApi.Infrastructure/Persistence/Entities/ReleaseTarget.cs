namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleaseTarget
{
    public Guid ReleaseId { get; set; }
    public Guid NodeId { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

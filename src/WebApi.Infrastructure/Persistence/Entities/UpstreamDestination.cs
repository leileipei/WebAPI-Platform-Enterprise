namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class UpstreamDestination
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClusterId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Weight { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Revision { get; set; } = 1;
}

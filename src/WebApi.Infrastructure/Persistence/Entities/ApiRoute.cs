namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApiRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApiVersionId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string RouteName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string NormalizedPath { get; set; } = string.Empty;
    public string[] Methods { get; set; } = [];
    public Guid ClusterId { get; set; }
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;
    public int TimeoutMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Revision { get; set; } = 1;
}

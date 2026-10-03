namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApiVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApiId { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string? OpenapiDocument { get; set; }
    public string? SchemaHash { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Revision { get; set; } = 1;
    public string? OpenapiSource { get; set; }
    public string? SourceFormat { get; set; }
    public DateTimeOffset? SealedAt { get; set; }
}

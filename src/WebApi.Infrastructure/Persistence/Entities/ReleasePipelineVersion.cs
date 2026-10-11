namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleasePipelineVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PipelineId { get; set; }
    public Guid ProjectId { get; set; }
    public int VersionNo { get; set; }
    public string ContentJson { get; set; } = "{}";
    public string DefinitionHash { get; set; } = string.Empty;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

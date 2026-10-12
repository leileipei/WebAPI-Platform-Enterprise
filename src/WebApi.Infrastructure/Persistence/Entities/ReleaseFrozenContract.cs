namespace WebApi.Infrastructure.Persistence.Entities;

// Derived from the authoritative frozen candidate; contains no consumer credentials.
public sealed class ReleaseFrozenContract
{
    public Guid ReleaseId { get; set; }
    public int Ordinal { get; set; }
    public Guid EnvironmentId { get; set; }
    public long ConfigVersion { get; set; }
    public Guid ApiId { get; set; }
    public Guid ApiVersionId { get; set; }
    public string FrozenJson { get; set; } = string.Empty;
}

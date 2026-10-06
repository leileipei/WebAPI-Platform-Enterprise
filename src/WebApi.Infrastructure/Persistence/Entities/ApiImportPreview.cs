namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApiImportPreview
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid ClusterId { get; set; }
    public Guid ActorId { get; set; }
    public long SourcePolicyRevision { get; set; }
    public string? BundleJson { get; set; }
    public string? PreviewJson { get; set; }
    public string SourceHash { get; set; } = "";
    public string BundleHash { get; set; } = "";
    public string SourceFormat { get; set; } = "json";
    public string Dialect { get; set; } = "Oas30";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = "Active";
    public Guid? ImportId { get; set; }
    public string? CommittedTargetsJson { get; set; }
    public DateTimeOffset? ReceiptExpiresAt { get; set; }
    public long Revision { get; set; } = 1;
}

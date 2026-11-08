namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApplicationCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public string AccessKey { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public string SecretLast4 { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public DateTimeOffset ValidFrom { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid OrganizationId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? EnvironmentId { get; set; }
    public string BindingMode { get; set; } = "LegacyGlobal";
    public long ExpiryRevision { get; set; } = 1;
    public long Revision { get; set; } = 1;
}

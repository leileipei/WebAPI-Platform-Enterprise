namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class SsoProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ProviderType { get; set; } = "oidc";
    public string Issuer { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    // Protected digest, scoped to provider and authentication revision; never exposed by DTOs.
    public string? ProtectedSecretFingerprint { get; set; }
    public string SecretRef { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[\"openid\",\"profile\",\"email\"]";
    public string ClaimMappingJson { get; set; } = "{}";
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
    public long Revision { get; set; } = 1;
    public long AuthRevision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class SsoLoginAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderId { get; set; }
    public long ProviderRevision { get; set; }
    public long AuthRevision { get; set; }
    public string ReturnPath { get; set; } = "/organizations";
    public string State { get; set; } = "Pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public string? FailureCode { get; set; }
}

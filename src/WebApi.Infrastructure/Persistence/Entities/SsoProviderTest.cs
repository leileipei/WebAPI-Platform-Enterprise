namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class SsoProviderTest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderId { get; set; }
    public long ProviderRevision { get; set; }
    public DateTimeOffset TestedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "Failed";
    public string StagesJson { get; set; } = "[]";
}

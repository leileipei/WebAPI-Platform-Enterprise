namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class GatewayConfigSnapshot
{
    public Guid ConfigVersionId { get; set; }
    public string Payload { get; set; } = "{}";
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public byte[] PayloadBytes { get; set; } = [];
}

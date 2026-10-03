namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class GatewayAck
{
    public Guid ReleaseId { get; set; }
    public Guid NodeId { get; set; }
    public string InstanceId { get; set; } = string.Empty;
    public long ConfigVersion { get; set; }
    public long DeploymentSequence { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
}

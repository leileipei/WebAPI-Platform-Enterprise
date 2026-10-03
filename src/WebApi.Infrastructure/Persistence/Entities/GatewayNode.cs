namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class GatewayNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvironmentId { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public long CurrentConfigVersion { get; set; }
    public long? TargetConfigVersion { get; set; }
    public string Status { get; set; } = "Active";
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public string? Metadata { get; set; }
    public long CurrentDeploymentSequence { get; set; }
    public string IdentityHash { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

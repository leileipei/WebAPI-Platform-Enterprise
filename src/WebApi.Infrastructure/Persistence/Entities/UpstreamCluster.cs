namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class UpstreamCluster
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LoadBalancingPolicy { get; set; } = string.Empty;
    public bool HealthCheckEnabled { get; set; }
    public string HealthCheckPath { get; set; } = string.Empty;
    public int HealthCheckIntervalSec { get; set; }
    public string Status { get; set; } = "Active";
    public long Revision { get; set; } = 1;
}

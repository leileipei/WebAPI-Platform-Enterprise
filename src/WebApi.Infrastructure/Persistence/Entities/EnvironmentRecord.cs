namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class EnvironmentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsProduction { get; set; }
    public int SortOrder { get; set; }
    public string Status { get; set; } = "Active";
    public Guid? ReleasePolicyId { get; set; }
    public long? DesiredConfigVersion { get; set; }
    public long DeploymentSequence { get; set; }
    public string? GatewayPublicUrl { get; set; }
    public string? GatewayInternalUrl { get; set; }
    public string BasePath { get; set; } = "/";
    public long AccessAddressRevision { get; set; } = 1;
    public long Revision { get; set; } = 1;
}

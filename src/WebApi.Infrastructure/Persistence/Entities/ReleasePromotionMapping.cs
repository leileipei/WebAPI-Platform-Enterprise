namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleasePromotionMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PromotionId { get; set; }
    public Guid TargetEnvironmentId { get; set; }
    public string ResourceKey { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Guid? TargetRouteId { get; set; }
    public Guid? ClusterId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string ParametersJson { get; set; } = "{}";
    public long Revision { get; set; } = 1;
}

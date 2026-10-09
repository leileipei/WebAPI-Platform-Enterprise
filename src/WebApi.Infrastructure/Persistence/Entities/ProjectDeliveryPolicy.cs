namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ProjectDeliveryPolicy
{
    public Guid? ActivePipelineVersionId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SourceEnvironmentId { get; set; }
    public Guid TargetEnvironmentId { get; set; }
    public string Mode { get; set; } = "Legacy";
    public string[] RequiredTestTypes { get; set; } = ["InterfaceFunction","Integration","ContractCompatibility"];
    public int VerificationValidityMinutes { get; set; } = 1440;
    public long Revision { get; set; } = 1;
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

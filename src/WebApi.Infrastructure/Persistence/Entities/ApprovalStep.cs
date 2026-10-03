namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApprovalStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowId { get; set; }
    public int StepOrder { get; set; }
    public string RoleCode { get; set; } = string.Empty;
    public int RequiredCount { get; set; }
}

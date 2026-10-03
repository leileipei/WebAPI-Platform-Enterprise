namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApprovalTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FlowId { get; set; }
    public Guid ReleaseId { get; set; }
    public int StepOrder { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public string Status { get; set; } = "Active";
    public string? Comment { get; set; }
    public DateTimeOffset? ActedAt { get; set; }
}

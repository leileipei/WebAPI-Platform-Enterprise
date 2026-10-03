namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleaseRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EnvironmentId { get; set; }
    public string ReleaseNo { get; set; } = string.Empty;
    public long FromConfigVersion { get; set; }
    public long ToConfigVersion { get; set; }
    public string ReleaseType { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public Guid RequestedBy { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? RollbackOf { get; set; }
    public Guid? RecoveryOf { get; set; }
    public long? DeploymentSequence { get; set; }
    public long BaselineConfigVersion { get; set; }
    public byte[]? CandidateBytes { get; set; }
    public string? ApprovalPolicy { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public string? FailureCode { get; set; }
    public Guid? PublishRequestedBy { get; set; }
    public string? PublishTraceId { get; set; }
}

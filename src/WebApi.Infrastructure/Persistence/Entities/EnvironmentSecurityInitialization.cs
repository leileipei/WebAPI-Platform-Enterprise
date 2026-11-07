namespace WebApi.Infrastructure.Persistence.Entities;

/// <summary>Persists the independently established security origin of an environment.</summary>
public sealed class EnvironmentSecurityInitialization
{
    public Guid EnvironmentId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OrganizationId { get; set; }
    public string State { get; set; } = "Unknown";
    public string? EvidenceHash { get; set; }
    public Guid? FreshProofId { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? ConfirmationReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public long Revision { get; set; } = 1;
}

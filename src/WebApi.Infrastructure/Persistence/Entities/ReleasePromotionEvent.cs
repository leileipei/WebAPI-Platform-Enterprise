namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ReleasePromotionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? PromotionId { get; set; }
    public Guid? AcceptanceId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public Guid? ActorId { get; set; }
    public Guid? ReleaseId { get; set; }
    public Guid? VerificationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

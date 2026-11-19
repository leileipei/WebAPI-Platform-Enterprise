namespace WebApi.Infrastructure.Persistence.Entities;

public sealed class FormalRotationDecision
{
    public Guid Id { get; init; }
    public Guid ScheduleId { get; init; }
    public string Kind { get; init; } = string.Empty;
    public DateTimeOffset RecordedAt { get; init; }
    public string IntentHash { get; init; } = string.Empty;
}

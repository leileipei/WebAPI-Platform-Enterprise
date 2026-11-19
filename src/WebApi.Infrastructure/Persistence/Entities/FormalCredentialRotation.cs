namespace WebApi.Infrastructure.Persistence.Entities;

public sealed class FormalCredentialRotation
{
    public Guid Id { get; init; }
    public Guid CredentialId { get; init; }
    public Guid ReplacementCredentialId { get; init; }
    public Guid EnvironmentId { get; init; }
    public DateTimeOffset HardDeadline { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string IntentHash { get; init; } = string.Empty;
}

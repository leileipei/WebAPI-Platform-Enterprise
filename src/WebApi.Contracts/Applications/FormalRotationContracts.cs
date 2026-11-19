namespace WebApi.Contracts.Applications;

public sealed record FormalRotationPreviewRequest(Guid EnvironmentId, DateTimeOffset NewValidFrom,
    DateTimeOffset NewExpiresAt, DateTimeOffset OverlapUntil, DateTimeOffset HardDeadline,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid? ReplacementCredentialId = null);

public sealed record FormalRotationConfirmRequest(FormalRotationPreviewRequest Window, string PreviewHash,
    bool? ConfirmAutomaticRevocation, string Reason);

public sealed record FormalRotationDecisionRequest(bool? Confirm, string Reason);

public sealed record FormalRotationEligibilityDto(IReadOnlyList<Guid> CredentialIds);

public sealed record FormalRotationReceipt(Guid RotationId, Guid CredentialId, Guid ReplacementCredentialId,
    Guid EnvironmentId, DateTimeOffset RecordedAt, DateTimeOffset HardDeadline, string Status, Guid? RevocationOperationId, bool Replayed)
{
    public long Revision { get; init; } = 1;
    public Guid? AbortRevocationOperationId { get; init; }
}

public sealed record FormalRotationPreviewTarget(Guid NodeId, Guid InstanceId);

public sealed record FormalRotationVerificationTarget(Guid NodeId, Guid InstanceId, string State);

public sealed record FormalRotationVerificationDto(Guid RotationId, string State, long? ConfigVersion,
    long? DeploymentSequence, IReadOnlyList<FormalRotationVerificationTarget> Targets);

public sealed record FormalRotationPreviewDto(Guid CredentialId, Guid ApplicationId, Guid EnvironmentId,
    string MaskedAccessKey, long CredentialRevision, string PreviewHash, long ConfigVersion, long DeploymentSequence,
    DateTimeOffset NewValidFrom, DateTimeOffset NewExpiresAt, DateTimeOffset OverlapUntil, DateTimeOffset HardDeadline,
    bool LastWorkingCredentialAtDeadline, bool LastPublishedCredentialAtDeadline,
    IReadOnlyList<FormalRotationPreviewTarget> Targets);

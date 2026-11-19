namespace WebApi.Contracts.Applications;

public sealed record FormalRotationPreviewRequest(Guid EnvironmentId, DateTimeOffset NewValidFrom,
    DateTimeOffset NewExpiresAt, DateTimeOffset OverlapUntil, DateTimeOffset HardDeadline);

public sealed record FormalRotationPreviewTarget(Guid NodeId, Guid InstanceId);

public sealed record FormalRotationPreviewDto(Guid CredentialId, Guid ApplicationId, Guid EnvironmentId,
    string MaskedAccessKey, long CredentialRevision, string PreviewHash, long ConfigVersion, long DeploymentSequence,
    DateTimeOffset NewValidFrom, DateTimeOffset NewExpiresAt, DateTimeOffset OverlapUntil, DateTimeOffset HardDeadline,
    bool LastWorkingCredentialAtDeadline, bool LastPublishedCredentialAtDeadline,
    IReadOnlyList<FormalRotationPreviewTarget> Targets);

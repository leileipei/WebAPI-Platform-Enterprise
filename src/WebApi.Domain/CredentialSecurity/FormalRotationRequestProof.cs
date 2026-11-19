namespace WebApi.Domain.CredentialSecurity;

public sealed record FormalRotationRequestProof(
    Guid EnvironmentId,
    Guid NodeId,
    Guid InstanceId,
    Guid CredentialId,
    Guid ApplicationId,
    Guid RouteId,
    string Method,
    Guid ReleaseId,
    long ConfigVersion,
    long DeploymentSequence,
    string SnapshotHash,
    Guid SecurityGeneration,
    long SecurityEpoch,
    string SecurityHeadHash,
    Guid LeaseId,
    DateTimeOffset AdmittedAt,
    DateTimeOffset CompletedAt,
    int StatusCode);

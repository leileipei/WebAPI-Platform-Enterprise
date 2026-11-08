namespace WebApi.Contracts.Applications;
public sealed record RevokeCredentialRequest(string Reason,Guid? ExpectedEnvironmentId=null);
public sealed record RevocationEnvironmentDto(Guid EnvironmentId,Guid SecurityGeneration,long Epoch,string HeadHash);
public sealed record RevocationTargetDto(Guid EnvironmentId,Guid NodeId,Guid InstanceGuid,string State,string? NodeName=null,Guid? CurrentInstanceGuid=null,string? Reason=null,DateTimeOffset? LastLeaseUntil=null,DateTimeOffset? LastHeartbeatAt=null,string? ErrorCode=null);
public sealed record RevocationCoverageGapDto(Guid? EnvironmentId,Guid? NodeId,string Reason);
public sealed record RevocationAlertDto(Guid EnvironmentId,string Severity,int ThresholdSeconds,int PendingTargets,DateTimeOffset ObservedAt);
public sealed record RevocationOperationDto(Guid OperationId,Guid CredentialId,DateTimeOffset CommittedAt,long Revision,string Status,string PollUrl,IReadOnlyList<RevocationEnvironmentDto> Environments,IReadOnlyList<RevocationTargetDto> Targets,bool Replayed=false,DateTimeOffset? CommitConfirmedAt=null,IReadOnlyList<RevocationAlertDto>? Alerts=null,IReadOnlyList<RevocationCoverageGapDto>? CoverageGaps=null);

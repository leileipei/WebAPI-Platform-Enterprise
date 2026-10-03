namespace WebApi.Contracts.Gateway;
public sealed record RegisterNodeRequest(Guid EnvironmentId,string NodeName,Guid InstanceId,string AppVersion);
public sealed record RegisteredNode(Guid NodeId,Guid EnvironmentId,Guid InstanceId);
public sealed record NodeHeartbeat(Guid InstanceId,long ConfigVersion,long DeploymentSequence,string Status);
public sealed record NodeAck(Guid InstanceId,Guid ReleaseId,long ConfigVersion,long DeploymentSequence,string PayloadHash,DateTimeOffset AppliedAt,bool Success,string? ErrorCode);
public sealed record AckResult(string ReleaseState,bool Duplicate);

namespace WebApi.Contracts.Gateway;
public sealed record RegisterNodeRequest(Guid EnvironmentId,string NodeName,Guid InstanceId,string AppVersion,IReadOnlyList<string>? SupportedSnapshotSchemas=null);
public sealed record RegisteredNode(Guid NodeId,Guid EnvironmentId,Guid InstanceId);
public sealed record NodeHeartbeat(Guid InstanceId,long ConfigVersion,long DeploymentSequence,string Status,IReadOnlyList<string>? SupportedSnapshotSchemas=null);
public sealed record NodeAck(Guid InstanceId,Guid ReleaseId,long ConfigVersion,long DeploymentSequence,string PayloadHash,DateTimeOffset AppliedAt,bool Success,string? ErrorCode);
public sealed record AckResult(string ReleaseState,bool Duplicate);

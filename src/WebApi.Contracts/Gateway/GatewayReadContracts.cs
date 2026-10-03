using WebApi.Contracts.Runtime;
namespace WebApi.Contracts.Gateway;
public sealed record GatewayNodeDto(Guid Id,Guid EnvironmentId,string NodeName,string InstanceId,string AppVersion,bool Enabled,string Status,bool Offline,long CurrentConfigVersion,long CurrentDeploymentSequence,long? TargetConfigVersion,DateTimeOffset? LastHeartbeatAt,string ETag,long? DesiredConfigVersion,long DesiredDeploymentSequence);
public sealed record GatewayEventDto(long Id,string EventType,string Message,string Detail,DateTimeOffset CreatedAt);
public sealed record ManagementCredential(Guid Id,string AccessKey,string Status,DateTimeOffset ValidFrom,DateTimeOffset ExpiresAt);
public sealed record ManagementApplication(Guid Id,string Status,IReadOnlyList<ManagementCredential> Credentials,IReadOnlyList<RuntimePermission> Permissions);
public sealed record ManagementSnapshot(string SchemaVersion,Guid EnvironmentId,long ConfigVersion,DateTimeOffset GeneratedAt,IReadOnlyList<RuntimeRoute> Routes,IReadOnlyList<RuntimeCluster> Clusters,IReadOnlyList<RuntimePolicy> Policies,IReadOnlyList<ManagementApplication> Applications,string ExportType="ManagementViewNotRuntimePayload");
public sealed record SnapshotInfo(long ConfigVersion,string PayloadHash,long SizeBytes,DateTimeOffset CreatedAt,string Status,Guid? LatestReleaseId,long? LatestDeploymentSequence,string? LatestDeploymentState);
public sealed record SetNodeEnabledRequest(bool Enabled);

namespace WebApi.Contracts.Security;
public sealed record SecurityRevocationProof(Guid CredentialId,Guid OperationId,long Epoch,DateTimeOffset RevokedAt,string EventHash);
public sealed record SecurityCheckpointPayload(string Protocol,Guid EnvironmentId,Guid SecurityGeneration,long Epoch,string HeadHash,IReadOnlyList<SecurityRevocationProof> Revocations,bool RequireEnvironmentCredentials,string MinimumSnapshotSchema,DateTimeOffset IssuedAt,DateTimeOffset FreshUntil,string KeyId,bool Full=true,long? BaseEpoch=null);
public sealed record SignedSecurityCheckpoint(SecurityCheckpointPayload Payload,string Signature);
public sealed record SecurityNodeLeasePayload(Guid LeaseId,string Protocol,Guid EnvironmentId,Guid SecurityGeneration,long Epoch,string CheckpointHash,Guid NodeId,Guid InstanceId,string Audience,DateTimeOffset IssuedAt,DateTimeOffset FreshUntil,string KeyId,int LeaseSeconds=30,int MaximumClockSkewSeconds=2,bool AllowAuthenticatedTraffic=true,IReadOnlyList<SecurityAckChallenge>? AckChallenges=null);
public sealed record SignedSecurityNodeLease(SecurityNodeLeasePayload Payload,string Signature);
public sealed record SecurityNodeMaterial(byte[] Checkpoint,byte[] Lease);

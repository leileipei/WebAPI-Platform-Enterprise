namespace WebApi.Contracts.Security;
public sealed record SecurityAck(Guid OperationId,Guid NodeId,Guid InstanceId,Guid EnvironmentId,Guid SecurityGeneration,long Epoch,string CheckpointHash,DateTimeOffset AppliedAt,bool Success,bool Durable,bool AdmissionEnforced,Guid ChallengeNonce,string? ErrorCode,byte[] Checkpoint,byte[] Lease);
public sealed record SecurityAckFailure(Guid OperationId,Guid NodeId,Guid InstanceId,Guid EnvironmentId,Guid ChallengeNonce,string ErrorCode,long ObservationSequence);
public sealed record SecurityAckFailureResult(bool Duplicate);
// A node is authorized only for its own ACK; aggregate operation state is a
// separate management concern and may include other environments.
public sealed record SecurityAckResult(bool Duplicate);
public sealed record SecurityAckChallenge(Guid OperationId,Guid ChallengeNonce,long RequiredEpoch,Guid? CredentialId,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]string? EventType=null);

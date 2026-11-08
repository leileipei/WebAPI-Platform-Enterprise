namespace WebApi.Contracts.Security;
public sealed record CurrentSecurityProof(Guid InstanceId,byte[] Checkpoint,byte[] Lease,DateTimeOffset AppliedAt,bool Durable,bool AdmissionEnforced);

namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class EnvironmentSecurityState
{
 public Guid EnvironmentId {get;set;} public Guid OrganizationId {get;set;} public Guid ProjectId {get;set;}
 public Guid SecurityGeneration {get;set;}=Guid.NewGuid(); public long Epoch {get;set;}
 public string HeadHash {get;set;}=WebApi.Infrastructure.CredentialSecurity.SecurityEventHash.Genesis;
 public bool? InitialRequireEnvironmentCredentials {get;set;}
 public bool RequireEnvironmentCredentials {get;set;} public string MinimumSnapshotSchema {get;set;}="2.0";
 public int LeaseSeconds {get;set;}=30; public int LeaseRefreshSeconds {get;set;}=5; public int MaximumClockSkewSeconds {get;set;}=2;
 public long Revision {get;set;}=1; public DateTimeOffset UpdatedAt {get;set;}
}
public sealed class SecurityOperation
{
 public Guid Id {get;set;}=Guid.NewGuid(); public Guid OrganizationId {get;set;} public Guid? ApplicationId {get;set;}
 public Guid? CredentialId {get;set;} public string Kind {get;set;}="CredentialRevoke";
 public Guid RequestedBy {get;set;} public string Reason {get;set;}=""; public DateTimeOffset CommittedAt {get;set;} public long Revision {get;set;}=1;
}
public sealed class SecurityEvent
{
 public Guid EnvironmentId {get;set;} public Guid OrganizationId {get;set;} public Guid ProjectId {get;set;}
 public Guid SecurityGeneration {get;set;} public long Epoch {get;set;} public Guid OperationId {get;set;}
 public string EventType {get;set;}="CredentialRevoked"; public Guid? CredentialId {get;set;} public string? PolicyJson {get;set;}
 public string PreviousHash {get;set;}=""; public string EventHash {get;set;}=""; public DateTimeOffset OccurredAt {get;set;}
}
public sealed class CredentialRevocation
{
 public Guid EnvironmentId {get;set;} public Guid OrganizationId {get;set;} public Guid ProjectId {get;set;}
 public Guid CredentialId {get;set;} public Guid ApplicationId {get;set;}
 public Guid SecurityGeneration {get;set;} public long Epoch {get;set;} public Guid OperationId {get;set;} public DateTimeOffset RevokedAt {get;set;}
}
public sealed class SecurityOutboxMessage
{
 public Guid Id {get;set;}=Guid.NewGuid(); public Guid EnvironmentId {get;set;} public Guid SecurityGeneration {get;set;}
 public long Epoch {get;set;} public Guid OperationId {get;set;} public DateTimeOffset CreatedAt {get;set;}
 public DateTimeOffset? ProcessedAt {get;set;} public DateTimeOffset? LeaseUntil {get;set;} public int Attempts {get;set;}
}
public sealed class SecurityOperationTarget
{
 public Guid OperationId {get;set;} public Guid EnvironmentId {get;set;} public Guid NodeId {get;set;} public Guid InstanceGuid {get;set;}
 public Guid ChallengeNonce {get;set;}=Guid.NewGuid(); public DateTimeOffset CreatedAt {get;set;} public string TargetState {get;set;}="Pending";
 public bool ProtocolSupportedAtFreeze {get;set;} public Guid? SupersededByInstanceGuid {get;set;} public DateTimeOffset? LastLeaseUntil {get;set;}
}

public sealed class GatewaySecurityLease
{
 public Guid NodeId {get;set;} public Guid InstanceId {get;set;} public Guid EnvironmentId {get;set;} public Guid OrganizationId {get;set;} public Guid ProjectId {get;set;}
 public Guid SecurityGeneration {get;set;} public long Epoch {get;set;} public string CheckpointHash {get;set;}="";public Guid LeaseId {get;set;}
 public DateTimeOffset IssuedAt {get;set;} public DateTimeOffset FreshUntil {get;set;} public DateTimeOffset LastIssuedLeaseUntil {get;set;} public string SigningKeyId {get;set;}=""; public int LeaseSeconds {get;set;}=30;public long Revision {get;set;}=1;
}

// An append-only issuance fact for each signed lease. The mutable per-instance
// row above is only a current horizon and cannot prove which lease was issued.
public sealed class GatewaySecurityLeaseIssuance
{
 public Guid LeaseId {get;set;} public Guid NodeId {get;set;} public Guid InstanceId {get;set;} public Guid EnvironmentId {get;set;} public Guid OrganizationId {get;set;} public Guid ProjectId {get;set;}
 public Guid SecurityGeneration {get;set;} public long Epoch {get;set;} public string CheckpointHash {get;set;}=""; public string PayloadHash {get;set;}="";
 public DateTimeOffset IssuedAt {get;set;} public DateTimeOffset FreshUntil {get;set;} public string SigningKeyId {get;set;}="";
}

// Once a lease was observed expired it must not become current again after a
// database clock regression. This is an observation, not proof that the clock
// was trustworthy before the first observation or after a database restore.
public sealed class GatewaySecurityLeaseExpiryObservation
{
 public Guid LeaseId {get;set;} public DateTimeOffset ObservedAt {get;set;}
}

// A database-wide monotonic lower bound, not an external time attestation.
public sealed class SecurityDatabaseClockWatermark
{
 public bool Singleton {get;set;}=true; public DateTimeOffset ObservedAt {get;set;}
}

// The last full proof accepted by the control plane, independently of later issuance.
public sealed class GatewaySecurityCurrentReceipt
{
 public Guid NodeId {get;set;} public Guid InstanceId {get;set;} public Guid EnvironmentId {get;set;} public Guid OrganizationId {get;set;} public Guid ProjectId {get;set;}
 public Guid SecurityGeneration {get;set;} public long Epoch {get;set;} public string CheckpointHash {get;set;}="";public string HeadHash {get;set;}="";public Guid LeaseId {get;set;}
 public DateTimeOffset FreshUntil {get;set;} public DateTimeOffset ReceivedAt {get;set;}
}

public sealed class SecurityAckRecord
{
 public Guid OperationId {get;set;} public Guid EnvironmentId {get;set;} public Guid NodeId {get;set;} public Guid InstanceGuid {get;set;}
 public Guid SecurityGeneration {get;set;} public long Epoch {get;set;} public string CheckpointHash {get;set;}="";public Guid ChallengeNonce {get;set;}
 public DateTimeOffset AppliedAt {get;set;} public DateTimeOffset ReceivedAt {get;set;} public bool Success {get;set;}=true;public bool Durable {get;set;} public bool AdmissionEnforced {get;set;} public string? ErrorCode {get;set;}
}

// A successful ACK is authoritative only after its exact current-instance
// challenge and signed lease have been verified by this control plane.
// Older immutable ACK records remain intact when a node reapplies a challenge.
public sealed class SecurityAckVerification
{
 public Guid OperationId {get;set;} public Guid EnvironmentId {get;set;} public Guid NodeId {get;set;} public Guid InstanceGuid {get;set;} public Guid ChallengeNonce {get;set;}
 public Guid? RevalidationNonce {get;set;}
 public Guid SecurityGeneration {get;set;} public long Epoch {get;set;} public string CheckpointHash {get;set;}=""; public Guid LeaseId {get;set;}
 public DateTimeOffset AppliedAt {get;set;} public DateTimeOffset ReceivedAt {get;set;}
}

public sealed class SecurityAckRevalidationChallenge
{
 public Guid Nonce {get;set;} public Guid OperationId {get;set;} public Guid EnvironmentId {get;set;} public Guid NodeId {get;set;} public Guid InstanceGuid {get;set;}
 public DateTimeOffset CreatedAt {get;set;} public DateTimeOffset ExpiresAt {get;set;}
}
public sealed class SecurityAckAttempt
{
 public Guid Id {get;set;}=Guid.NewGuid(); public Guid OperationId {get;set;} public Guid EnvironmentId {get;set;} public Guid NodeId {get;set;} public Guid InstanceGuid {get;set;}
 public Guid ChallengeNonce {get;set;} public Guid? ReportedNonce {get;set;} public long? ObservationSequence {get;set;} public string RequestHash {get;set;}="";public string ErrorCode {get;set;}="";public DateTimeOffset ReceivedAt {get;set;}
}
public sealed class SecurityAckFailureLatest
{
 public Guid OperationId {get;set;} public Guid EnvironmentId {get;set;} public Guid NodeId {get;set;} public Guid InstanceGuid {get;set;}
 public Guid ReportedNonce {get;set;} public string ErrorCode {get;set;}=""; public DateTimeOffset LastAcceptedAt {get;set;} public long LatestSequence {get;set;} public long ObservationCount {get;set;}=1;
}
public sealed class SecurityOperationAlert
{
 public Guid OperationId {get;set;} public Guid EnvironmentId {get;set;} public int ThresholdSeconds {get;set;}
 public string Severity {get;set;}="Warning"; public string OperationStatus {get;set;}="Unconfirmed";
 public int PendingTargets {get;set;} public DateTimeOffset ObservedAt {get;set;}
}

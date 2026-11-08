using System.Text.Json.Serialization;
namespace WebApi.Contracts.Applications;
// Trusted identity is constructed only after credential verification; never from client identity headers.
public sealed record CredentialUseIdentity(Guid EnvironmentId,Guid NodeId,Guid InstanceId,Guid CredentialId,Guid ApplicationId,long ConfigVersion,long DeploymentSequence,string PayloadHash,Guid RouteId,string Method,Guid? AdmissionLeaseId=null,Guid? AdmissionSecurityGeneration=null,long? AdmissionSecurityEpoch=null);
public sealed record UsageCounters(long AuthenticatedCount,long AuthorizedCount,long RequestCount,long Status2xx,long Status3xx,long Status4xx,long Status5xx,long Status403,long Status429,long TransportFailures,long ClientAborts,long RetryAttempts,long CacheHits)
{
    public static UsageCounters Zero {get;}=new(0,0,0,0,0,0,0,0,0,0,0,0,0);
}
public sealed record CredentialUsageProof(long ConfigVersion,long DeploymentSequence,string PayloadHash,Guid RouteId,string Method,long AuthenticatedCount,DateTimeOffset LastAuthenticatedAt,long AuthorizedCount=0,long RequestCount=0,long SuccessfulCount=0,DateTimeOffset? LastSuccessfulAt=null,[property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Guid? AdmissionLeaseId=null);
public sealed record CredentialUsageBucketData(Guid EnvironmentId,Guid NodeId,Guid InstanceId,Guid CredentialId,Guid ApplicationId,DateTimeOffset BucketStart,long BatchRevision,UsageCounters Counters,DateTimeOffset LastUsedAt,string Coverage,int LostWindowSeconds,IReadOnlyList<CredentialUsageProof> Proofs);
public sealed record CredentialUsageEnvelope(CredentialUsageBucketData Bucket,string PayloadHash);
public sealed record CredentialUsageGap(Guid Id,Guid EnvironmentId,Guid NodeId,Guid InstanceId,DateTimeOffset Start,DateTimeOffset End,string Reason);
public sealed record UsageSourceHealth(Guid InstanceId,DateTimeOffset CoveredUntil,string State);
public sealed record UsageBatch(IReadOnlyList<CredentialUsageEnvelope> Buckets,IReadOnlyList<CredentialUsageGap> LostWindows,UsageSourceHealth? SourceHealth=null);
public sealed record UsageBucketReceipt(Guid InstanceId,Guid CredentialId,DateTimeOffset BucketStart,long BatchRevision,string PayloadHash,string Result);
public sealed record UsageBatchReceiptPayload(Guid EnvironmentId,Guid NodeId,string BatchHash,DateTimeOffset ReceivedAt,IReadOnlyList<UsageBucketReceipt> Buckets,IReadOnlyList<Guid> GapIds,string KeyId);
public sealed record UsageBatchReceipt(UsageBatchReceiptPayload Payload,string Signature);
public sealed record RequestOutcome(int? Status,bool Authorized,int Attempts,bool CacheHit,bool TransportFailure,bool ClientAborted);

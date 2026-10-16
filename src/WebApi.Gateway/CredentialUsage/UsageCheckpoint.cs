using WebApi.Contracts.CredentialUsage;
namespace WebApi.Gateway.CredentialUsage;
public sealed record UsageCheckpoint(Guid EnvironmentId,string NodeName,Guid CollectorId,long NextSequence,DateTimeOffset DurableThrough,IReadOnlyList<UsageObservation> Active,IReadOnlyList<UsageBatchCore> Pending,IReadOnlyList<UsageGap> Gaps);

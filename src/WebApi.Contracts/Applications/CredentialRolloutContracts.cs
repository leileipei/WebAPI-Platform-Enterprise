namespace WebApi.Contracts.Applications;
public sealed record CredentialRolloutDto(string ConfigVersion,string DeploymentSequence,string PayloadHash,IReadOnlyList<Guid> NodeIds,DateTimeOffset VerifiedAt,bool Current=true);

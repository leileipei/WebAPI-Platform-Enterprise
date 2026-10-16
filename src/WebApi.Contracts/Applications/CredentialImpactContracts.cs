namespace WebApi.Contracts.Applications;
public sealed record CredentialImpactRequest(DateTimeOffset ValidFrom,DateTimeOffset ExpiresAt,string Status);
public sealed record CredentialImpactConfirmation(string ImpactHash,bool ConfirmCloseAllAccess);
public sealed record CredentialImpactDto(string ImpactHash,long CredentialRevision,long ApplicationRevision,bool LastWorkingCredential,IReadOnlyList<Guid> AtRiskVisibleEnvironmentIds,bool PublishedImpactUnknown);

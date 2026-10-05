namespace WebApi.Contracts.Sso;
public sealed record SsoClaimMapping(string? DisplayName,string? Email);
public sealed record SaveSsoProviderRequest(Guid? OrganizationId,string Name,string Issuer,string ClientId,string SecretRef,IReadOnlyList<string> Scopes,SsoClaimMapping ClaimMapping);
public enum SsoProviderOperation { Enable,Disable,Default,Rotate }
public sealed record SsoTestStageDto(string Code,bool Passed,string Detail);
public sealed record SsoTestDto(Guid Id,long ProviderRevision,DateTimeOffset TestedAt,string Status,IReadOnlyList<SsoTestStageDto> Stages);
public sealed record SsoProviderDto(Guid Id,Guid? OrganizationId,string Name,string ProviderType,string Issuer,string ClientId,string SecretRef,IReadOnlyList<string> Scopes,SsoClaimMapping ClaimMapping,bool Enabled,bool IsDefault,long Revision,long AuthRevision,SsoTestDto? LastTest,string CallbackUrl);
public sealed record PublicSsoProviderDto(Guid Id,string Name,bool IsDefault);
public sealed record CreateSsoUserRequest(string Username,string DisplayName,string? Email,Guid ProviderId,string Subject);
public sealed record UpdateExternalIdentityRequest(Guid ProviderId,string Subject,bool Enabled);
public sealed record ExternalIdentityDto(Guid Id,Guid UserId,Guid ProviderId,string Issuer,string Subject,bool Enabled,long Revision);

namespace WebApi.Contracts.Applications;
public sealed record SaveApplicationRequest(Guid OrganizationId,Guid? ProjectId,string Code,string Name,string Owner,string Status="Active");
public sealed record ApplicationDto(Guid Id,Guid OrganizationId,Guid? ProjectId,string Code,string Name,string Owner,string Status,long Revision,string Activation="WorkingConfiguration",[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ApplicationSummaryDto? Summary=null);
public sealed record ApplicationDetailDto(ApplicationDto Application,IReadOnlyList<CredentialDto> Credentials,IReadOnlyList<ApplicationPermissionDto> Permissions,IReadOnlyDictionary<Guid,long?> RunningConfigVersions);
public sealed record CredentialCreateRequest(DateTimeOffset ValidFrom,DateTimeOffset ExpiresAt,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? EnvironmentId=null);
public sealed record CredentialUpdateRequest(DateTimeOffset ValidFrom,DateTimeOffset ExpiresAt,string Status,CredentialImpactConfirmation? ImpactConfirmation=null);
public sealed record CredentialDto(Guid Id,Guid ApplicationId,string AccessKey,string SecretLast4,string Status,DateTimeOffset ValidFrom,DateTimeOffset ExpiresAt,DateTimeOffset? LastUsedAt,DateTimeOffset? RevokedAt,long Revision,string Activation="WorkingConfiguration",
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? EnvironmentId=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? BindingMode=null);
public sealed record CredentialCreateResponse(CredentialDto Credential,string? Secret,string? ApiKey,bool SecretUnavailable=false);
public sealed record PermissionSaveRequest(Guid ApiId,Guid EnvironmentId,DateTimeOffset ValidFrom,DateTimeOffset? ExpiresAt=null);
public sealed record ApplicationPermissionDto(Guid Id,Guid ApplicationId,Guid ApiId,Guid EnvironmentId,DateTimeOffset ValidFrom,DateTimeOffset? ExpiresAt,long Revision,string Activation="WorkingConfiguration");

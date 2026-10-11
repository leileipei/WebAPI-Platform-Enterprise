using WebApi.Contracts.Security;
namespace WebApi.Contracts.Governance;
public sealed record SaveResourceRequest(string Code,string Name,string Status="Active");
public sealed record CreateEnvironmentRequest(string Code,string Name,bool IsProduction=false,int SortOrder=0,Guid? ReleasePolicyId=null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] OptionalJsonProperty<string> GatewayPublicUrl=default,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] OptionalJsonProperty<string> GatewayInternalUrl=default,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] OptionalJsonProperty<string> BasePath=default);
public sealed record UpdateEnvironmentRequest(string Code,string Name,string Status,bool IsProduction,int SortOrder,Guid? ReleasePolicyId,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] OptionalJsonProperty<string> GatewayPublicUrl=default,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] OptionalJsonProperty<string> GatewayInternalUrl=default,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] OptionalJsonProperty<string> BasePath=default);
public sealed record OrganizationDto(Guid Id,string Code,string Name,string Status,long Revision);
public sealed record ProjectDto(Guid Id,Guid OrganizationId,string Code,string Name,string Status,long Revision,Guid? OwnerUserId);
public sealed record EnvironmentDto(Guid Id,Guid ProjectId,string Code,string Name,string Status,bool IsProduction,int SortOrder,Guid? ReleasePolicyId,long? DesiredConfigVersion,long DeploymentSequence,long Revision,string? GatewayPublicUrl=null,string BasePath="/",long AccessAddressRevision=1);
public sealed record EnvironmentDetailDto(Guid Id,Guid ProjectId,string Code,string Name,string Status,bool IsProduction,int SortOrder,Guid? ReleasePolicyId,long? DesiredConfigVersion,long DeploymentSequence,long Revision,string? GatewayPublicUrl,string BasePath,long AccessAddressRevision,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? GatewayInternalUrl);
public sealed record ScopeTreeDto(IReadOnlyList<OrganizationDto> Organizations,IReadOnlyList<ProjectDto> Projects,IReadOnlyList<EnvironmentDto> Environments);
public sealed record CreateUserRequest(string Username,string DisplayName,string Password,string? Email=null);
public sealed record UpdateUserRequest(string DisplayName,string Status,string? Email=null);
public sealed record UserDto(Guid Id,string Username,string DisplayName,string? Email,string Status,string AuthSource,long Revision,IReadOnlyList<Guid> RoleIds);
public sealed record AssignRolesRequest(IReadOnlyList<Guid> RoleIds);
public sealed record SaveScopesRequest(IReadOnlyList<ScopeGrantDto> Scopes);
public sealed record SaveRoleRequest(string Code,string Name,Guid? OrganizationId=null);
public sealed record RoleDto(Guid Id,string Code,string Name,Guid? OrganizationId,bool IsSystem,long Revision,IReadOnlyList<string> Permissions);
public sealed record AssignPermissionsRequest(IReadOnlyList<string> Permissions);

public sealed record AuditDto(long Id,Guid? OrganizationId,Guid? ProjectId,Guid? EnvironmentId,Guid? UserId,string Action,string ResourceType,string ResourceId,string? BeforeJson,string? AfterJson,string? Ip,string? TraceId,DateTimeOffset CreatedAt);

public sealed record AuditPageDto(IReadOnlyList<AuditDto> Items,int Total,int Page,int PageSize,bool ExportAllowed);

namespace WebApi.Contracts.Security;
public sealed record ScopeRef(Guid OrganizationId, Guid? ProjectId = null, Guid? EnvironmentId = null);
public sealed record ActorContext(Guid UserId, string TraceId);
public sealed record ResourceRef(string Type, Guid Id, ScopeRef Scope);
public interface IAuthorizationService
{
    Task RequireAsync(ActorContext actor, string permission, ResourceRef resource, CancellationToken cancellationToken = default);
}
public sealed record LoginRequest(string Username, string Password);
public sealed record IdentityDto(Guid Id, string Username, string DisplayName, IReadOnlyList<string> Permissions, IReadOnlyList<ScopeGrantDto> Scopes);
public sealed record ScopeGrantDto(ScopeRef Scope, string AccessMode);

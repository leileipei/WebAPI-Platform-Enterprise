namespace WebApi.Infrastructure.Persistence.Entities;
public sealed class ApplicationRouteMethodGrant
{
    public Guid PermissionId {get;set;}
    public Guid EnvironmentId {get;set;}
    public Guid ApiId {get;set;}
    public Guid RouteId {get;set;}
    public long AuthorizationRevision {get;set;}
    public string Method {get;set;}=string.Empty;
}

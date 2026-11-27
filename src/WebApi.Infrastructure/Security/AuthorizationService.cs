using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Security;
public sealed class AuthorizationService(WebApiDbContext db) : IAuthorizationService
{
    public Task RequireAsync(ActorContext actor, string permission, ResourceRef resource, CancellationToken cancellationToken = default) =>
        RequireAsync(actor, permission, resource, allowArchivedEnvironment: false, cancellationToken);

    internal Task RequireArchivedCredentialRevocationAsync(ActorContext actor, ResourceRef resource, CancellationToken ct) =>
        RequireAsync(actor, "credential.revoke", resource, allowArchivedEnvironment: true, ct);

    internal IQueryable<Permission> FunctionalPermissions(ActorContext actor, ScopeRef scope) =>
        from membership in db.Set<UserRole>()
        join role in db.Set<Role>() on membership.RoleId equals role.Id
        join grant in db.Set<RolePermission>() on role.Id equals grant.RoleId
        join permission in db.Set<Permission>() on grant.PermissionId equals permission.Id
        where membership.UserId == actor.UserId && (role.OrganizationId == null || role.OrganizationId == scope.OrganizationId)
        select permission;

    internal static IQueryable<Guid> PlatformAdministratorUserIds(WebApiDbContext db) =>
        from membership in db.Set<UserRole>()
        join role in db.Set<Role>() on membership.RoleId equals role.Id
        where role.OrganizationId == null && role.IsSystem && role.Code == "PlatformAdmin"
        select membership.UserId;

    private async Task RequireAsync(ActorContext actor, string permission, ResourceRef resource, bool allowArchivedEnvironment,
        CancellationToken cancellationToken)
    {
        await new PasswordLifecycleGuard(db).RequireBusinessAccessAsync(actor,cancellationToken);
        var functional=await FunctionalPermissions(actor,resource.Scope).AnyAsync(p=>p.Code==permission,cancellationToken);
        if(!functional) throw new ApiException(403,"permission_denied","缺少操作权限。");
        if(resource.Scope.OrganizationId==Guid.Empty)
        {
            if(!await PlatformAdministratorUserIds(db).ContainsAsync(actor.UserId,cancellationToken))
                throw new ApiException(403,"platform_permission_required","需要平台管理权限。");
            return;
        }
        var grants=await db.Set<UserProjectScope>().Where(x=>x.UserId==actor.UserId && x.OrganizationId==resource.Scope.OrganizationId).ToListAsync(cancellationToken);
        var read=permission.EndsWith(".read",StringComparison.Ordinal);
        if(!grants.Any(x=>ScopeMatcher.Matches(new(x.OrganizationId,x.ProjectId,x.EnvironmentId),resource.Scope) && (read || ScopeMatcher.CanWrite(x.AccessMode))))
            throw new ApiException(403,"scope_denied","数据范围不允许此操作。");
        var governance=permission.StartsWith("organization.",StringComparison.Ordinal)||permission.StartsWith("project.",StringComparison.Ordinal)||permission.StartsWith("environment.",StringComparison.Ordinal);
        if(!governance && (!await db.Set<Organization>().AnyAsync(x=>x.Id==resource.Scope.OrganizationId && x.Status=="Active",cancellationToken) ||
            (resource.Scope.ProjectId is Guid project && !await db.Set<Project>().AnyAsync(x=>x.Id==project && x.OrganizationId==resource.Scope.OrganizationId && x.Status=="Active",cancellationToken)) ||
            (resource.Scope.EnvironmentId is Guid env && !await db.Set<EnvironmentRecord>().AnyAsync(x=>x.Id==env && x.ProjectId==resource.Scope.ProjectId &&
                (x.Status=="Active" || allowArchivedEnvironment&&x.Status=="Disabled"),cancellationToken))))
            throw new ApiException(409,"inactive_scope","组织、项目或环境已停用。");
    }
    public async Task<bool> CanAsync(ActorContext actor,string permission,ResourceRef resource,CancellationToken ct=default)
    {
        try {await RequireAsync(actor,permission,resource,ct);return true;} catch(ApiException e) when(e.Status is 403 or 409 && e.Code!="password_change_required") {return false;}
    }
}

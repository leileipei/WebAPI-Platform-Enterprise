using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Security;
public sealed class AuthorizationService(WebApiDbContext db) : IAuthorizationService
{
    public async Task RequireAsync(ActorContext actor, string permission, ResourceRef resource, CancellationToken cancellationToken = default)
    {
        if(!await db.Set<UserRecord>().AnyAsync(x=>x.Id==actor.UserId && x.Status=="Active",cancellationToken))
            throw new ApiException(401,"inactive_session","会话已失效。");
        var functional=await (from ur in db.Set<UserRole>() join r in db.Set<Role>() on ur.RoleId equals r.Id
            join rp in db.Set<RolePermission>() on r.Id equals rp.RoleId join p in db.Set<Permission>() on rp.PermissionId equals p.Id
            where ur.UserId==actor.UserId && p.Code==permission && (r.OrganizationId==null || r.OrganizationId==resource.Scope.OrganizationId) select p.Id).AnyAsync(cancellationToken);
        if(!functional) throw new ApiException(403,"permission_denied","缺少操作权限。");
        if(resource.Scope.OrganizationId==Guid.Empty)
        {
            if(!await (from ur in db.Set<UserRole>() join r in db.Set<Role>() on ur.RoleId equals r.Id where ur.UserId==actor.UserId && r.Code=="PlatformAdmin" && r.IsSystem && r.OrganizationId==null select r.Id).AnyAsync(cancellationToken))
                throw new ApiException(403,"platform_permission_required","需要平台管理权限。");
            return;
        }
        var grants=await db.Set<UserProjectScope>().Where(x=>x.UserId==actor.UserId && x.OrganizationId==resource.Scope.OrganizationId).ToListAsync(cancellationToken);
        var read=permission.EndsWith(".read",StringComparison.Ordinal);
        if(!grants.Any(x=>ScopeMatcher.Matches(new(x.OrganizationId,x.ProjectId,x.EnvironmentId),resource.Scope) && (read || ScopeMatcher.CanWrite(x.AccessMode))))
            throw new ApiException(403,"scope_denied","数据范围不允许此操作。");
        if(!await db.Set<Organization>().AnyAsync(x=>x.Id==resource.Scope.OrganizationId && x.Status=="Active",cancellationToken) ||
            (resource.Scope.ProjectId is Guid project && !await db.Set<Project>().AnyAsync(x=>x.Id==project && x.OrganizationId==resource.Scope.OrganizationId && x.Status=="Active",cancellationToken)) ||
            (resource.Scope.EnvironmentId is Guid env && !await db.Set<EnvironmentRecord>().AnyAsync(x=>x.Id==env && x.ProjectId==resource.Scope.ProjectId && x.Status=="Active",cancellationToken)))
            throw new ApiException(409,"inactive_scope","组织、项目或环境已停用。");
    }
    public async Task<bool> CanAsync(ActorContext actor,string permission,ResourceRef resource,CancellationToken ct=default)
    {
        try {await RequireAsync(actor,permission,resource,ct);return true;} catch(ApiException e) when(e.Status is 403 or 409) {return false;}
    }
}

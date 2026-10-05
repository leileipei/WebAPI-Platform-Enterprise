using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Governance;
public sealed record AuditAccessView(IQueryable<AuditLog> Query,string ScopeFingerprint);
public sealed class AuditAccessQuery(WebApiDbContext db,AuthorizationService auth)
{
    public async Task<IQueryable<AuditLog>> QueryAsync(ActorContext actor,string? traceId,string? resourceId,CancellationToken ct=default)=>(await AccessAsync(actor,traceId,resourceId,ct)).Query;
    public async Task<AuditAccessView> AccessAsync(ActorContext actor,string? traceId,string? resourceId,CancellationToken ct=default)
    {
        if(traceId?.Length>128||resourceId?.Length>128)throw new ApiException(422,"invalid_filter","筛选值过长。");
        var query=db.Set<AuditLog>().AsNoTracking();string fingerprint="platform";
        if(!await auth.CanAsync(actor,"audit.read",new("audit",Guid.Empty,new(Guid.Empty)),ct))
        {
            var grants=await db.Set<UserProjectScope>().AsNoTracking().Where(x=>x.UserId==actor.UserId).ToArrayAsync(ct);
            var rolePermissions=await (from ur in db.Set<UserRole>() join r in db.Set<Role>() on ur.RoleId equals r.Id join rp in db.Set<RolePermission>() on r.Id equals rp.RoleId join p in db.Set<Permission>() on rp.PermissionId equals p.Id where ur.UserId==actor.UserId&&p.Code=="audit.read" select r.OrganizationId).ToArrayAsync(ct);
            if(rolePermissions.Length==0)throw new ApiException(403,"permission_denied","缺少审计权限。");
            var allowed=grants.Where(g=>rolePermissions.Contains(null)||rolePermissions.Contains(g.OrganizationId)).Select(g=>new ScopeRef(g.OrganizationId,g.ProjectId,g.EnvironmentId)).Distinct().OrderBy(s=>s.OrganizationId).ThenBy(s=>s.ProjectId).ThenBy(s=>s.EnvironmentId).ToArray();
            fingerprint=System.Text.Json.JsonSerializer.Serialize(allowed,CanonicalJson.Options);
            IQueryable<AuditLog> filtered=query.Where(_=>false);foreach(var scope in allowed)filtered=filtered.Union(query.Where(x=>x.OrganizationId==scope.OrganizationId&&(scope.ProjectId==null||x.ProjectId==scope.ProjectId)&&(scope.EnvironmentId==null||x.EnvironmentId==scope.EnvironmentId)));query=filtered;
        }
        if(!string.IsNullOrEmpty(traceId))query=query.Where(x=>x.TraceId==traceId);if(!string.IsNullOrEmpty(resourceId))query=query.Where(x=>x.ResourceId==resourceId);return new(query,fingerprint);
    }
}

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
            // Permission moves can capture two environments in one immutable audit row.
            // Apply the union of authorized scopes to every captured permission, including
            // historical rows, before pagination/counting or application-audit filtering.
            var scopeJson=System.Text.Json.JsonSerializer.Serialize(allowed);
            query=db.Set<AuditLog>().FromSqlInterpolated($"""
                SELECT a.* FROM audit_logs a
                WHERE NOT EXISTS (
                    SELECT 1 FROM jsonb_path_query(
                        COALESCE(a.before_json,'[]'::jsonb) || COALESCE(a.after_json,'[]'::jsonb),
                        '$[*] ? (@.Type == "ApplicationApiPermission").Fields') AS captured(value)
                    WHERE NOT EXISTS (
                        SELECT 1 FROM environments e
                        JOIN projects p ON p.id=e.project_id
                        CROSS JOIN jsonb_to_recordset({scopeJson}::jsonb)
                            AS s("OrganizationId" uuid,"ProjectId" uuid,"EnvironmentId" uuid)
                        WHERE e.id::text=captured.value->>'EnvironmentId'
                            AND s."OrganizationId"=p.organization_id
                            AND (s."ProjectId" IS NULL OR s."ProjectId"=e.project_id)
                            AND (s."EnvironmentId" IS NULL OR s."EnvironmentId"=e.id)
                    )
                )
                """).AsNoTracking();
            IQueryable<AuditLog> filtered=query.Where(_=>false);foreach(var scope in allowed)filtered=filtered.Union(query.Where(x=>x.OrganizationId==scope.OrganizationId&&(scope.ProjectId==null||x.ProjectId==scope.ProjectId)&&(scope.EnvironmentId==null||x.EnvironmentId==scope.EnvironmentId)));query=filtered;
        }
        if(!string.IsNullOrEmpty(traceId))query=query.Where(x=>x.TraceId==traceId);if(!string.IsNullOrEmpty(resourceId))query=query.Where(x=>x.ResourceId==resourceId);return new(query,fingerprint);
    }
}

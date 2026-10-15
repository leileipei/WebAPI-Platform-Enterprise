using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Applications;

public sealed class ApplicationAuditQuery(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditAccessQuery access)
{
    public async Task<AuditPageDto> ReadAsync(Guid applicationId,int page,int pageSize,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.ApplicationAsync(applicationId,ct);await auth.RequireAsync(actor,"app.read",new("application",applicationId,scope),ct);
        var authorized=await access.QueryAsync(actor,null,null,ct);
        if(page<1||page>1000000||pageSize<1||pageSize>100)throw new ApiException(422,"invalid_paging","分页范围不合法。");
        var id=applicationId.ToString();
        var credential=JsonSerializer.Serialize(new[]{new{Type="ApplicationCredential",Fields=new{ApplicationId=applicationId}}});
        var permission=JsonSerializer.Serialize(new[]{new{Type="ApplicationApiPermission",Fields=new{ApplicationId=applicationId}}});
        // Associations and captured historical fields come from trusted server records. A query string
        // cannot replace this predicate, and the original per-environment audit scope remains applied.
        var linked=db.Set<AuditLog>().FromSqlInterpolated($"SELECT a.* FROM audit_logs a WHERE a.resource_type IN ('ApplicationRecord','ApplicationCredential','ApplicationApiPermission') AND ((a.resource_type='ApplicationRecord' AND a.resource_id={id}) OR EXISTS(SELECT 1 FROM application_credentials c WHERE c.application_id={applicationId} AND c.id::text=a.resource_id) OR EXISTS(SELECT 1 FROM application_api_permissions p WHERE p.application_id={applicationId} AND p.id::text=a.resource_id) OR a.before_json @> {credential}::jsonb OR a.after_json @> {credential}::jsonb OR a.before_json @> {permission}::jsonb OR a.after_json @> {permission}::jsonb)").AsNoTracking();
        var query=authorized.Where(x=>linked.Any(y=>y.Id==x.Id));var total=await query.CountAsync(ct);
        var rows=await query.OrderByDescending(x=>x.Id).Skip((page-1)*pageSize).Take(pageSize).Select(x=>new AuditDto(x.Id,x.OrganizationId,x.ProjectId,x.EnvironmentId,x.UserId,x.Action,x.ResourceType,x.ResourceId,x.BeforeJson,x.AfterJson,x.Ip==null?null:x.Ip.ToString(),x.TraceId,x.CreatedAt)).ToArrayAsync(ct);
        // This endpoint has no export action; the existing general audit export is unchanged.
        return new(rows,total,page,pageSize,false);
    }
}

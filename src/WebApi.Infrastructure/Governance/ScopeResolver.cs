using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Governance;
public sealed class ScopeResolver(WebApiDbContext db)
{
    public static ApiException Missing() => new(404,"resource_not_found","资源不存在或不可访问。");
    public async Task<ScopeRef> ProjectAsync(Guid id,CancellationToken ct=default)
    {var p=await db.Set<Project>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();return new(p.OrganizationId,p.Id);}
    public async Task<ScopeRef> EnvironmentAsync(Guid id,CancellationToken ct=default)
    {var e=await db.Set<EnvironmentRecord>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();var p=await ProjectAsync(e.ProjectId,ct);return p with {EnvironmentId=e.Id};}
    public async Task<ScopeRef> ApiAsync(Guid id,CancellationToken ct=default)
    {var a=await db.Set<Api>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();return new(a.OrganizationId,a.ProjectId);}
    public async Task<ScopeRef> VersionAsync(Guid id,CancellationToken ct=default)
    {var v=await db.Set<ApiVersion>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();return await ApiAsync(v.ApiId,ct);}
    public async Task<ScopeRef> ApplicationAsync(Guid id,CancellationToken ct=default)
    {var a=await db.Set<ApplicationRecord>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw Missing();return new(a.OrganizationId,a.ProjectId);}
}

using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Routing;
namespace WebApi.Infrastructure.Releases;
public sealed class ReleaseCandidateBuilder(WebApiDbContext db,ScopeResolver scopes,RouteService routes)
{
    public async Task<FrozenReleaseCandidate> BuildAsync(Guid environmentId,CreateReleaseRequest request,CancellationToken ct)
    {
        var candidate=await PreviewAsync(environmentId,new(request.BaseConfigVersion,request.VersionIds),ct);
        RequireRevisions(request.ResourceRevisions,candidate.ResourceRevisions);return candidate;
    }
    public static void RequireRevisions(IReadOnlyList<ResourceRevision> expected,IReadOnlyList<ResourceRevision> actual)
    {
        if(expected.Count>50000||expected.Select(r=>(r.Type,r.Id)).Distinct().Count()!=expected.Count) throw new ApiException(422,"invalid_revisions","资源修订重复或超过限制。");
        foreach(var revision in expected) {var current=actual.SingleOrDefault(r=>r.Type==revision.Type&&r.Id==revision.Id);if(current is not null&&current.Revision!=revision.Revision) throw new ApiException(412,"stale_revision","候选资源已更新，请重新预览。");}
        if(expected.Any(e=>!actual.Any(a=>a.Type==e.Type&&a.Id==e.Id))) throw new ApiException(422,"foreign_revision","revision对象不属于候选资源。");
        if(actual.Any(a=>!expected.Any(e=>e.Type==a.Type&&e.Id==a.Id))) throw new ApiException(422,"missing_revision","必须提供预览中的全部资源修订。");
    }
    public static bool RevisionsCurrent(IReadOnlyList<ResourceRevision> expected,IReadOnlyList<ResourceRevision> actual)
    {try {RequireRevisions(expected,actual);return true;}catch(ApiException e) when(e.Status is 412 or 422) {return false;}}
    public async Task<FrozenReleaseCandidate> PreviewAsync(Guid environmentId,PreviewReleaseRequest request,CancellationToken ct)
    {
        var scope=await scopes.EnvironmentAsync(environmentId,ct);var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==environmentId,ct);
        if(request.BaseConfigVersion!=(env.DesiredConfigVersion??0)) throw new ApiException(409,"stale_baseline","环境基准版本已变化。");
        if(request.VersionIds.Count is <1 or >100||request.VersionIds.Distinct().Count()!=request.VersionIds.Count) throw new ApiException(422,"invalid_selection","请为每个API选择一个版本。");
        var versions=new List<FrozenApiVersion>();var revisions=new List<ResourceRevision>();var apiIds=new HashSet<Guid>();
        foreach(var id in request.VersionIds.OrderBy(x=>x))
        {
            var v=await db.Set<ApiVersion>().AsNoTracking().SingleOrDefaultAsync(v=>v.Id==id,ct)??throw new ApiException(422,"invalid_version","所选版本不存在。");var a=await db.Set<Api>().AsNoTracking().SingleAsync(a=>a.Id==v.ApiId,ct);
            if(a.ProjectId!=scope.ProjectId||a.OrganizationId!=scope.OrganizationId||!apiIds.Add(a.Id)||v.Status=="Retired") throw new ApiException(422,"foreign_version","版本跨范围、重复选择同一API或已停用。");
            var parameters=await db.Set<ApiParameter>().AsNoTracking().Where(p=>p.ApiVersionId==id).Select(p=>new ParameterDto(p.Id,p.ApiVersionId,p.Location,p.Name,p.DataType,p.Required,p.Schema,p.Description,p.ExampleJson)).ToArrayAsync(ct);
            var schemas=await db.Set<ApiSchema>().AsNoTracking().Where(s=>s.ApiVersionId==id).Select(s=>new SchemaDto(s.Id,s.ApiVersionId,s.SchemaType,s.Name,s.StatusCode,s.ContentType,s.SchemaJson,s.SchemaHash,s.ExampleJson)).ToArrayAsync(ct);
            versions.Add(new(CatalogService.Dto(a),CatalogService.Dto(v),parameters,schemas));revisions.Add(new("version",id,v.Revision));revisions.Add(new("api",a.Id,a.VersionNo));
        }
        var routeEntities=await db.Set<ApiRoute>().AsNoTracking().Where(r=>r.EnvironmentId==environmentId&&request.VersionIds.Contains(r.ApiVersionId)).OrderBy(r=>r.Id).ToArrayAsync(ct);var routeDtos=new List<RouteDto>();
        foreach(var r in routeEntities) {routeDtos.Add(RouteService.Dto(r,await routes.RequiresKeyAsync(r.Id,ct)));revisions.Add(new("route",r.Id,r.Revision));}
        var clusterIds=routeEntities.Select(r=>r.ClusterId).Distinct().ToArray();var clusters=new List<ClusterDto>();
        foreach(var c in await db.Set<UpstreamCluster>().AsNoTracking().Where(c=>clusterIds.Contains(c.Id)).OrderBy(c=>c.Id).ToArrayAsync(ct))
        {if(c.EnvironmentId!=environmentId||c.ProjectId!=scope.ProjectId) throw new ApiException(422,"foreign_cluster","候选Cluster跨环境。");var destinations=await db.Set<UpstreamDestination>().AsNoTracking().Where(d=>d.ClusterId==c.Id).OrderBy(d=>d.Id).ToArrayAsync(ct);clusters.Add(new(c.Id,c.ProjectId,c.EnvironmentId,c.Name,c.LoadBalancingPolicy,c.HealthCheckEnabled,c.HealthCheckPath,c.HealthCheckIntervalSec,c.Status,c.Revision,destinations.Select(ClusterService.Dto).ToArray()));revisions.Add(new("cluster",c.Id,c.Revision));revisions.AddRange(destinations.Select(d=>new ResourceRevision("destination",d.Id,d.Revision)));}
        var routeIds=routeEntities.Select(r=>r.Id).ToArray();var bindings=await db.Set<RoutePolicyBinding>().AsNoTracking().Where(b=>routeIds.Contains(b.RouteId)).Select(b=>new FrozenBinding(b.RouteId,b.PolicyId,b.Priority)).ToArrayAsync(ct);var policyIds=bindings.Select(b=>b.PolicyId).ToArray();var policies=await db.Set<Policy>().AsNoTracking().Where(p=>policyIds.Contains(p.Id)).ToArrayAsync(ct);
        if(policies.Any(p=>p.OrganizationId!=scope.OrganizationId||p.ProjectId is Guid project&&project!=scope.ProjectId)) throw new ApiException(422,"foreign_policy","候选Policy跨范围。");revisions.AddRange(policies.Select(p=>new ResourceRevision("policy",p.Id,p.VersionNo)));
        var grants=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(p=>p.EnvironmentId==environmentId).OrderBy(p=>p.Id).ToArrayAsync(ct);var applications=new List<FrozenApplication>();
        foreach(var appId in grants.Select(g=>g.ApplicationId).Distinct().OrderBy(x=>x))
        {
            var app=await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync(a=>a.Id==appId,ct);if(app.OrganizationId!=scope.OrganizationId||app.ProjectId is Guid p&&p!=scope.ProjectId) throw new ApiException(422,"foreign_application","候选应用跨范围。");
            var credentials=await db.Set<ApplicationCredential>().AsNoTracking().Where(c=>c.ApplicationId==appId).OrderBy(c=>c.Id).ToArrayAsync(ct);var permissions=grants.Where(g=>g.ApplicationId==appId).ToArray();foreach(var g in permissions) if((await scopes.ApiAsync(g.ApiId,ct)).ProjectId!=scope.ProjectId) throw new ApiException(422,"foreign_authorization","候选授权跨项目。");
            applications.Add(new(ApplicationService.Dto(app),credentials.Select(c=>new FrozenCredential(c.Id,c.AccessKey,c.SecretHash,c.SecretLast4,c.Status,c.ValidFrom,c.ExpiresAt,c.Revision)).ToArray(),permissions.Select(ApplicationService.Dto).ToArray()));revisions.Add(new("application",appId,app.Revision));revisions.AddRange(credentials.Select(c=>new ResourceRevision("credential",c.Id,c.Revision)));revisions.AddRange(permissions.Select(g=>new ResourceRevision("authorization",g.Id,g.Revision)));
        }
        return new(environmentId,scope.OrganizationId,scope.ProjectId!.Value,request.BaseConfigVersion,request.VersionIds,versions,routeDtos,clusters,policies.Select(p=>new FrozenPolicy(p.Id,p.Type,p.Config,p.Enabled,p.VersionNo)).ToArray(),bindings,applications,revisions);
    }
}

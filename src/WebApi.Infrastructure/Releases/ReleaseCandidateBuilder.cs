using WebApi.Infrastructure.Comparisons;
using System.Text.Json;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Gateway;
using WebApi.Domain.Runtime;
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
public sealed class ReleaseCandidateBuilder(WebApiDbContext db,ScopeResolver scopes,RouteService routes,VersionRiskReviewService reviews,JwtApplicationBindingService mappings,GatewayPolicyDeploymentRules deployment,SnapshotCompiler compiler,PublishSettings publishSettings)
{
    public async Task<FrozenReleaseCandidate> BuildAsync(Guid environmentId,CreateReleaseRequest request,CancellationToken ct,ActorContext? actor=null)
    {
        var candidate=await PreviewAsync(environmentId,new(request.BaseConfigVersion,request.VersionIds),ct);
        RequireRevisions(request.ResourceRevisions,candidate.ResourceRevisions);
        if(request.RiskReviewIds is {Count:>0}){if(actor is null)throw new ApiException(401,"review_actor_required","评审引用需要当前用户身份。");candidate=candidate with {RiskReviewReferences=await reviews.ResolveReferencesAsync(new(candidate.OrganizationId,candidate.ProjectId,candidate.EnvironmentId),candidate.VersionIds,request.RiskReviewIds,actor,ct)};}return candidate;
    }
    public async Task<FrozenReleaseCandidate> BuildPromotionAsync(Guid environmentId,PreviewReleaseRequest request,IReadOnlyList<Guid> routeIds,WebApi.Infrastructure.Delivery.PromotionCredentialResult selection,CancellationToken ct)
    {
        var candidate=await PreviewCoreAsync(environmentId,request,ct,routeIds,selection);
        if(routeIds.Count==0||routeIds.Distinct().Count()!=routeIds.Count||routeIds.Any(id=>!candidate.Routes.Any(r=>r.Id==id)))throw new ApiException(422,"invalid_promotion_routes","晋级目标路由准备不完整。");
        var routes=candidate.Routes.Where(r=>routeIds.Contains(r.Id)).ToArray();var bindings=candidate.Bindings.Where(b=>routeIds.Contains(b.RouteId)).ToArray();var policyIds=bindings.Select(b=>b.PolicyId).ToHashSet();var clusterIds=routes.Select(r=>r.ClusterId).ToHashSet();
        var revisions=candidate.ResourceRevisions.Where(r=>r.Type is not("application" or "credential" or "authorization")&&(r.Type!="route"||routeIds.Contains(r.Id))&&(r.Type!="policy"||policyIds.Contains(r.Id))&&(r.Type!="cluster"||clusterIds.Contains(r.Id))&&(r.Type!="destination"||candidate.Clusters.Where(c=>clusterIds.Contains(c.Id)).Any(c=>c.Destinations.Any(d=>d.Id==r.Id)))).Concat(selection.ResourceRevisions).DistinctBy(r=>(r.Type,r.Id)).OrderBy(r=>r.Type,StringComparer.Ordinal).ThenBy(r=>r.Id).ToArray();
        return candidate with{Routes=routes,Bindings=bindings,Policies=candidate.Policies.Where(p=>policyIds.Contains(p.Id)).ToArray(),Clusters=candidate.Clusters.Where(c=>clusterIds.Contains(c.Id)).ToArray(),Applications=selection.Applications,ResourceRevisions=revisions,SharedCredentialImpact=selection.Impact};
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
        =>await PreviewCoreAsync(environmentId,request,ct);
    private async Task<FrozenReleaseCandidate> PreviewCoreAsync(Guid environmentId,PreviewReleaseRequest request,CancellationToken ct,IReadOnlyList<Guid>? selectedRoutes=null,WebApi.Infrastructure.Delivery.PromotionCredentialResult? selection=null)
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
        var routeEntities=await db.Set<ApiRoute>().AsNoTracking().Where(r=>r.EnvironmentId==environmentId&&request.VersionIds.Contains(r.ApiVersionId)&&(selectedRoutes==null||selectedRoutes.Contains(r.Id))).OrderBy(r=>r.Id).ToArrayAsync(ct);var routeDtos=new List<RouteDto>();
        foreach(var r in routeEntities) {var effective=await routes.EffectiveAsync(r.Id,ct);routeDtos.Add(RouteService.Dto(r,effective.RequireApiKey) with {EffectiveAuthenticationMode=effective.Authentication?.Mode==AuthenticationMode.JWT?"JWT":null});revisions.Add(new("route",r.Id,r.Revision));}
        var clusterIds=routeEntities.Select(r=>r.ClusterId).Distinct().ToArray();var clusters=new List<ClusterDto>();
        foreach(var c in await db.Set<UpstreamCluster>().AsNoTracking().Where(c=>clusterIds.Contains(c.Id)).OrderBy(c=>c.Id).ToArrayAsync(ct))
        {if(c.EnvironmentId!=environmentId||c.ProjectId!=scope.ProjectId) throw new ApiException(422,"foreign_cluster","候选Cluster跨环境。");var destinations=await db.Set<UpstreamDestination>().AsNoTracking().Where(d=>d.ClusterId==c.Id).OrderBy(d=>d.Id).ToArrayAsync(ct);clusters.Add(new(c.Id,c.ProjectId,c.EnvironmentId,c.Name,c.LoadBalancingPolicy,c.HealthCheckEnabled,c.HealthCheckPath,c.HealthCheckIntervalSec,c.Status,c.Revision,destinations.Select(ClusterService.Dto).ToArray()));revisions.Add(new("cluster",c.Id,c.Revision));revisions.AddRange(destinations.Select(d=>new ResourceRevision("destination",d.Id,d.Revision)));}
        var routeIds=routeEntities.Select(r=>r.Id).ToArray();var bindings=await db.Set<RoutePolicyBinding>().AsNoTracking().Where(b=>routeIds.Contains(b.RouteId)).Select(b=>new FrozenBinding(b.RouteId,b.PolicyId,b.Priority)).ToArrayAsync(ct);var policyIds=bindings.Select(b=>b.PolicyId).ToArray();var policies=await db.Set<Policy>().AsNoTracking().Where(p=>policyIds.Contains(p.Id)).ToArrayAsync(ct);
        if(policies.Any(p=>p.OrganizationId!=scope.OrganizationId||p.ProjectId is Guid project&&project!=scope.ProjectId)) throw new ApiException(422,"foreign_policy","候选Policy跨范围。");revisions.AddRange(policies.Select(p=>new ResourceRevision("policy",p.Id,p.VersionNo)));
        var mappedIds=new HashSet<Guid>();
        foreach(var policy in policies)
        {
            deployment.Validate(policy.Type,policy.Config);
            if(policy.Type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(policy.Config).Jwt is {} jwt)
            {
                await mappings.ValidateAsync(new(policy.OrganizationId,policy.ProjectId),jwt,null,ct);
                mappedIds.UnionWith(jwt.ApplicationMappings.Select(m=>m.ApplicationId));
            }
        }
        RuntimeSnapshot? baseline=null;
        if(request.BaseConfigVersion>0)
        {
            var bytes=await (from v in db.Set<GatewayConfigVersion>().AsNoTracking() join s in db.Set<GatewayConfigSnapshot>() on v.Id equals s.ConfigVersionId where v.EnvironmentId==environmentId&&v.VersionNo==request.BaseConfigVersion select s.PayloadBytes).SingleOrDefaultAsync(ct);
            try { baseline=SnapshotValidator.ParsePayload(bytes??throw new ApiException(409,"baseline_unavailable","环境基准快照不可用。"),environmentId); }
            catch(JsonException) { throw new ApiException(409,"baseline_unavailable","环境基准快照不可用。"); }
            var retainedPolicyIds=baseline.Routes.Where(r=>!apiIds.Contains(r.ApiId)).SelectMany(r=>r.PolicyBindings??[]).Select(b=>b.PolicyId).ToHashSet();
            foreach(var policy in baseline.Policies.Where(p=>retainedPolicyIds.Contains(p.Id)))
                if(policy.Type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(policy.Config).Jwt is {} jwt)
                {
                    deployment.Validate(policy.Type,policy.Config);
                    await mappings.ValidateAsync(scope,jwt,null,ct);
                    mappedIds.UnionWith(jwt.ApplicationMappings.Select(m=>m.ApplicationId));
                }
        }
        var grants=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(p=>p.EnvironmentId==environmentId).OrderBy(p=>p.Id).ToArrayAsync(ct);var applications=new List<FrozenApplication>();
        if(selection is not null){applications.AddRange(selection.Applications);revisions.AddRange(selection.ResourceRevisions);if(mappedIds.Except(selection.Applications.Select(a=>a.Application.Id)).Any())throw new ApiException(422,"jwt_application_not_selected","目标JWT映射应用须明确选择或由保留业务基线提供。");}
        else foreach(var appId in grants.Select(g=>g.ApplicationId).Concat(mappedIds).Distinct().OrderBy(x=>x))
        {
            var app=await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync(a=>a.Id==appId,ct);if(app.OrganizationId!=scope.OrganizationId||app.ProjectId is Guid p&&p!=scope.ProjectId) throw new ApiException(422,"foreign_application","候选应用跨范围。");
            var credentials=await db.Set<ApplicationCredential>().AsNoTracking().Where(c=>c.ApplicationId==appId).OrderBy(c=>c.Id).ToArrayAsync(ct);var permissions=grants.Where(g=>g.ApplicationId==appId).ToArray();foreach(var g in permissions) if((await scopes.ApiAsync(g.ApiId,ct)).ProjectId!=scope.ProjectId) throw new ApiException(422,"foreign_authorization","候选授权跨项目。");
            applications.Add(new(ApplicationService.Dto(app),credentials.Select(c=>new FrozenCredential(c.Id,c.AccessKey,c.SecretHash,c.SecretLast4,c.Status,c.ValidFrom,c.ExpiresAt,c.Revision)).ToArray(),permissions.Select(ApplicationService.Dto).ToArray()));revisions.Add(new("application",appId,app.Revision));revisions.AddRange(credentials.Select(c=>new ResourceRevision("credential",c.Id,c.Revision)));revisions.AddRange(permissions.Select(g=>new ResourceRevision("authorization",g.Id,g.Revision)));
        }
        var candidate=new FrozenReleaseCandidate(environmentId,scope.OrganizationId,scope.ProjectId!.Value,request.BaseConfigVersion,request.VersionIds,versions,routeDtos,clusters,policies.Select(p=>new FrozenPolicy(p.Id,p.Type,p.Config,p.Enabled,p.VersionNo)).ToArray(),bindings,applications,revisions);
        var newSemantics=policies.Any(p=>p.Enabled&&routeEntities.Any(r=>r.Enabled&&bindings.Any(b=>b.RouteId==r.Id&&b.PolicyId==p.Id))&&Is22(p.Type,p.Config))
            || baseline is not null&&baseline.Routes.Where(r=>!apiIds.Contains(r.ApiId)).Any(r=>r.AuthenticationMode==AuthenticationMode.JWT||(r.PolicyBindings??[]).Any(b=>baseline.Policies.Any(p=>p.Id==b.PolicyId&&Is22(p.Type,p.Config))));
        if(newSemantics)
        {
            var compiled=compiler.Compile(candidate,baseline??new RuntimeSnapshot("2.0",environmentId,0,DateTimeOffset.UtcNow,[],[],[],[]),request.BaseConfigVersion+1,DateTimeOffset.UtcNow);
            using var document=JsonDocument.Parse(compiled.Payload);
            if(document.RootElement.GetProperty("schemaVersion").GetString()=="2.2") await SnapshotSchemaCapabilities.RequireOnline22Async(db,publishSettings,environmentId,ct);
        }
        return candidate;
    }
    private static bool Is22(string type,string config)=>type is "retry" or "cache"||type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(config).Mode==AuthenticationMode.JWT;
}

using WebApi.Infrastructure.Settings;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Security;
using WebApi.Domain.Routing;
using WebApi.Domain.Policies;
using WebApi.Contracts.Policies;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Routing;
public sealed class RouteService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,SystemSettingsReader settings)
{
    private static readonly string[] methods=["GET","POST","PUT","PATCH","DELETE","HEAD","OPTIONS"];
    public static RouteDto Dto(ApiRoute r,bool key=true)=>new(r.Id,r.ApiVersionId,r.EnvironmentId,r.RouteName,r.Path,r.NormalizedPath,r.Methods,r.ClusterId,r.Priority,RouteNormalizer.MatchOrder(r.Path,r.Priority),r.Enabled,r.TimeoutMs,key,r.Revision);
    public async Task<EffectiveRoutePolicies> EffectiveAsync(Guid routeId,CancellationToken ct)=>PolicyBindingRules.Validate(await RoutePolicyService.LoadAsync(db,routeId,ct),true);
    public async Task<bool> RequiresKeyAsync(Guid routeId,CancellationToken ct)=>(await EffectiveAsync(routeId,ct)).RequireApiKey;
    private async Task<RouteDto> EffectiveDtoAsync(ApiRoute route,CancellationToken ct)
    {var effective=await EffectiveAsync(route.Id,ct);return Dto(route,effective.RequireApiKey) with {EffectiveTimeoutMs=effective.TimeoutMs??route.TimeoutMs};}
    public async Task<PageResult<RouteDto>> ListAsync(Guid environmentId,ActorContext actor,int page,int size,CancellationToken ct=default)
    {var scope=await scopes.EnvironmentAsync(environmentId,ct);if(!await auth.CanAsync(actor,"route.read",new("environment",environmentId,scope),ct)) throw ScopeResolver.Missing();var routes=await db.Set<ApiRoute>().AsNoTracking().Where(r=>r.EnvironmentId==environmentId).ToArrayAsync(ct);var result=new List<RouteDto>();foreach(var r in routes) result.Add(await EffectiveDtoAsync(r,ct));return Pagination.Slice(result.OrderBy(r=>r.MatchOrder).ThenBy(r=>r.Path).ToArray(),page,size);}
    public async Task<RouteDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {var route=await db.Set<ApiRoute>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(route.EnvironmentId,ct);if(!await auth.CanAsync(actor,"route.read",new("route",id,scope),ct)) throw ScopeResolver.Missing();return await EffectiveDtoAsync(route,ct);}
    public async Task<CommandResult<RouteDto>> SaveAsync(Guid environmentId,SaveRouteRequest request,string? ifMatch,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.EnvironmentAsync(environmentId,ct);return await commands.ExecuteAsync(actor,scope,"route.save",async(_,token)=>{
            await auth.RequireAsync(actor,"route.write",new("environment",environmentId,scope),token);

            if(request.Id is not null&&request.TimeoutMs is null)throw new ApiException(422,"route_timeout_required","编辑路由必须提交基础超时。");
            var timeout=request.TimeoutMs??(await settings.GatewayAsync(token)).DefaultRouteTimeoutMs;
            var normalized=RouteNormalizer.Normalize(request.Path);RouteNormalizer.MatchOrder(request.Path,request.Priority);
            if(string.IsNullOrWhiteSpace(request.RouteName)||request.RouteName.Length>128||request.TimeoutMs is <1 or >300000||request.Methods is null||request.Methods.Count is <1 or >7||request.Methods.Any(m=>m is null||!methods.Contains(m.ToUpperInvariant()))) throw new ApiException(422,"invalid_route","路由名称、方法集合或超时不合法（最多300秒）。");
            var verbs=request.Methods.Select(m=>m.ToUpperInvariant()).Distinct().OrderBy(m=>m,StringComparer.Ordinal).ToArray();
            var versionScope=await scopes.VersionAsync(request.ApiVersionId,token);var version=await db.Set<ApiVersion>().AsNoTracking().SingleAsync(v=>v.Id==request.ApiVersionId,token);
            if(versionScope.ProjectId!=scope.ProjectId||versionScope.OrganizationId!=scope.OrganizationId||version.Status=="Retired") throw new ApiException(422,"foreign_version","版本不属于环境所在项目或已停用。");
            if(!await db.Set<UpstreamCluster>().AnyAsync(c=>c.Id==request.ClusterId&&c.EnvironmentId==environmentId&&c.ProjectId==scope.ProjectId&&c.Status=="Active",token)) throw new ApiException(422,"foreign_cluster","后端集群不属于当前环境或已停用。");
            var r=request.Id is Guid id?await db.Set<ApiRoute>().SingleOrDefaultAsync(r=>r.Id==id&&r.EnvironmentId==environmentId,token)??throw ScopeResolver.Missing():new ApiRoute {EnvironmentId=environmentId};
            if(request.Id is not null) RevisionTag.Require(ifMatch,r.Revision);
            var oldBindings=request.Id is null?Array.Empty<PolicyBindingConfiguration>():await RoutePolicyService.LoadAsync(db,r.Id,token);
            var authChanged=request.RequireApiKey!=PolicyBindingRules.Validate(oldBindings,true).RequireApiKey;
            if(authChanged) await auth.RequireAsync(actor,"policy.write",new("environment",environmentId,scope),token);
            if(request.Enabled&&await db.Set<RouteMethod>().AnyAsync(m=>m.EnvironmentId==environmentId&&m.NormalizedPath==normalized&&verbs.Contains(m.Method)&&m.RouteId!=r.Id,token)) throw new ApiException(409,"route_conflict","同一环境、方法和同形路径已有启用路由。");
            if(request.Id is null) db.Add(r);else r.Revision++;
            r.ApiVersionId=request.ApiVersionId;r.RouteName=request.RouteName;r.Path=request.Path=="/"?"/":request.Path.TrimEnd('/');r.NormalizedPath=normalized;r.Methods=verbs;r.ClusterId=request.ClusterId;r.Priority=request.Priority;r.Enabled=request.Enabled;r.TimeoutMs=timeout;r.UpdatedAt=DateTimeOffset.UtcNow;
            var previous=await db.Set<RouteMethod>().Where(m=>m.RouteId==r.Id).ToArrayAsync(token);var desired=request.Enabled?verbs:Array.Empty<string>();
            db.RemoveRange(previous.Where(m=>!desired.Contains(m.Method)));foreach(var item in previous.Where(m=>desired.Contains(m.Method))) {item.EnvironmentId=environmentId;item.NormalizedPath=normalized;}
            foreach(var verb in desired.Where(v=>!previous.Any(m=>m.Method==v))) db.Add(new RouteMethod {RouteId=r.Id,EnvironmentId=environmentId,Method=verb,NormalizedPath=normalized});
            var updatedBindings=oldBindings.ToList();
            if(authChanged) {
                var existingAuthIds=oldBindings.Where(b=>b.Type=="authentication").Select(b=>b.PolicyId).ToArray();
                db.RemoveRange(await db.Set<RoutePolicyBinding>().Where(b=>b.RouteId==r.Id&&existingAuthIds.Contains(b.PolicyId)).ToArrayAsync(token));
                var privatePolicy=new Policy {OrganizationId=scope.OrganizationId,ProjectId=scope.ProjectId,Name="RouteAuth-"+r.Id+"-"+Guid.NewGuid().ToString("N"),Type="authentication",Config=PolicyConfigurationValidator.Normalize("authentication",JsonSerializer.Serialize(new {mode=request.RequireApiKey?"ApiKey":"Anonymous"}))};
                db.Add(privatePolicy);db.Add(new RoutePolicyBinding {RouteId=r.Id,PolicyId=privatePolicy.Id});
                updatedBindings.RemoveAll(b=>b.Type=="authentication");updatedBindings.Add(new(privatePolicy.Id,privatePolicy.Type,privatePolicy.Config,true,0));
            }
            var effective=PolicyBindingRules.Validate(updatedBindings,true);
            return new CommandResult<RouteDto>(Dto(r,effective.RequireApiKey) with {EffectiveTimeoutMs=effective.TimeoutMs??r.TimeoutMs},RevisionTag.Format(r.Revision));
        },ct);
    }
    public async Task DeleteAsync(Guid id,string? tag,ActorContext actor,CancellationToken ct=default)
    {
        var original=await db.Set<ApiRoute>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(original.EnvironmentId,ct);
        await commands.ExecuteAsync(actor,scope,"route.delete",async(_,token)=>{await auth.RequireAsync(actor,"route.write",new("route",id,scope),token);var r=await db.Set<ApiRoute>().SingleAsync(r=>r.Id==id,token);RevisionTag.Require(tag,r.Revision);db.RemoveRange(await db.Set<RouteMethod>().Where(m=>m.RouteId==id).ToArrayAsync(token));db.RemoveRange(await db.Set<RoutePolicyBinding>().Where(b=>b.RouteId==id).ToArrayAsync(token));db.Remove(r);return true;},ct);
    }
}

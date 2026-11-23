using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Routing;
public sealed class ClusterService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,UpstreamAddressPolicy addresses,ClusterHealthReadService health,IdempotentCommandExecutor idempotency,CommandRequestContext commandContext)
{
    public static DestinationDto Dto(UpstreamDestination d)=>new(d.Id,d.ClusterId,d.Name,d.Address,d.Weight,d.Enabled,d.Metadata,d.Revision);
    private async Task<ClusterDto> DtoAsync(UpstreamCluster c,CancellationToken ct)=>new(c.Id,c.ProjectId,c.EnvironmentId,c.Name,c.LoadBalancingPolicy,c.HealthCheckEnabled,c.HealthCheckPath,c.HealthCheckIntervalSec,c.Status,c.Revision,await db.Set<UpstreamDestination>().AsNoTracking().Where(d=>d.ClusterId==c.Id).OrderBy(d=>d.Name).Select(d=>Dto(d)).ToArrayAsync(ct));
    public async Task<ClusterDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {
        var c=await db.Set<UpstreamCluster>().AsNoTracking().SingleOrDefaultAsync(c=>c.Id==id,ct)??throw ScopeResolver.Missing();
        var scope=await scopes.EnvironmentAsync(c.EnvironmentId,ct);
        if(!await auth.CanAsync(actor,"cluster.read",new("cluster",id,scope),ct)) throw ScopeResolver.Missing();
        var dto=await DtoAsync(c,ct);
        if (await auth.FunctionalPermissions(actor,scope).AnyAsync(p=>p.Code=="route.read",ct))
        {
            var routes=await db.Set<ApiRoute>().AsNoTracking().Where(r=>r.ClusterId==id && r.EnvironmentId==c.EnvironmentId)
                .OrderBy(r=>r.RouteName).ThenBy(r=>r.Id)
                .Select(r=>new ClusterRouteRef(r.Id,r.ApiVersionId,r.RouteName,r.Path,r.Enabled)).ToArrayAsync(ct);
            dto=dto with { Routes=routes,RouteCount=routes.Length };
        }
        var result=(await health.ReadAsync(c.EnvironmentId,[dto],ct))[0];
        if (health.Enabled)
        {
            if(!await auth.CanAsync(actor,"cluster.read",new("cluster",id,scope),ct)) throw ScopeResolver.Missing();
            if(!await auth.FunctionalPermissions(actor,scope).AnyAsync(p=>p.Code=="route.read",ct)) result=result with { RouteCount=null,Routes=null };
        }
        return result;
    }
    public async Task<PageResult<ClusterDto>> ListAsync(Guid environmentId,ActorContext actor,int page,int size,CancellationToken ct=default)
    {
        var scope=await scopes.EnvironmentAsync(environmentId,ct);if(!await auth.CanAsync(actor,"cluster.read",new("environment",environmentId,scope),ct)) throw ScopeResolver.Missing();
        page=Math.Clamp(page,1,1000000);size=Math.Clamp(size,1,100);
        var query=db.Set<UpstreamCluster>().AsNoTracking().Where(c=>c.EnvironmentId==environmentId);var total=await query.CountAsync(ct);
        var routeRead=await auth.FunctionalPermissions(actor,scope).AnyAsync(p=>p.Code=="route.read",ct);
        var pageRows=await query.OrderBy(c=>c.Name).ThenBy(c=>c.Id).Skip((page-1)*size).Take(size)
            .Select(c=>new { Cluster=c, Count=routeRead ? (int?)db.Set<ApiRoute>().Count(r=>r.ClusterId==c.Id && r.EnvironmentId==environmentId) : null }).ToArrayAsync(ct);
        var routeCounts=pageRows.ToDictionary(p=>p.Cluster.Id,p=>p.Count);
        var clusters=pageRows.Select(p=>p.Cluster).ToArray();
        if(clusters.Length==0)return new([],total,page,size);
        var ids=clusters.Select(c=>c.Id).ToArray();
        var destinations=(await db.Set<UpstreamDestination>().AsNoTracking().Where(d=>ids.Contains(d.ClusterId)).OrderBy(d=>d.Name).ThenBy(d=>d.Id).ToArrayAsync(ct)).ToLookup(d=>d.ClusterId);
        var result=clusters.Select(c=>new ClusterDto(c.Id,c.ProjectId,c.EnvironmentId,c.Name,c.LoadBalancingPolicy,c.HealthCheckEnabled,c.HealthCheckPath,c.HealthCheckIntervalSec,c.Status,c.Revision,destinations[c.Id].Select(Dto).ToArray(),routeCounts[c.Id])).ToArray();
        result=await health.ReadAsync(environmentId,result,ct);
        if (health.Enabled)
        {
            if(!await auth.CanAsync(actor,"cluster.read",new("environment",environmentId,scope),ct)) throw ScopeResolver.Missing();
            if(routeRead && !await auth.FunctionalPermissions(actor,scope).AnyAsync(p=>p.Code=="route.read",ct))
                result=result.Select(c=>c with {RouteCount=null}).ToArray();
        }
        return new(result,total,page,size);
    }
    public async Task<CommandResult<ClusterDto>> SaveAsync(Guid environmentId,Guid? id,SaveClusterRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.EnvironmentAsync(environmentId,ct);return await commands.ExecuteAsync(actor,scope,"cluster.save",async(_,token)=>{
        await auth.RequireAsync(actor,"cluster.write",new("environment",environmentId,scope),token);
        if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>128||request.LoadBalancingPolicy is not ("RoundRobin" or "LeastRequests" or "PowerOfTwoChoices" or "Random" or "FirstAlphabetical")||string.IsNullOrWhiteSpace(request.HealthCheckPath)||!request.HealthCheckPath.StartsWith('/')||request.HealthCheckPath.Length>256||request.HealthCheckPath.Contains('?')||request.HealthCheckIntervalSec is <1 or >3600||request.Status is not ("Active" or "Disabled")) throw new ApiException(422,"invalid_cluster","集群配置不合法。");
        var c=id is Guid existing?await db.Set<UpstreamCluster>().SingleOrDefaultAsync(c=>c.Id==existing&&c.EnvironmentId==environmentId,token)??throw ScopeResolver.Missing():new UpstreamCluster {ProjectId=scope.ProjectId!.Value,EnvironmentId=environmentId};if(id is null) db.Add(c);else {RevisionTag.Require(tag,c.Revision);c.Revision++;}
        c.Name=request.Name;c.LoadBalancingPolicy=request.LoadBalancingPolicy;c.HealthCheckEnabled=request.HealthCheckEnabled;c.HealthCheckPath=request.HealthCheckPath;c.HealthCheckIntervalSec=request.HealthCheckIntervalSec;c.Status=request.Status;
        return new CommandResult<ClusterDto>(await DtoAsync(c,token),RevisionTag.Format(c.Revision));},ct);}
    public async Task<CommandResult<ClusterDto>> CopyAsync(Guid id, CopyClusterRequest request, string? tag, ActorContext actor,CancellationToken ct)
    {
        var environment=await db.Set<UpstreamCluster>().Where(c=>c.Id==id).Select(c=>(Guid?)c.EnvironmentId).SingleOrDefaultAsync(ct)??throw ScopeResolver.Missing();
        var scope=await scopes.EnvironmentAsync(environment,ct);
        return await commands.ExecuteAsync(actor,scope,"cluster.copy",async(_,token)=>
        {
            await auth.RequireAsync(actor,"cluster.write",new("cluster",id,scope),token);
            await auth.RequireAsync(actor,"cluster.read",new("cluster",id,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"cluster.copy",commandContext.IdempotencyKey),
                CanonicalJson.Serialize(new{id,request,tag}),async inner=>
            {
                var source=await db.Set<UpstreamCluster>().SingleAsync(c=>c.Id==id&&c.EnvironmentId==environment,inner);
                RevisionTag.Require(tag,source.Revision);
                var name=request.Name.Trim();
                if(name.Length is <1 or >128) throw new ApiException(422,"invalid_cluster","请输入 1 至 128 个字符的集群名称。");
                if(await db.Set<UpstreamCluster>().AnyAsync(c=>c.EnvironmentId==environment&&c.Name==name,inner))
                    throw new ApiException(409,"cluster_name_taken","当前环境已有同名集群。");
                var copy=new UpstreamCluster { ProjectId=source.ProjectId,EnvironmentId=environment,Name=name,
                    LoadBalancingPolicy=source.LoadBalancingPolicy,HealthCheckEnabled=source.HealthCheckEnabled,HealthCheckPath=source.HealthCheckPath,
                    HealthCheckIntervalSec=source.HealthCheckIntervalSec,Status=source.Status };
                var children=await db.Set<UpstreamDestination>().AsNoTracking().Where(d=>d.ClusterId==id).OrderBy(d=>d.Name).ToArrayAsync(inner);
                var destinations=children.Select(d=>new UpstreamDestination { ClusterId=copy.Id,Name=d.Name,Address=addresses.Validate(d.Address),
                    Weight=d.Weight,Enabled=d.Enabled,Metadata=d.Metadata }).ToArray();
                db.Add(copy);db.AddRange(destinations);
                var dto=new ClusterDto(copy.Id,copy.ProjectId,environment,copy.Name,copy.LoadBalancingPolicy,copy.HealthCheckEnabled,
                    copy.HealthCheckPath,copy.HealthCheckIntervalSec,copy.Status,copy.Revision,destinations.Select(Dto).ToArray(),0);
                return new CommandResult<ClusterDto>(dto,RevisionTag.Format(copy.Revision));
            },token);
        },ct);
    }
    public async Task DeleteAsync(Guid id,string? tag,ActorContext actor,CancellationToken ct=default)
    {var cluster=await db.Set<UpstreamCluster>().AsNoTracking().SingleOrDefaultAsync(c=>c.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(cluster.EnvironmentId,ct);await commands.ExecuteAsync(actor,scope,"cluster.delete",async(_,token)=>{await auth.RequireAsync(actor,"cluster.write",new("cluster",id,scope),token);var c=await db.Set<UpstreamCluster>().SingleAsync(c=>c.Id==id,token);RevisionTag.Require(tag,c.Revision);if(await db.Set<ApiRoute>().AnyAsync(r=>r.ClusterId==id,token)) throw new ApiException(409,"cluster_in_use","集群已被路由引用。");db.RemoveRange(await db.Set<UpstreamDestination>().Where(d=>d.ClusterId==id).ToArrayAsync(token));db.Remove(c);return true;},ct);}
    public async Task<CommandResult<DestinationDto>> SaveDestinationAsync(Guid clusterId,Guid? id,SaveDestinationRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {var cluster=await db.Set<UpstreamCluster>().AsNoTracking().SingleOrDefaultAsync(c=>c.Id==clusterId,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(cluster.EnvironmentId,ct);return await commands.ExecuteAsync(actor,scope,"destination.save",async(_,token)=>{
        await auth.RequireAsync(actor,"cluster.write",new("cluster",clusterId,scope),token);if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>128||request.Weight is <1 or >1000) throw new ApiException(422,"invalid_destination","后端名称或权重不合法。");var address=addresses.Validate(request.Address);JsonFields.Validate(request.Metadata);
        var d=id is Guid existing?await db.Set<UpstreamDestination>().SingleOrDefaultAsync(d=>d.Id==existing&&d.ClusterId==clusterId,token)??throw ScopeResolver.Missing():new UpstreamDestination {ClusterId=clusterId};if(id is null) db.Add(d);else {RevisionTag.Require(tag,d.Revision);if(d.Enabled&&!request.Enabled) await ProtectLastAsync(clusterId,d.Id,token);d.Revision++;}
        d.Name=request.Name;d.Address=address;d.Weight=request.Weight;d.Enabled=request.Enabled;d.Metadata=request.Metadata;return new CommandResult<DestinationDto>(Dto(d),RevisionTag.Format(d.Revision));},ct);}
    private async Task ProtectLastAsync(Guid cluster,Guid id,CancellationToken ct) {if(!await db.Set<UpstreamDestination>().AnyAsync(d=>d.ClusterId==cluster&&d.Id!=id&&d.Enabled,ct)) throw new ApiException(409,"last_destination","不能删除或禁用集群最后一个启用后端。");}
    public async Task DeleteDestinationAsync(Guid id,string? tag,ActorContext actor,CancellationToken ct=default)
    {var dest=await db.Set<UpstreamDestination>().AsNoTracking().SingleOrDefaultAsync(d=>d.Id==id,ct)??throw ScopeResolver.Missing();var cluster=await db.Set<UpstreamCluster>().AsNoTracking().SingleAsync(c=>c.Id==dest.ClusterId,ct);var scope=await scopes.EnvironmentAsync(cluster.EnvironmentId,ct);await commands.ExecuteAsync(actor,scope,"destination.delete",async(_,token)=>{await auth.RequireAsync(actor,"cluster.write",new("destination",id,scope),token);var d=await db.Set<UpstreamDestination>().SingleAsync(d=>d.Id==id,token);RevisionTag.Require(tag,d.Revision);if(d.Enabled) await ProtectLastAsync(d.ClusterId,id,token);db.Remove(d);return true;},ct);}
}

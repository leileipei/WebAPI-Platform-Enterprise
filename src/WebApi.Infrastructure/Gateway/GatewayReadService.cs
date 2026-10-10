using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Gateway;
public sealed class GatewayReadService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,HistoricalSnapshotService history,AuditedCommandExecutor commands)
{
    private async Task<EnvironmentRecord> RequireReadAsync(Guid env,string code,ActorContext actor,CancellationToken ct)
    {
        var environment=await db.Set<EnvironmentRecord>().AsNoTracking().SingleOrDefaultAsync(e=>e.Id==env,ct)??throw ScopeResolver.Missing();
        var scope=(await scopes.ProjectAsync(environment.ProjectId,ct)) with {EnvironmentId=env};
        if(!await auth.CanAsync(actor,code,new("environment",env,scope),ct))throw ScopeResolver.Missing();return environment;
    }
    private async Task RequireReadScopeAsync(Guid env,string code,ActorContext actor,CancellationToken ct)
    {
        var scope=await (from e in db.Set<EnvironmentRecord>().AsNoTracking() join p in db.Set<Project>().AsNoTracking() on e.ProjectId equals p.Id
            where e.Id==env select new ScopeRef(p.OrganizationId,p.Id,e.Id)).SingleOrDefaultAsync(ct)??throw ScopeResolver.Missing();
        if(!await auth.CanAsync(actor,code,new("environment",env,scope),ct))throw ScopeResolver.Missing();
    }
    private static string Tag(GatewayNode n)=>$"\"{n.InstanceId}:{n.Enabled}\"";
    private static GatewayNodeDto Dto(GatewayNode n,EnvironmentRecord env)=>Dto(n,env.DesiredConfigVersion,env.DeploymentSequence);
    private static GatewayNodeDto Dto(GatewayNode n,long? desiredVersion,long deploymentSequence)=>new(n.Id,n.EnvironmentId,n.NodeName,n.InstanceId,n.AppVersion,n.Enabled,n.Status,n.LastHeartbeatAt is null||n.LastHeartbeatAt<DateTimeOffset.UtcNow.AddSeconds(-120),n.CurrentConfigVersion,n.CurrentDeploymentSequence,n.TargetConfigVersion,n.LastHeartbeatAt,Tag(n),desiredVersion,deploymentSequence,SnapshotSchemaCapabilities.Read(n));
    public async Task<PageResult<GatewayNodeDto>> NodesAsync(Guid env,ActorContext actor,int page=1,int size=50,CancellationToken ct=default)
    {
        await RequireReadScopeAsync(env,"gateway.read",actor,ct);page=Math.Clamp(page,1,1000000);size=Math.Clamp(size,1,100);
        var query=db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==env);var total=await query.CountAsync(ct);
        var selected=query.OrderBy(n=>n.NodeName).ThenBy(n=>n.Id).Skip((page-1)*size).Take(size);
        var nodes=await (from n in selected join e in db.Set<EnvironmentRecord>().AsNoTracking() on n.EnvironmentId equals e.Id
            select new {Node=n,e.DesiredConfigVersion,e.DeploymentSequence}).OrderBy(x=>x.Node.NodeName).ThenBy(x=>x.Node.Id).ToArrayAsync(ct);
        return new(nodes.Select(x=>Dto(x.Node,x.DesiredConfigVersion,x.DeploymentSequence)).ToArray(),total,page,size);
    }
    public async Task<GatewayNodeDto> NodeAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {var n=await db.Set<GatewayNode>().AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id,ct)??throw ScopeResolver.Missing();return Dto(n,await RequireReadAsync(n.EnvironmentId,"gateway.read",actor,ct));}
    public async Task<PageResult<GatewayEventDto>> EventsAsync(Guid id,ActorContext actor,int page=1,int size=50,CancellationToken ct=default)
    {
        var environment=await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.Id==id).Select(n=>(Guid?)n.EnvironmentId).SingleOrDefaultAsync(ct)??throw ScopeResolver.Missing();
        await RequireReadScopeAsync(environment,"gateway.read",actor,ct);page=Math.Clamp(page,1,1000000);size=Math.Clamp(size,1,100);
        var capped=db.Set<GatewayNodeEvent>().AsNoTracking().Where(e=>e.GatewayNodeId==id).OrderByDescending(e=>e.Id).Take(10000);
        var total=await capped.CountAsync(ct);
        var events=await capped.OrderByDescending(e=>e.Id).Skip((page-1)*size).Take(size).Select(e=>new GatewayEventDto(e.Id,e.EventType,e.Message,e.Detail,e.CreatedAt)).ToArrayAsync(ct);
        return new(events,total,page,size);
    }
    public async Task<ManagementSnapshot> SnapshotAsync(Guid env,long version,ActorContext actor,CancellationToken ct=default)
    {await RequireReadAsync(env,"gateway.config.read",actor,ct);var artifact=await history.ReadAsync(env,version,ct);var s=artifact.Snapshot;return new(s.SchemaVersion,s.EnvironmentId,s.ConfigVersion,s.GeneratedAt,s.Routes,s.Clusters,s.Policies,s.Applications.Select(a=>new ManagementApplication(a.Id,a.Status,a.Credentials.Select(k=>new ManagementCredential(k.Id,k.AccessKey,k.Status,k.ValidFrom,k.ExpiresAt)).ToArray(),a.Permissions)).ToArray());}
    public async Task<PageResult<SnapshotInfo>> SnapshotsAsync(Guid env,ActorContext actor,int page=1,int size=50,CancellationToken ct=default)
    {
        await RequireReadScopeAsync(env,"gateway.config.read",actor,ct);page=Math.Clamp(page,1,1000000);size=Math.Clamp(size,1,100);
        var query=db.Set<GatewayConfigVersion>().AsNoTracking().Where(v=>v.EnvironmentId==env);var total=await query.CountAsync(ct);
        var selected=query.OrderByDescending(v=>v.VersionNo).ThenBy(v=>v.Id).Skip((page-1)*size).Take(size);
        var rows=await (from v in selected join snapshot in db.Set<GatewayConfigSnapshot>().AsNoTracking() on v.Id equals snapshot.ConfigVersionId into snapshots
            from snapshot in snapshots.DefaultIfEmpty() select new {v.VersionNo,v.SnapshotHash,v.CreatedAt,v.Status,SizeBytes=(long?)snapshot.SizeBytes}).ToArrayAsync(ct);
        if(rows.Length==0)return new([],total,page,size);
        // Keep the page order after the join, and select only metadata, never either payload column.
        rows=rows.OrderByDescending(v=>v.VersionNo).ToArray();var versions=rows.Select(v=>v.VersionNo).ToArray();
        var latest=await db.Set<ReleaseRecord>().AsNoTracking().Where(r=>r.EnvironmentId==env&&versions.Contains(r.ToConfigVersion)&&r.DeploymentSequence>0
            &&!db.Set<ReleaseRecord>().Any(other=>other.EnvironmentId==env&&other.ToConfigVersion==r.ToConfigVersion&&other.DeploymentSequence>r.DeploymentSequence))
            .Select(r=>new {r.Id,r.ToConfigVersion,r.DeploymentSequence,r.Status}).ToDictionaryAsync(r=>r.ToConfigVersion,ct);
        var result=rows.Select(v=>{latest.TryGetValue(v.VersionNo,out var release);return new SnapshotInfo(v.VersionNo,v.SnapshotHash!,v.SizeBytes??throw new InvalidOperationException("Sequence contains no elements"),v.CreatedAt,v.Status,release?.Id,release?.DeploymentSequence,release?.Status);}).ToArray();
        return new(result,total,page,size);
    }
    public async Task<GatewayNodeDto> SetEnabledAsync(Guid id,bool enabled,string? etag,ActorContext actor,CancellationToken ct=default)
    {
        var original=await db.Set<GatewayNode>().AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(original.EnvironmentId,ct);
        return await commands.ExecuteAsync(actor,scope,"gateway.identity.enabled",async(_,token)=>{await auth.RequireAsync(actor,"gateway.operate",new("node",id,scope),token);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={original.EnvironmentId} FOR UPDATE",token);var node=await db.Set<GatewayNode>().SingleAsync(n=>n.Id==id,token);if(Tag(node)!=etag) throw new ApiException(412,"stale_revision","节点实例或启用状态已变化。");node.Enabled=enabled;return Dto(node,await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==node.EnvironmentId,token));},ct);
    }
}

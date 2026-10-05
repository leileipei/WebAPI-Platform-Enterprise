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
    private async Task<ScopeRef> RequireReadAsync(Guid env,string code,ActorContext actor,CancellationToken ct)
    {var scope=await scopes.EnvironmentAsync(env,ct);if(!await auth.CanAsync(actor,code,new("environment",env,scope),ct)) throw ScopeResolver.Missing();return scope;}
    private static string Tag(GatewayNode n)=>$"\"{n.InstanceId}:{n.Enabled}\"";
    private static GatewayNodeDto Dto(GatewayNode n,EnvironmentRecord env)=>new(n.Id,n.EnvironmentId,n.NodeName,n.InstanceId,n.AppVersion,n.Enabled,n.Status,n.LastHeartbeatAt is null||n.LastHeartbeatAt<DateTimeOffset.UtcNow.AddSeconds(-120),n.CurrentConfigVersion,n.CurrentDeploymentSequence,n.TargetConfigVersion,n.LastHeartbeatAt,Tag(n),env.DesiredConfigVersion,env.DeploymentSequence,SnapshotSchemaCapabilities.Read(n));
    public async Task<PageResult<GatewayNodeDto>> NodesAsync(Guid env,ActorContext actor,int page=1,int size=50,CancellationToken ct=default)
    {await RequireReadAsync(env,"gateway.read",actor,ct);var environment=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==env,ct);return Pagination.Slice((await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==env).OrderBy(n=>n.NodeName).ToArrayAsync(ct)).Select(n=>Dto(n,environment)).ToArray(),page,size);}
    public async Task<GatewayNodeDto> NodeAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {var n=await db.Set<GatewayNode>().AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id,ct)??throw ScopeResolver.Missing();await RequireReadAsync(n.EnvironmentId,"gateway.read",actor,ct);return Dto(n,await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==n.EnvironmentId,ct));}
    public async Task<PageResult<GatewayEventDto>> EventsAsync(Guid id,ActorContext actor,int page=1,int size=50,CancellationToken ct=default)
    {await NodeAsync(id,actor,ct);return Pagination.Slice(await db.Set<GatewayNodeEvent>().AsNoTracking().Where(e=>e.GatewayNodeId==id).OrderByDescending(e=>e.Id).Take(10000).Select(e=>new GatewayEventDto(e.Id,e.EventType,e.Message,e.Detail,e.CreatedAt)).ToArrayAsync(ct),page,size);}
    public async Task<ManagementSnapshot> SnapshotAsync(Guid env,long version,ActorContext actor,CancellationToken ct=default)
    {await RequireReadAsync(env,"gateway.config.read",actor,ct);var artifact=await history.ReadAsync(env,version,ct);var s=artifact.Snapshot;return new(s.SchemaVersion,s.EnvironmentId,s.ConfigVersion,s.GeneratedAt,s.Routes,s.Clusters,s.Policies,s.Applications.Select(a=>new ManagementApplication(a.Id,a.Status,a.Credentials.Select(k=>new ManagementCredential(k.Id,k.AccessKey,k.Status,k.ValidFrom,k.ExpiresAt)).ToArray(),a.Permissions)).ToArray());}
    public async Task<PageResult<SnapshotInfo>> SnapshotsAsync(Guid env,ActorContext actor,int page=1,int size=50,CancellationToken ct=default)
    {
        await RequireReadAsync(env,"gateway.config.read",actor,ct);var rows=await db.Set<GatewayConfigVersion>().AsNoTracking().Where(v=>v.EnvironmentId==env).OrderByDescending(v=>v.VersionNo).ToArrayAsync(ct);var slice=Pagination.Slice(rows,page,size);var result=new List<SnapshotInfo>();
        foreach(var v in slice.Items) {var r=await db.Set<ReleaseRecord>().AsNoTracking().Where(r=>r.EnvironmentId==env&&r.ToConfigVersion==v.VersionNo&&r.DeploymentSequence>0).OrderByDescending(r=>r.DeploymentSequence).FirstOrDefaultAsync(ct);var bytes=await db.Set<GatewayConfigSnapshot>().Where(s=>s.ConfigVersionId==v.Id).Select(s=>s.SizeBytes).SingleAsync(ct);result.Add(new(v.VersionNo,v.SnapshotHash!,bytes,v.CreatedAt,v.Status,r?.Id,r?.DeploymentSequence,r?.Status));}return new(result,slice.Total,slice.Page,slice.PageSize);
    }
    public async Task<GatewayNodeDto> SetEnabledAsync(Guid id,bool enabled,string? etag,ActorContext actor,CancellationToken ct=default)
    {
        var original=await db.Set<GatewayNode>().AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(original.EnvironmentId,ct);
        return await commands.ExecuteAsync(actor,scope,"gateway.identity.enabled",async(_,token)=>{await auth.RequireAsync(actor,"gateway.operate",new("node",id,scope),token);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={original.EnvironmentId} FOR UPDATE",token);var node=await db.Set<GatewayNode>().SingleAsync(n=>n.Id==id,token);if(Tag(node)!=etag) throw new ApiException(412,"stale_revision","节点实例或启用状态已变化。");node.Enabled=enabled;return Dto(node,await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==node.EnvironmentId,token));},ct);
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Runtime;
namespace WebApi.Infrastructure.Gateway;
public sealed class NodeRegistry(WebApiDbContext db,NodeIdentityService identities)
{
    public async Task<RegisteredNode> RegisterAsync(RegisterNodeRequest request,string secret,CancellationToken ct=default)
    {
        var hash=identities.EnrollmentHash(request.EnvironmentId,request.NodeName,secret);if(!Regex.IsMatch(request.NodeName,"^[a-zA-Z0-9_.-]{1,128}$")||request.AppVersion.Length is <1 or >64) throw new ApiException(422,"invalid_node","节点名称或版本格式不合法。");
        await using var tx=await db.Database.BeginTransactionAsync(ct);await LockAsync(request.EnvironmentId,ct);
        if(!await db.Set<EnvironmentRecord>().AnyAsync(e=>e.Id==request.EnvironmentId&&e.Status=="Active",ct)) throw NodeIdentityService.Denied();
        var node=await db.Set<GatewayNode>().SingleOrDefaultAsync(n=>n.EnvironmentId==request.EnvironmentId&&n.NodeName==request.NodeName,ct);
        if(node is null) {node=new() {EnvironmentId=request.EnvironmentId,NodeName=request.NodeName,IdentityHash=hash,Status="NotReady"};db.Add(node);}
        else if(!node.Enabled||node.IdentityHash!=hash) throw NodeIdentityService.Denied();
        if(node.InstanceId!=request.InstanceId.ToString()) {node.InstanceId=request.InstanceId.ToString();node.CurrentConfigVersion=0;node.CurrentDeploymentSequence=0;node.Status="NotReady";db.Add(new GatewayNodeEvent {GatewayNodeId=node.Id,EventType="registered",Message="节点实例注册",Detail=JsonSerializer.Serialize(new {instanceId=request.InstanceId})});}
        node.Metadata=SnapshotSchemaCapabilities.Merge(node.Metadata,request.InstanceId,request.SupportedSnapshotSchemas);node.AppVersion=request.AppVersion;node.LastHeartbeatAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(node.Id,node.EnvironmentId,request.InstanceId);
    }
    private async Task LockAsync(Guid env,CancellationToken ct)=>await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={env} FOR UPDATE",ct);
    public async Task HeartbeatAsync(NodeIdentity identity,NodeHeartbeat heartbeat,CancellationToken ct=default)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);await LockAsync(identity.EnvironmentId,ct);var node=await identities.RevalidateAsync(identity,heartbeat.InstanceId,ct);
        if(heartbeat.Status is not ("Ready" or "NotReady" or "Degraded")||heartbeat.ConfigVersion<0||heartbeat.DeploymentSequence<0||(heartbeat.ConfigVersion==0)!=(heartbeat.DeploymentSequence==0)||heartbeat.DeploymentSequence<node.CurrentDeploymentSequence||heartbeat.Status=="Ready"&&heartbeat.ConfigVersion==0) throw new ApiException(409,"invalid_heartbeat","节点心跳状态或运行序列不合法。");
        if(heartbeat.DeploymentSequence>0&&!await db.Set<ReleaseRecord>().AnyAsync(r=>r.EnvironmentId==node.EnvironmentId&&r.DeploymentSequence==heartbeat.DeploymentSequence&&r.ToConfigVersion==heartbeat.ConfigVersion,ct)) throw new ApiException(409,"unknown_running_config","节点报告的运行版本没有对应发布事实。");
        if(node.Status!=heartbeat.Status) db.Add(new GatewayNodeEvent {GatewayNodeId=node.Id,EventType="status",Message="节点状态更新",Detail=JsonSerializer.Serialize(new {status=heartbeat.Status,configVersion=heartbeat.ConfigVersion,deploymentSequence=heartbeat.DeploymentSequence})});
        node.Metadata=SnapshotSchemaCapabilities.Merge(node.Metadata,heartbeat.InstanceId,heartbeat.SupportedSnapshotSchemas);node.CurrentConfigVersion=heartbeat.ConfigVersion;node.CurrentDeploymentSequence=heartbeat.DeploymentSequence;node.Status=heartbeat.Status;node.LastHeartbeatAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    public async Task<DesiredConfigResponse?> DesiredAsync(NodeIdentity identity,CancellationToken ct=default)
    {
        await identities.RevalidateAsync(identity,null,ct);var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==identity.EnvironmentId,ct);if(env.DesiredConfigVersion is not long version||env.DeploymentSequence<=0) return null;
        var row=await (from r in db.Set<ReleaseRecord>().AsNoTracking() join v in db.Set<GatewayConfigVersion>() on new {r.EnvironmentId,VersionNo=r.ToConfigVersion} equals new {v.EnvironmentId,v.VersionNo} join s in db.Set<GatewayConfigSnapshot>() on v.Id equals s.ConfigVersionId where r.EnvironmentId==env.Id&&r.DeploymentSequence==env.DeploymentSequence&&r.ToConfigVersion==version select new {r.Id,v.SnapshotHash,s.SizeBytes,s.PayloadBytes}).SingleAsync(ct);
        var envelope=new SnapshotEnvelope(row.Id,env.DeploymentSequence,version,row.SnapshotHash!,row.SizeBytes);RedisSnapshotStore.Validate(env.Id,envelope,row.PayloadBytes);return new(envelope,row.PayloadBytes);
    }
}

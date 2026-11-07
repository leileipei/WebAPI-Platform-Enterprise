using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Runtime;
namespace WebApi.Infrastructure.Gateway;
public sealed class NodeRegistry(WebApiDbContext db,NodeIdentityService identities)
{
    public async Task<RegisteredNode> RegisterAsync(RegisterNodeRequest request,string secret,CancellationToken ct=default)
    {
        var hash=identities.EnrollmentHash(request.EnvironmentId,request.NodeName,secret);if(!Regex.IsMatch(request.NodeName,"^[a-zA-Z0-9_.-]{1,128}$")||request.AppVersion.Length is <1 or >64) throw new ApiException(422,"invalid_node","节点名称或版本格式不合法。");
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)",ct);await LockAsync(request.EnvironmentId,ct);
        if(!await db.Set<EnvironmentRecord>().AnyAsync(e=>e.Id==request.EnvironmentId&&e.Status=="Active",ct)) throw NodeIdentityService.Denied();
        var node=await db.Set<GatewayNode>().SingleOrDefaultAsync(n=>n.EnvironmentId==request.EnvironmentId&&n.NodeName==request.NodeName,ct);
        if(node is null) {node=new() {EnvironmentId=request.EnvironmentId,NodeName=request.NodeName,IdentityHash=hash,Status="NotReady"};db.Add(node);}
        else if(!node.Enabled||node.IdentityHash!=hash) throw NodeIdentityService.Denied();
        var priorInstance=node.InstanceId;
        var instanceChanged=node.InstanceId!=request.InstanceId.ToString();
        if(instanceChanged)
        {
            var instanceText=request.InstanceId.ToString();
            var retired=await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM gateway_node_events WHERE gateway_node_id={node.Id} AND event_type='registered' AND detail->>'instanceId'={instanceText})
                    OR EXISTS(SELECT 1 FROM security_operation_targets WHERE node_id={node.Id} AND instance_guid={request.InstanceId})
                    OR EXISTS(SELECT 1 FROM gateway_security_leases WHERE node_id={node.Id} AND instance_id={request.InstanceId})
                    OR EXISTS(SELECT 1 FROM credential_usage_collector_instances WHERE node_id={node.Id} AND instance_id={request.InstanceId})
                    OR EXISTS(SELECT 1 FROM gateway_acks WHERE node_id={node.Id} AND instance_id={instanceText}) AS "Value"
                """).SingleAsync(ct);
            if(retired)throw new ApiException(409,"retired_node_instance","节点实例标识已使用，请以新的实例标识注册。");
            await db.Set<CredentialUsageCollectorInstance>().Where(x=>x.NodeId==node.Id&&x.InstanceId!=request.InstanceId&&x.EndedAt==null).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.EndedAt,DateTimeOffset.UtcNow),ct);
            node.Metadata=SecurityProtocolCapabilities.ClearReceipt(RuntimeApplicationReceipt.Clear(node.Metadata));node.InstanceId=instanceText;node.CurrentConfigVersion=0;node.CurrentDeploymentSequence=0;node.Status="NotReady";db.Add(new GatewayNodeEvent {GatewayNodeId=node.Id,EventType="registered",Message="节点实例注册",Detail=JsonSerializer.Serialize(new {instanceId=request.InstanceId})});
        }
        node.Metadata=SnapshotSchemaCapabilities.Merge(node.Metadata,request.InstanceId,request.SupportedSnapshotSchemas);node.Metadata=SecurityProtocolCapabilities.Merge(node.Metadata,request.InstanceId,request.SecurityCapabilities);
        var priorTargets=instanceChanged?await db.Set<SecurityOperationTarget>().Where(x=>x.NodeId==node.Id&&x.EnvironmentId==request.EnvironmentId&&x.InstanceGuid!=request.InstanceId).ToArrayAsync(ct):[];
        var missingOperations=await db.Set<SecurityEvent>().AsNoTracking()
            .Where(e=>e.EnvironmentId==request.EnvironmentId&&!db.Set<SecurityOperationTarget>().Any(t=>t.OperationId==e.OperationId&&t.EnvironmentId==e.EnvironmentId&&t.NodeId==node.Id&&t.InstanceGuid==request.InstanceId))
            .Select(e=>e.OperationId).Distinct().ToArrayAsync(ct);
        var minimum=await db.Set<EnvironmentSecurityState>().AsNoTracking().Where(s=>s.EnvironmentId==request.EnvironmentId).Select(s=>s.MinimumSnapshotSchema).SingleOrDefaultAsync(ct);
        var supported=SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(node,minimum);
        if((minimum is "2.3" or "2.4"||priorTargets.Length>0||missingOperations.Length>0)&&!supported)
            throw new ApiException(409,"gateway_security_capability_required","当前环境要求节点声明兼容的快照和安全协议能力。");
        if(priorTargets.Length>0||missingOperations.Length>0)
        {
            var now=await SecurityDatabaseClock.ObserveAsync(db,ct);
            if(await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db,now,ct))
                throw new ApiException(503,"security_clock_unavailable","安全时间不可用或已回拨。");
            var currentOperations=await db.Set<SecurityOperationTarget>().AsNoTracking().Where(x=>x.NodeId==node.Id&&x.EnvironmentId==request.EnvironmentId&&x.InstanceGuid==request.InstanceId).Select(x=>x.OperationId).ToHashSetAsync(ct);
            GatewaySecurityLease? priorLease=null;
            if(Guid.TryParse(priorInstance,out var previousId))priorLease=await db.Set<GatewaySecurityLease>().AsNoTracking().SingleOrDefaultAsync(x=>x.NodeId==node.Id&&x.InstanceId==previousId,ct);
            var added=0;
            foreach(var group in priorTargets.GroupBy(x=>new{x.OperationId,x.EnvironmentId}))
            {
                if(currentOperations.Add(group.Key.OperationId))
                {db.Add(new SecurityOperationTarget {OperationId=group.Key.OperationId,EnvironmentId=group.Key.EnvironmentId,NodeId=node.Id,InstanceGuid=request.InstanceId,ProtocolSupportedAtFreeze=supported,CreatedAt=now});added++;}
                foreach(var old in group)
                {
                    old.SupersededByInstanceGuid=request.InstanceId;
                    if(old.InstanceGuid.ToString()==priorInstance&&old.ProtocolSupportedAtFreeze)
                    {
                        // Missing lease history is unknown, not proof that the old
                        // instance has no outstanding admission lease.
                        if(priorLease is not null&&(old.LastLeaseUntil is null||old.LastLeaseUntil<priorLease.LastIssuedLeaseUntil))old.LastLeaseUntil=priorLease.LastIssuedLeaseUntil;
                    }
                }
            }
            // A newly registered node must receive a distinct challenge for every
            // existing security operation in its environment. A later full
            // checkpoint alone is not an acknowledgement of those targets.
            foreach(var operationId in missingOperations.Where(id=>currentOperations.Add(id)))
            {db.Add(new SecurityOperationTarget {OperationId=operationId,EnvironmentId=request.EnvironmentId,NodeId=node.Id,InstanceGuid=request.InstanceId,ProtocolSupportedAtFreeze=supported,CreatedAt=now});added++;}
            if(added>0)db.Add(new GatewayNodeEvent {GatewayNodeId=node.Id,EventType="security_targets_added",Message="节点安全确认目标已更新",Detail=JsonSerializer.Serialize(new {instanceId=request.InstanceId,targetCount=added,protocolSupported=supported})});
        }
        node.AppVersion=request.AppVersion;node.LastHeartbeatAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(node.Id,node.EnvironmentId,request.InstanceId);
    }
    private async Task LockAsync(Guid env,CancellationToken ct)=>await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={env} FOR UPDATE",ct);
    public async Task HeartbeatAsync(NodeIdentity identity,NodeHeartbeat heartbeat,CancellationToken ct=default)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);await LockAsync(identity.EnvironmentId,ct);var node=await identities.RevalidateAsync(identity,heartbeat.InstanceId,ct);
        if(heartbeat.Status is not ("Ready" or "NotReady" or "Degraded")||heartbeat.ConfigVersion<0||heartbeat.DeploymentSequence<0||(heartbeat.ConfigVersion==0)!=(heartbeat.DeploymentSequence==0)||heartbeat.DeploymentSequence<node.CurrentDeploymentSequence||heartbeat.Status=="Ready"&&heartbeat.ConfigVersion==0) throw new ApiException(409,"invalid_heartbeat","节点心跳状态或运行序列不合法。");
        if(heartbeat.DeploymentSequence>0&&!await db.Set<ReleaseRecord>().AnyAsync(r=>r.EnvironmentId==node.EnvironmentId&&r.DeploymentSequence==heartbeat.DeploymentSequence&&r.ToConfigVersion==heartbeat.ConfigVersion,ct)) throw new ApiException(409,"unknown_running_config","节点报告的运行版本没有对应发布事实。");
        if(node.Status!=heartbeat.Status) db.Add(new GatewayNodeEvent {GatewayNodeId=node.Id,EventType="status",Message="节点状态更新",Detail=JsonSerializer.Serialize(new {status=heartbeat.Status,configVersion=heartbeat.ConfigVersion,deploymentSequence=heartbeat.DeploymentSequence})});
        node.Metadata=SnapshotSchemaCapabilities.Merge(node.Metadata,heartbeat.InstanceId,heartbeat.SupportedSnapshotSchemas);node.Metadata=SecurityProtocolCapabilities.Merge(node.Metadata,heartbeat.InstanceId,heartbeat.SecurityCapabilities);node.CurrentConfigVersion=heartbeat.ConfigVersion;node.CurrentDeploymentSequence=heartbeat.DeploymentSequence;node.Status=heartbeat.Status;node.LastHeartbeatAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }
    public async Task<DesiredConfigResponse?> DesiredAsync(NodeIdentity identity,CancellationToken ct=default)
    {
        await identities.RevalidateAsync(identity,null,ct);var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==identity.EnvironmentId,ct);if(env.DesiredConfigVersion is not long version||env.DeploymentSequence<=0) return null;
        var row=await (from r in db.Set<ReleaseRecord>().AsNoTracking() join v in db.Set<GatewayConfigVersion>() on new {r.EnvironmentId,VersionNo=r.ToConfigVersion} equals new {v.EnvironmentId,v.VersionNo} join s in db.Set<GatewayConfigSnapshot>() on v.Id equals s.ConfigVersionId where r.EnvironmentId==env.Id&&r.DeploymentSequence==env.DeploymentSequence&&r.ToConfigVersion==version select new {r.Id,v.SnapshotHash,s.SizeBytes,s.PayloadBytes}).SingleAsync(ct);
        var envelope=new SnapshotEnvelope(row.Id,env.DeploymentSequence,version,row.SnapshotHash!,row.SizeBytes);RedisSnapshotStore.Validate(env.Id,envelope,row.PayloadBytes);return new(envelope,row.PayloadBytes);
    }
}

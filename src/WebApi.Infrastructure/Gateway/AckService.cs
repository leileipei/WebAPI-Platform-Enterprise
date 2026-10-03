using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Gateway;
public sealed class AckService(WebApiDbContext db,NodeIdentityService identities)
{
    public async Task<AckResult> RecordAsync(Guid nodeId,NodeAck ack,NodeIdentity identity,CancellationToken ct=default)
    {
        if(nodeId!=identity.NodeId) throw NodeIdentityService.Denied();await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={identity.EnvironmentId} FOR UPDATE",ct);
        var node=await identities.RevalidateAsync(identity,ack.InstanceId,ct);var r=await db.Set<ReleaseRecord>().SingleOrDefaultAsync(r=>r.Id==ack.ReleaseId&&r.EnvironmentId==identity.EnvironmentId,ct)??throw new ApiException(409,"ack_release_mismatch","确认不属于该环境发布。");
        var target=await db.Set<ReleaseTarget>().SingleOrDefaultAsync(t=>t.ReleaseId==r.Id&&t.NodeId==nodeId,ct);var config=await db.Set<GatewayConfigVersion>().SingleOrDefaultAsync(v=>v.EnvironmentId==r.EnvironmentId&&v.VersionNo==r.ToConfigVersion,ct);
        if(target is null||target.InstanceId!=ack.InstanceId.ToString()||ack.ConfigVersion!=r.ToConfigVersion||ack.DeploymentSequence!=r.DeploymentSequence||config?.SnapshotHash!=ack.PayloadHash) throw new ApiException(409,"ack_target_mismatch","实例、版本、序列或摘要不匹配冻结目标。");
        if(ack.AppliedAt.Offset!=TimeSpan.Zero||ack.AppliedAt>DateTimeOffset.UtcNow.AddMinutes(5)||ack.AppliedAt<r.CreatedAt.AddMinutes(-5)||ack.Success&&ack.ErrorCode is not null||!ack.Success&&(ack.ErrorCode is null||!Regex.IsMatch(ack.ErrorCode,"^[a-z0-9_]{1,64}$"))) throw new ApiException(422,"invalid_ack","确认时间或错误代码不合法。");
        var previous=await db.Set<GatewayAck>().SingleOrDefaultAsync(a=>a.ReleaseId==r.Id&&a.NodeId==nodeId,ct);
        if(previous is not null)
        {if(previous.Success!=ack.Success||previous.ErrorCode!=ack.ErrorCode||previous.InstanceId!=ack.InstanceId.ToString()||previous.PayloadHash!=ack.PayloadHash||previous.DeploymentSequence!=ack.DeploymentSequence||previous.ConfigVersion!=ack.ConfigVersion) throw new ApiException(409,"ack_conflict","已经记录不同确认结果。");await tx.CommitAsync(ct);return new(r.Status,true);}
        if(r.Status!="Publishing") throw new ApiException(409,"release_not_publishing","该发布已结束，不接受延迟确认。");
        if(r.DeadlineAt<=DateTimeOffset.UtcNow) {await ReleaseTimeoutService.FailAsync(db,r,"ack_timeout",ct);await tx.CommitAsync(ct);throw new ApiException(409,"ack_deadline_passed","确认已超时，发布失败。");}
        db.Add(new GatewayAck {ReleaseId=r.Id,NodeId=nodeId,InstanceId=ack.InstanceId.ToString(),ConfigVersion=ack.ConfigVersion,DeploymentSequence=ack.DeploymentSequence,PayloadHash=ack.PayloadHash,AppliedAt=ack.AppliedAt,Success=ack.Success,ErrorCode=ack.ErrorCode});node.LastHeartbeatAt=DateTimeOffset.UtcNow;
        if(ack.Success) {node.CurrentConfigVersion=ack.ConfigVersion;node.CurrentDeploymentSequence=ack.DeploymentSequence;node.Status="Ready";}else node.Status="Degraded";
        db.Add(new GatewayNodeEvent {GatewayNodeId=nodeId,EventType=ack.Success?"applied":"apply_failed",Message=ack.Success?"配置已应用":"配置应用失败",Detail=JsonSerializer.Serialize(new {releaseId=r.Id,configVersion=ack.ConfigVersion,deploymentSequence=ack.DeploymentSequence,errorCode=ack.ErrorCode})});
        await db.SaveChangesAsync(ct);
        if(!ack.Success) await ReleaseTimeoutService.FailAsync(db,r,"node_"+ack.ErrorCode,ct);
        else if(await db.Set<GatewayAck>().CountAsync(a=>a.ReleaseId==r.Id&&a.Success,ct)==await db.Set<ReleaseTarget>().CountAsync(t=>t.ReleaseId==r.Id,ct))
        {
            r.Status="Succeeded";r.CompletedAt=DateTimeOffset.UtcNow;config!.Status="Published";config.PublishedAt=DateTimeOffset.UtcNow;
            var candidate=JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes!,CanonicalJson.Options)!;
            foreach(var id in candidate.VersionIds) {var version=await db.Set<ApiVersion>().SingleAsync(v=>v.Id==id,ct);version.Status="Published";version.SealedAt??=DateTimeOffset.UtcNow;}
            var project=await db.Set<EnvironmentRecord>().Where(e=>e.Id==r.EnvironmentId).Select(e=>e.ProjectId).SingleAsync(ct);var org=await db.Set<Project>().Where(p=>p.Id==project).Select(p=>p.OrganizationId).SingleAsync(ct);
            db.Add(new AuditLog {EnvironmentId=r.EnvironmentId,ProjectId=project,OrganizationId=org,UserId=r.PublishRequestedBy,Action="release.succeeded",ResourceType="ReleaseRecord",ResourceId=r.Id.ToString(),TraceId=r.PublishTraceId,AfterJson=JsonSerializer.Serialize(new {configVersion=r.ToConfigVersion,deploymentSequence=r.DeploymentSequence})});
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(r.Status,false);
    }
}

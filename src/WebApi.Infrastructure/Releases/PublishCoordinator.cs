using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.Releases;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Messaging;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Releases;
public sealed record PublishSettings(int MinimumNodes=2,int HeartbeatGraceSeconds=120,int AckTimeoutSeconds=120);
public sealed class PublishCoordinator(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,ReleaseService releases,SnapshotCompiler compiler,PublishSettings settings)
{
    public async Task<ReleaseDto> StartAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {
        var initial=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);
        return await commands.ExecuteAsync(actor,scope,"release.publish",async(_,token)=>{
            await RequirePublishAsync(initial,actor,scope,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"release.publish",requestContext.IdempotencyKey),CanonicalJson.Serialize(new {releaseId=id}),async inner=>{
                await LockEnvironmentAsync(initial.EnvironmentId,inner);var r=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id,inner);ReleaseStateMachine.Require(r.Status,"Ready");
                await VerifyBaselineAsync(r,inner);if(await db.Set<ReleaseRecord>().AnyAsync(x=>x.EnvironmentId==r.EnvironmentId&&x.Id!=id&&(x.Status=="Building"||x.Status=="Publishing"),inner)) throw new ApiException(409,"environment_busy","该环境已有正在下发的发布。");
                await TargetsAsync(r.EnvironmentId,inner);await VerifyVersionsAsync(r,inner);r.Status="Building";r.PublishRequestedBy=actor.UserId;r.PublishTraceId=actor.TraceId;
                return await releases.DtoAsync(r,inner);
            },token);
        },ct);
    }
    public async Task LockEnvironmentAsync(Guid envId,CancellationToken ct)=>await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={envId} FOR UPDATE",ct);
    private async Task VerifyBaselineAsync(ReleaseRecord r,CancellationToken ct)
    {var current=await db.Set<EnvironmentRecord>().AsNoTracking().Where(e=>e.Id==r.EnvironmentId).Select(e=>e.DesiredConfigVersion).SingleAsync(ct);if((current??0)!=r.BaselineConfigVersion) throw new ApiException(409,"stale_baseline","运行基线已变化，请重新建立候选并审批。");}
    private static FrozenReleaseCandidate Candidate(ReleaseRecord r)=>JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes!,CanonicalJson.Options)??throw new ApiException(422,"invalid_candidate","冻结候选不完整。");
    private async Task RequirePublishAsync(ReleaseRecord r,ActorContext actor,ScopeRef scope,CancellationToken ct)
    {
        await auth.RequireAsync(actor,"release.publish",new("environment",r.EnvironmentId,scope),ct);
        if(r.ApprovalPolicy is null) return;
        foreach(var version in Candidate(r).Versions)
        {var actual=await scopes.ApiAsync(version.Api.Id,ct);if(actual.OrganizationId!=scope.OrganizationId||actual.ProjectId!=scope.ProjectId) throw new ApiException(409,"candidate_scope_changed","候选资源范围已变化。");await auth.RequireAsync(actor,"release.publish",new("api",version.Api.Id,actual with {EnvironmentId=r.EnvironmentId}),ct);}
    }
    private async Task VerifyVersionsAsync(ReleaseRecord r,CancellationToken ct)
    {foreach(var frozen in Candidate(r).Versions) {var v=await db.Set<ApiVersion>().AsNoTracking().SingleOrDefaultAsync(v=>v.Id==frozen.Version.Id,ct);if(v is null||v.ApiId!=frozen.Api.Id||v.Revision!=frozen.Version.Revision) throw new ApiException(409,"version_changed_since_approval","版本内容在审批后发生变化，请重新提交审批。");}}
    private async Task<GatewayNode[]> TargetsAsync(Guid envId,CancellationToken ct)
    {
        var nodes=await db.Set<GatewayNode>().Where(n=>n.EnvironmentId==envId&&n.Enabled).OrderBy(n=>n.Id).ToArrayAsync(ct);var threshold=DateTimeOffset.UtcNow.AddSeconds(-settings.HeartbeatGraceSeconds);
        if(nodes.Length<settings.MinimumNodes||nodes.Any(n=>n.IdentityHash.Length!=64||!Guid.TryParse(n.InstanceId,out _)||n.LastHeartbeatAt is null||n.LastHeartbeatAt<threshold)) throw new ApiException(409,"gateway_cohort_unavailable","至少需要两个有效注册节点，且全部已启用节点必须在线；不会缩减确认目标。");return nodes;
    }
    public async Task<bool> BuildNextAsync(CancellationToken ct=default)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
        var r=await db.Set<ReleaseRecord>().FromSqlRaw("SELECT * FROM release_records WHERE status='Building' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(ct);
        if(r is null) {await tx.CommitAsync(ct);return false;}
        await LockEnvironmentAsync(r.EnvironmentId,ct);var scope=await scopes.EnvironmentAsync(r.EnvironmentId,ct);
        try
        {
            if(r.PublishRequestedBy is not Guid publisher) throw new ApiException(409,"publisher_missing","发布身份缺失。");
            await RequirePublishAsync(r,new(publisher,r.PublishTraceId??r.Id.ToString()),scope,ct);await VerifyBaselineAsync(r,ct);await VerifyVersionsAsync(r,ct);var nodes=await TargetsAsync(r.EnvironmentId,ct);
            var env=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==r.EnvironmentId,ct);var baseline=new RuntimeSnapshot("2.0",env.Id,0,DateTimeOffset.UtcNow,[],[],[],[]);
            if(r.BaselineConfigVersion>0)
            {var bytes=await (from v in db.Set<GatewayConfigVersion>() join s in db.Set<GatewayConfigSnapshot>() on v.Id equals s.ConfigVersionId where v.EnvironmentId==env.Id&&v.VersionNo==r.BaselineConfigVersion select s.PayloadBytes).SingleAsync(ct);baseline=JsonSerializer.Deserialize<RuntimeSnapshot>(bytes,CanonicalJson.Options)!;}
            var next=(await db.Set<GatewayConfigVersion>().Where(v=>v.EnvironmentId==env.Id).Select(v=>(long?)v.VersionNo).MaxAsync(ct)??0)+1;var frozen=Candidate(r);var compiled=compiler.Compile(frozen,baseline,next,DateTimeOffset.UtcNow);
            var config=new GatewayConfigVersion {EnvironmentId=env.Id,VersionNo=next,Status="Publishing",CreatedBy=publisher,SnapshotHash=compiled.Hash};config.SnapshotKey=$"environment/{env.Id}/snapshot/{next}";db.Add(config);db.Add(new GatewayConfigSnapshot {ConfigVersionId=config.Id,PayloadBytes=compiled.Payload.ToArray(),Payload=Encoding.UTF8.GetString(compiled.Payload.Span),SizeBytes=compiled.Size});
            env.DeploymentSequence=checked(env.DeploymentSequence+1);env.DesiredConfigVersion=next;r.ToConfigVersion=next;r.DeploymentSequence=env.DeploymentSequence;r.Status="Publishing";r.DeadlineAt=DateTimeOffset.UtcNow.AddSeconds(settings.AckTimeoutSeconds);
            foreach(var node in nodes) {db.Add(new ReleaseTarget {ReleaseId=r.Id,NodeId=node.Id,InstanceId=node.InstanceId});node.TargetConfigVersion=next;}
            foreach(var v in frozen.Versions) {var version=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==v.Version.Id,ct);version.SealedAt??=DateTimeOffset.UtcNow;version.Status="Publishing";}
            var message=new OutboxMessage {EnvironmentId=env.Id,ReleaseId=r.Id,DeploymentSequence=env.DeploymentSequence,EventType="desired_config"};message.Payload=JsonSerializer.Serialize(new DeploymentEvent(message.Id,env.Id,env.DeploymentSequence,r.Id,next),CanonicalJson.Options);db.Add(message);
            Audit(r,scope,"release.snapshot.persisted",new {configVersion=next,deploymentSequence=env.DeploymentSequence,payloadHash=compiled.Hash,targetCount=nodes.Length});
        }
        catch(ApiException error) {r.Status="Failed";r.FailureCode=error.Code;r.CompletedAt=DateTimeOffset.UtcNow;Audit(r,scope,"release.build.failed",new {error=error.Code});}
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;
    }
    private void Audit(ReleaseRecord r,ScopeRef scope,string action,object after)=>db.Add(new AuditLog {OrganizationId=scope.OrganizationId,ProjectId=scope.ProjectId,EnvironmentId=r.EnvironmentId,UserId=r.PublishRequestedBy,Action=action,ResourceType="ReleaseRecord",ResourceId=r.Id.ToString(),TraceId=r.PublishTraceId,AfterJson=JsonSerializer.Serialize(after,CanonicalJson.Options)});
}

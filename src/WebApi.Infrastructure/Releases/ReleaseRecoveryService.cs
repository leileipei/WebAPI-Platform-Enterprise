using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Releases;
public sealed class ReleaseRecoveryService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,ReleaseService releases,HistoricalSnapshotService history,PublishCoordinator publish)
{
    public async Task<ReleaseDto> RetryAsync(Guid failedId,ActorContext actor,CancellationToken ct=default)
    {
        var initial=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==failedId,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);
        return await commands.ExecuteAsync(actor,scope,"release.retry",async(_,token)=>{
            await auth.RequireAsync(actor,"release.publish",new("release",failedId,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"release.retry",requestContext.IdempotencyKey),CanonicalJson.Serialize(new {failedReleaseId=failedId}),async inner=>{
                await publish.LockEnvironmentAsync(initial.EnvironmentId,inner);var failed=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==failedId,inner);var env=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==failed.EnvironmentId,inner);
                if(failed.Status!="Failed"||failed.ToConfigVersion<=0||failed.DeploymentSequence!=env.DeploymentSequence||failed.ToConfigVersion!=env.DesiredConfigVersion) throw new ApiException(409,"stale_retry_target","失败记录已过时或尚未生成可重试快照。");
                if(await db.Set<ReleaseRecord>().AnyAsync(r=>r.EnvironmentId==env.Id&&(r.Status=="Building"||r.Status=="Publishing"),inner)) throw new ApiException(409,"environment_busy","环境已有正在下发的记录。");await publish.ValidateCohortAsync(env.Id,inner);var candidate=await history.CandidateAsync(env.Id,failed.ToConfigVersion,failed.ToConfigVersion,inner);
                var record=new ReleaseRecord {EnvironmentId=env.Id,ReleaseNo="RTY-"+Guid.NewGuid().ToString("N"),ReleaseType="retry",Status="Building",RequestedBy=actor.UserId,PublishRequestedBy=actor.UserId,PublishTraceId=actor.TraceId,RecoveryOf=failed.Id,BaselineConfigVersion=failed.ToConfigVersion,FromConfigVersion=failed.ToConfigVersion,ToConfigVersion=failed.ToConfigVersion,CandidateBytes=CanonicalJson.Serialize(candidate),ApprovalPolicy="[]"};db.Add(record);return await releases.DtoAsync(record,inner);
            },token);
        },ct);
    }
}

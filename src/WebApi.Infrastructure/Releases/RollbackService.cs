using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Gateway;
using WebApi.Domain.Policies;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Releases;
public sealed class RollbackService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,ReleaseService releases,HistoricalSnapshotService history,PublishSettings publishSettings,WebApi.Infrastructure.Delivery.PromotionExecutionService deliveryExecution)
{
    public async Task<ReleaseDto> CreateAsync(Guid originalId,long target,ActorContext actor,CancellationToken ct=default)
    {
        var original=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==originalId,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(original.EnvironmentId,ct);
        return await commands.ExecuteAsync(actor,scope,"release.rollback.create",async(_,token)=>{
            await auth.RequireAsync(actor,"release.rollback",new("release",originalId,scope),token);await auth.RequireAsync(actor,"gateway.config.read",new("environment",original.EnvironmentId,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"release.rollback.create",requestContext.IdempotencyKey),CanonicalJson.Serialize(new {originalId,targetConfigVersion=target}),async inner=>{
                await deliveryExecution.RequireRecoveryVisibilityAsync(original,actor,inner);var env=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==original.EnvironmentId,inner);
                if(original.Status is not ("Succeeded" or "Failed")||original.DeploymentSequence!=env.DeploymentSequence||original.ToConfigVersion!=env.DesiredConfigVersion||target<=0||target==env.DesiredConfigVersion) throw new ApiException(409,"stale_deployment","只能从当前已结束部署建立指向不同历史快照的回滚。");
                if(await db.Set<ReleaseRecord>().AnyAsync(r=>r.EnvironmentId==env.Id&&(r.Status=="Building"||r.Status=="Publishing"),inner)) throw new ApiException(409,"environment_busy","环境正在下发。");var candidate=await history.CandidateAsync(env.Id,target,env.DesiredConfigVersion!.Value,inner);
                if(candidate.Routes.Any(route=>route.EffectiveAuthenticationMode=="JWT")||candidate.Policies.Any(policy=>policy.Type is "retry" or "cache"||policy.Type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(policy.Config).Mode==WebApi.Contracts.Policies.AuthenticationMode.JWT))
                    await SnapshotSchemaCapabilities.RequireOnline22Async(db,publishSettings,env.Id,inner);
                var r=new ReleaseRecord {EnvironmentId=env.Id,ReleaseNo="RBK-"+Guid.NewGuid().ToString("N"),ReleaseType="rollback",Status="Draft",RequestedBy=actor.UserId,RollbackOf=original.Id,ArtifactId=original.ArtifactId,SourceReleaseId=original.SourceReleaseId,BaselineConfigVersion=env.DesiredConfigVersion.Value,FromConfigVersion=env.DesiredConfigVersion.Value,ToConfigVersion=target,CandidateBytes=CanonicalJson.Serialize(candidate)};db.Add(r);return await releases.DtoAsync(r,inner);
            },token);
        },ct);
    }
}

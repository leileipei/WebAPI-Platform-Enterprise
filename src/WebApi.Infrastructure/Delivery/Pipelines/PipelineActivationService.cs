using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery.Pipelines;
public sealed class PipelineActivationService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,
    AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,
    DeliveryLockCoordinator locks,PipelineDefinitionService definitions,RunningDeploymentReader running,ProjectDeliveryPolicyService policies)
{
    internal static async Task RequireIdleAsync(WebApiDbContext db,Guid projectId,CancellationToken ct)
    {
        if(await db.Set<ReleasePipelineRun>().AnyAsync(r=>r.ProjectId==projectId&&(r.Status=="Active"||r.Status=="Paused"||r.Status=="TimedOut"),ct))throw Busy();
        string[] activeReleases=["Draft","WaitingApproval","Ready","Building","Publishing"];
        if(await (from r in db.Set<ReleaseRecord>() join e in db.Set<EnvironmentRecord>() on r.EnvironmentId equals e.Id where e.ProjectId==projectId&&activeReleases.Contains(r.Status) select r.Id).AnyAsync(ct))throw Busy();
        string[] pendingPromotions=["Draft","WaitingApproval","Ready","Deploying","Verifying","DeploymentFailed","VerificationFailed"];
        string[] terminalRuns=["Completed","Cancelled","Invalidated"];
        // Terminal Pipeline history retains its actual deployment/verification facts.
        // Only a stage linked to its own terminal run leaves the independent queue;
        // the release guard above still blocks any deployment that is publishing.
        if(await db.Set<ReleasePromotion>().AnyAsync(p=>p.ProjectId==projectId&&pendingPromotions.Contains(p.Status)&&
            !(p.GateOrigin=="PipelineRunStage"&&db.Set<ReleasePipelineRunStage>().Any(s=>
                s.Id==p.PipelineRunStageId&&s.ProjectId==p.ProjectId&&db.Set<ReleasePipelineRun>().Any(r=>
                    r.Id==s.RunId&&r.ProjectId==p.ProjectId&&terminalRuns.Contains(r.Status)))),ct))throw Busy();
    }
    private async Task<ScopeRef> RequireManageAsync(Guid projectId,ActorContext actor,CancellationToken ct)
    {var scope=await scopes.ProjectAsync(projectId,ct);await auth.RequireAsync(actor,"pipeline.manage",new("project",projectId,scope),ct);await auth.RequireAsync(actor,"project.write",new("project",projectId,scope),ct);return scope;}
    public async Task<DeliveryPolicyDto> ActivateAsync(Guid projectId,ActivatePipelineRequest request,string? etag,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);
        return await commands.ExecuteAsync(actor,scope,"pipeline.policy.activate",async(_,token)=>{
            await RequireManageAsync(projectId,actor,token);await locks.LockProjectAsync(projectId,token);
            var version=await db.Set<ReleasePipelineVersion>().AsNoTracking().SingleOrDefaultAsync(v=>v.Id==request.PipelineVersionId&&v.ProjectId==projectId,token)??throw ScopeResolver.Missing();
            var content=PipelineDefinitionService.Content(version);var ids=content.Profiles.Select(p=>p.EnvironmentId).ToArray();await locks.LockEnvironmentsAsync(ids,token);
            await definitions.RequireEnvironmentReadsAsync(projectId,content.Definition,actor,token);
            foreach(var target in ids.Skip(1))await auth.RequireAsync(actor,"environment.write",new("environment",target,scope with{EnvironmentId=target}),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.policy.activate",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{projectId,request,etag}),async inner=>{
                var policy=await db.Set<ProjectDeliveryPolicy>().SingleOrDefaultAsync(p=>p.ProjectId==projectId,inner);RevisionTag.Require(etag,policy?.Revision??0);
                await RequireIdleAsync(db,projectId,inner);
                if(!await db.Set<ReleasePipeline>().AnyAsync(p=>p.Id==version.PipelineId&&p.ProjectId==projectId&&p.Status=="Active",inner))throw new ApiException(409,"pipeline_archived","已归档流水线不可激活。");
                var current=await definitions.FreezeAsync(projectId,content.Definition,actor,inner);
                if(PipelineDefinitionService.Hash(current)!=version.DefinitionHash)throw new ApiException(409,"pipeline_definition_stale","环境或审批模板已改变，请发布新的定义版本。");
                var production=content.Profiles[^1];var source=content.Profiles[^2];var env=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==production.EnvironmentId,inner);
                if(string.IsNullOrEmpty(env.GatewayPublicUrl))throw new ApiException(422,"production_entry_required","生产环境需要可用的Gateway对外访问地址。");
                await running.ReadAsync(env.Id,null,inner);
                if(policy?.Mode=="PipelineRequired"&&policy.ActivePipelineVersionId==version.Id)return ProjectDeliveryPolicyService.View(projectId,policy);
                if(policy is null){policy=new(){ProjectId=projectId,OrganizationId=scope.OrganizationId};db.Add(policy);}else policy.Revision++;
                policy.Mode="PipelineRequired";policy.ActivePipelineVersionId=version.Id;policy.SourceEnvironmentId=source.EnvironmentId;policy.TargetEnvironmentId=production.EnvironmentId;
                policy.RequiredTestTypes=source.RequiredTypes.ToArray();policy.VerificationValidityMinutes=source.EvidenceValidityMinutes;policy.UpdatedBy=actor.UserId;policy.UpdatedAt=DateTimeOffset.UtcNow;
                return ProjectDeliveryPolicyService.View(projectId,policy);
            },token);
        },ct);
    }
    public async Task<DeliveryPolicyDto> RestoreAsync(Guid projectId,RestoreDeliveryPolicyRequest request,string? etag,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);
        return await commands.ExecuteAsync(actor,scope,"pipeline.policy.restore",async(_,token)=>{
            await RequireManageAsync(projectId,actor,token);await locks.LockProjectAsync(projectId,token);
            var row=await db.Set<ProjectDeliveryPolicy>().SingleOrDefaultAsync(p=>p.ProjectId==projectId,token);
            var ids=await db.Set<EnvironmentRecord>().Where(e=>e.ProjectId==projectId).Select(e=>e.Id).ToArrayAsync(token);await locks.LockEnvironmentsAsync(ids,token);
            if(row?.ActivePipelineVersionId is Guid versionId){var version=await db.Set<ReleasePipelineVersion>().AsNoTracking().SingleAsync(v=>v.Id==versionId,token);var content=PipelineDefinitionService.Content(version);await definitions.RequireEnvironmentReadsAsync(projectId,content.Definition,actor,token);foreach(var target in content.Profiles.Skip(1))await auth.RequireAsync(actor,"environment.write",new("environment",target.EnvironmentId,scope with{EnvironmentId=target.EnvironmentId}),token);}
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.policy.restore",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{projectId,request,etag}),async inner=>{
                if(row?.Mode!="PipelineRequired")throw new ApiException(409,"pipeline_not_active","项目当前未启用流水线模式。");
                await RequireIdleAsync(db,projectId,inner);
                var result=await policies.SaveConnectionWithinTransactionAsync(projectId,request.Connection,etag,actor,inner,allowPipelineExit:true);return result.Value;
            },token);
        },ct);
    }
    private static ApiException Busy()=>new(409,"delivery_in_progress","项目存在未结束的流水线、正式发布、晋级或恢复，不能切换交付规则。");
}

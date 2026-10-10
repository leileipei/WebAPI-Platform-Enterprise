using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
public sealed class ProjectDeliveryPolicyService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,DeliveryLockCoordinator locks)
{
    private static readonly string[] testTypes=["InterfaceFunction","Integration","ContractCompatibility"];
    internal static DeliveryPolicyDto View(Guid projectId,ProjectDeliveryPolicy? row)=>row is null?new(null,projectId,null,null,"Legacy",testTypes,1440,0):new(row.Id,projectId,row.SourceEnvironmentId,row.TargetEnvironmentId,row.Mode,row.RequiredTestTypes,row.VerificationValidityMinutes,row.Revision,row.ActivePipelineVersionId);
    public async Task<DeliveryPolicyDto> GetAsync(Guid projectId,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);if(!await auth.CanAsync(actor,"project.read",new("project",projectId,scope),ct))throw ScopeResolver.Missing();
        var row=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==projectId,ct);
        if(row is not null)foreach(var env in new[]{row.SourceEnvironmentId,row.TargetEnvironmentId})if(!await auth.CanAsync(actor,"environment.read",new("environment",env,scope with{EnvironmentId=env}),ct))throw ScopeResolver.Missing();
        return View(projectId,row);
    }
    private static SaveDeliveryPolicyRequest Normalize(SaveDeliveryPolicyRequest request)
    {
        if(request.RequiredTestTypes is null||request.RequiredTestTypes.Count is <1 or >3||request.RequiredTestTypes.Distinct().Count()!=request.RequiredTestTypes.Count||request.RequiredTestTypes.Except(testTypes).Any()||request.Mode is not("Legacy" or "PromotionRequired")||request.VerificationValidityMinutes is <1 or >10080)throw Invalid();
        return request with{RequiredTestTypes=request.RequiredTestTypes.Order(StringComparer.Ordinal).ToArray()};
    }
    public async Task<CommandResult<DeliveryPolicyDto>> SaveAsync(Guid projectId,SaveDeliveryPolicyRequest request,string? etag,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);
        return await commands.ExecuteAsync(actor,scope,"delivery_policy.save",async(_,token)=>{
            await auth.RequireAsync(actor,"project.write",new("project",projectId,scope),token);await locks.LockProjectAsync(projectId,token);await locks.LockEnvironmentsAsync([request.SourceEnvironmentId,request.TargetEnvironmentId],token);
            if(await db.Set<ProjectDeliveryPolicy>().AnyAsync(p=>p.ProjectId==projectId&&p.Mode=="PipelineRequired",token))throw PipelineGuard();
            var normalized=Normalize(request);
            // Recheck original authorization even for a replayed command.
            await RequireConnectionAsync(projectId,normalized,actor,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"delivery_policy.save",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{projectId,request=normalized,etag}),inner=>SaveConnectionWithinTransactionAsync(projectId,normalized,etag,actor,inner),token);
        },ct);
    }
    private async Task<(EnvironmentRecord Source,EnvironmentRecord Target,ScopeRef Scope)> RequireConnectionAsync(Guid projectId,SaveDeliveryPolicyRequest request,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);await auth.RequireAsync(actor,"project.write",new("project",projectId,scope),ct);
        var source=await db.Set<EnvironmentRecord>().SingleOrDefaultAsync(x=>x.Id==request.SourceEnvironmentId&&x.ProjectId==projectId,ct);
        var target=await db.Set<EnvironmentRecord>().SingleOrDefaultAsync(x=>x.Id==request.TargetEnvironmentId&&x.ProjectId==projectId,ct);
        if(source is null||target is null||source.Id==target.Id||source.Status!="Active"||target.Status!="Active"||source.IsProduction||!target.IsProduction)throw Invalid();
        if(!await db.Set<Project>().AnyAsync(x=>x.Id==projectId&&x.Status=="Active",ct)||!await db.Set<Organization>().AnyAsync(x=>x.Id==scope.OrganizationId&&x.Status=="Active",ct))throw Invalid();
        await auth.RequireAsync(actor,"environment.read",new("environment",source.Id,scope with{EnvironmentId=source.Id}),ct);await auth.RequireAsync(actor,"environment.write",new("environment",target.Id,scope with{EnvironmentId=target.Id}),ct);return(source,target,scope);
    }
    internal async Task<CommandResult<DeliveryPolicyDto>> SaveConnectionWithinTransactionAsync(Guid projectId,SaveDeliveryPolicyRequest request,string? etag,ActorContext actor,CancellationToken ct,bool allowPipelineExit=false)
    {
        if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Policy mutation requires the owning transaction.");
        var normalized=Normalize(request);var connection=await RequireConnectionAsync(projectId,normalized,actor,ct);var target=connection.Target;
        var row=await db.Set<ProjectDeliveryPolicy>().SingleOrDefaultAsync(x=>x.ProjectId==projectId,ct);RevisionTag.Require(etag,row?.Revision??0);if(row?.Mode=="PipelineRequired"&&!allowPipelineExit)throw PipelineGuard();
        if(request.Mode=="PromotionRequired"){
            if(string.IsNullOrEmpty(target.GatewayPublicUrl)||target.ReleasePolicyId is not Guid flow||!await db.Set<ApprovalFlow>().AnyAsync(x=>x.Id==flow&&x.OrganizationId==connection.Scope.OrganizationId&&x.Enabled,ct))throw Invalid();
            var steps=await db.Set<ApprovalStep>().Where(x=>x.FlowId==target.ReleasePolicyId).OrderBy(x=>x.StepOrder).ToArrayAsync(ct);
            if(steps.Length!=2||steps[0].StepOrder!=1||steps[1].StepOrder!=2||steps.Any(x=>x.RequiredCount is <1 or >5))throw Invalid();
            if(row?.Mode!="PromotionRequired"&&await db.Set<ReleaseRecord>().AnyAsync(x=>x.EnvironmentId==target.Id&&(x.Status=="WaitingApproval"||x.Status=="Ready"||x.Status=="Building"||x.Status=="Publishing"),ct))throw new ApiException(409,"release_in_progress","目标环境存在进行中的发布，暂不能启用晋级门禁。");
        }
        if(row is null){row=new(){ProjectId=projectId,OrganizationId=connection.Scope.OrganizationId};db.Add(row);}else row.Revision++;
        row.SourceEnvironmentId=connection.Source.Id;row.TargetEnvironmentId=target.Id;row.Mode=normalized.Mode;row.ActivePipelineVersionId=null;row.RequiredTestTypes=normalized.RequiredTestTypes.ToArray();row.VerificationValidityMinutes=normalized.VerificationValidityMinutes;row.UpdatedBy=actor.UserId;row.UpdatedAt=DateTimeOffset.UtcNow;
        return new(View(projectId,row),RevisionTag.Format(row.Revision));
    }
    private static ApiException PipelineGuard()=>new(409,"pipeline_mode_requires_explicit_restore","流水线模式须通过显式规则恢复命令停用。");
    private static ApiException Invalid()=>new(422,"invalid_delivery_policy","需选择本项目启用的非生产来源和生产目标，配置有效测试类型、有效期及生产入口与两级审批。");
}

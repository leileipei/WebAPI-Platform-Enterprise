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
public sealed class ProjectDeliveryPolicyService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext)
{
    private static readonly string[] testTypes=["InterfaceFunction","Integration","ContractCompatibility"];
    internal static DeliveryPolicyDto View(Guid projectId,ProjectDeliveryPolicy? row)=>row is null?new(null,projectId,null,null,"Legacy",testTypes,1440,0):new(row.Id,projectId,row.SourceEnvironmentId,row.TargetEnvironmentId,row.Mode,row.RequiredTestTypes,row.VerificationValidityMinutes,row.Revision);
    public async Task<DeliveryPolicyDto> GetAsync(Guid projectId,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);if(!await auth.CanAsync(actor,"project.read",new("project",projectId,scope),ct))throw ScopeResolver.Missing();
        var row=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(x=>x.ProjectId==projectId,ct);
        if(row is not null)foreach(var env in new[]{row.SourceEnvironmentId,row.TargetEnvironmentId})if(!await auth.CanAsync(actor,"environment.read",new("environment",env,scope with{EnvironmentId=env}),ct))throw ScopeResolver.Missing();
        return View(projectId,row);
    }
    public async Task<CommandResult<DeliveryPolicyDto>> SaveAsync(Guid projectId,SaveDeliveryPolicyRequest request,string? etag,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);
        return await commands.ExecuteAsync(actor,scope,"delivery_policy.save",async(_,token)=>{
            await auth.RequireAsync(actor,"project.write",new("project",projectId,scope),token);
            var source=await db.Set<EnvironmentRecord>().SingleOrDefaultAsync(x=>x.Id==request.SourceEnvironmentId&&x.ProjectId==projectId,token);
            var target=await db.Set<EnvironmentRecord>().SingleOrDefaultAsync(x=>x.Id==request.TargetEnvironmentId&&x.ProjectId==projectId,token);
            if(source is null||target is null||source.Id==target.Id||source.Status!="Active"||target.Status!="Active"||source.IsProduction||!target.IsProduction||request.Mode is not("Legacy" or "PromotionRequired")||request.VerificationValidityMinutes is <1 or >10080||request.RequiredTestTypes.Count is <1 or >3||request.RequiredTestTypes.Distinct().Count()!=request.RequiredTestTypes.Count||request.RequiredTestTypes.Except(testTypes).Any())throw Invalid();
            if(!await db.Set<Project>().AnyAsync(x=>x.Id==projectId&&x.Status=="Active",token)||!await db.Set<Organization>().AnyAsync(x=>x.Id==scope.OrganizationId&&x.Status=="Active",token))throw Invalid();
            await auth.RequireAsync(actor,"environment.read",new("environment",source.Id,scope with{EnvironmentId=source.Id}),token);
            await auth.RequireAsync(actor,"environment.write",new("environment",target.Id,scope with{EnvironmentId=target.Id}),token);
            var normalized=request with{RequiredTestTypes=request.RequiredTestTypes.Order(StringComparer.Ordinal).ToArray()};
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"delivery_policy.save",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{projectId,request=normalized,etag}),async commandToken=>{
                var row=await db.Set<ProjectDeliveryPolicy>().SingleOrDefaultAsync(x=>x.ProjectId==projectId,commandToken);RevisionTag.Require(etag,row?.Revision??0);
                if(request.Mode=="PromotionRequired"){
                    if(string.IsNullOrEmpty(target.GatewayPublicUrl)||target.ReleasePolicyId is not Guid flow||!await db.Set<ApprovalFlow>().AnyAsync(x=>x.Id==flow&&x.OrganizationId==scope.OrganizationId&&x.Enabled,commandToken))throw Invalid();
                    var steps=await db.Set<ApprovalStep>().Where(x=>x.FlowId==target.ReleasePolicyId).OrderBy(x=>x.StepOrder).ToArrayAsync(commandToken);
                    if(steps.Length!=2||steps[0].StepOrder!=1||steps[1].StepOrder!=2||steps.Any(x=>x.RequiredCount is <1 or >5))throw Invalid();
                    if(row?.Mode!="PromotionRequired"&&await db.Set<ReleaseRecord>().AnyAsync(x=>x.EnvironmentId==target.Id&&(x.Status=="WaitingApproval"||x.Status=="Ready"||x.Status=="Building"||x.Status=="Publishing"),commandToken))throw new ApiException(409,"release_in_progress","目标环境存在进行中的发布，暂不能启用晋级门禁。");
                }
                if(row is null){row=new(){ProjectId=projectId,OrganizationId=scope.OrganizationId};db.Add(row);}else row.Revision++;
                row.SourceEnvironmentId=source.Id;row.TargetEnvironmentId=target.Id;row.Mode=request.Mode;row.RequiredTestTypes=normalized.RequiredTestTypes.ToArray();row.VerificationValidityMinutes=request.VerificationValidityMinutes;row.UpdatedBy=actor.UserId;row.UpdatedAt=DateTimeOffset.UtcNow;
                return new CommandResult<DeliveryPolicyDto>(View(projectId,row),RevisionTag.Format(row.Revision));
            },token);
        },ct);
    }
    private static ApiException Invalid()=>new(422,"invalid_delivery_policy","需选择本项目启用的非生产来源和生产目标，配置有效测试类型、有效期及生产入口与两级审批。");
}

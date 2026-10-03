using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Governance;
namespace WebApi.Infrastructure.Releases;
public sealed class ApprovalFlowService(WebApiDbContext db,AuthorizationService auth,AuditedCommandExecutor commands)
{
    private async Task<ApprovalFlowDto> DtoAsync(ApprovalFlow f,CancellationToken ct)=>new(f.Id,f.OrganizationId,f.Name,f.Enabled,f.Revision,await db.Set<ApprovalStep>().Where(s=>s.FlowId==f.Id).OrderBy(s=>s.StepOrder).Select(s=>new ApprovalRule(s.StepOrder,s.RoleCode,s.RequiredCount)).ToArrayAsync(ct));
    public async Task<IReadOnlyList<ApprovalFlowDto>> ListAsync(Guid orgId,ActorContext actor,CancellationToken ct=default)
    {await auth.RequireAsync(actor,"environment.write",new("organization",orgId,new(orgId)),ct);var result=new List<ApprovalFlowDto>();foreach(var f in await db.Set<ApprovalFlow>().AsNoTracking().Where(f=>f.OrganizationId==orgId).OrderBy(f=>f.Name).ToArrayAsync(ct)) result.Add(await DtoAsync(f,ct));return result;}
    public async Task<CommandResult<ApprovalFlowDto>> SaveAsync(Guid orgId,Guid? id,SaveApprovalFlowRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {
        return await commands.ExecuteAsync(actor,new(orgId),"approval_flow.save",async(_,token)=>{
            await auth.RequireAsync(actor,"environment.write",new("organization",orgId,new(orgId)),token);
            var rules=request.Steps.OrderBy(s=>s.StepOrder).ToArray();if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>128||rules.Length!=2||rules[0].StepOrder!=1||rules[1].StepOrder!=2||rules.Any(s=>s.RequiredCount is <1 or >5||string.IsNullOrWhiteSpace(s.RoleCode))) throw new ApiException(422,"invalid_approval_policy","需配置顺序1/2的两级审批，每级1到5人。");
            foreach(var rule in rules) if(!await(from r in db.Set<Role>() join rp in db.Set<RolePermission>() on r.Id equals rp.RoleId join p in db.Set<Permission>() on rp.PermissionId equals p.Id where (r.OrganizationId==null||r.OrganizationId==orgId)&&r.Code==rule.RoleCode&&p.Code=="approval.act" select r.Id).AnyAsync(token)) throw new ApiException(422,"invalid_approval_role","审批角色不存在或没有approval.act权限。");
            var f=id is Guid existing?await db.Set<ApprovalFlow>().SingleOrDefaultAsync(f=>f.Id==existing&&f.OrganizationId==orgId,token)??throw ScopeResolver.Missing():new ApprovalFlow {OrganizationId=orgId,ScopeType="environment"};if(id is null) db.Add(f);else {RevisionTag.Require(tag,f.Revision);f.Revision++;}
            f.Name=request.Name;f.Enabled=request.Enabled;var old=await db.Set<ApprovalStep>().Where(s=>s.FlowId==f.Id).ToArrayAsync(token);
            foreach(var rule in rules) {var step=old.SingleOrDefault(s=>s.StepOrder==rule.StepOrder);if(step is null) {step=new ApprovalStep {FlowId=f.Id,StepOrder=rule.StepOrder};db.Add(step);}step.RoleCode=rule.RoleCode;step.RequiredCount=rule.RequiredCount;}
            db.RemoveRange(old.Where(s=>s.StepOrder is not (1 or 2)));return new CommandResult<ApprovalFlowDto>(new(f.Id,f.OrganizationId,f.Name,f.Enabled,f.Revision,rules),RevisionTag.Format(f.Revision));
        },ct);
    }
}

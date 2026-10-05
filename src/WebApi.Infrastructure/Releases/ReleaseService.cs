using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Contracts.Applications;
using WebApi.Domain.Releases;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Releases;
public sealed class ReleaseService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,ReleaseCandidateBuilder candidates)
{
    public async Task<ScopeRef> ReadScopeAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {var r=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(r.EnvironmentId,ct);if(!await auth.CanAsync(actor,"release.read",new("release",id,scope),ct)) throw ScopeResolver.Missing();return scope;}
    public async Task<ReleaseDto> DtoAsync(ReleaseRecord r,CancellationToken ct)
    {
        FrozenCandidateView? view=null;string? hash=null;
        if(r.CandidateBytes is not null&&r.ApprovalPolicy is not null)
        {var c=JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes,CanonicalJson.Options)!;view=SafeView(c);hash=Convert.ToHexStringLower(SHA256.HashData(r.CandidateBytes));}
        var rules=r.ApprovalPolicy is null?Array.Empty<ApprovalRule>():JsonSerializer.Deserialize<ApprovalRule[]>(r.ApprovalPolicy,CanonicalJson.Options)!;
        var tasks=await db.Set<ApprovalTask>().Where(t=>t.ReleaseId==r.Id).OrderBy(t=>t.StepOrder).ThenBy(t=>t.Id).ToArrayAsync(ct);
        var targetEntities=await db.Set<ReleaseTarget>().AsNoTracking().Where(t=>t.ReleaseId==r.Id).ToArrayAsync(ct);var acks=await db.Set<GatewayAck>().AsNoTracking().Where(a=>a.ReleaseId==r.Id).ToArrayAsync(ct);
        return new(r.Id,r.EnvironmentId,r.ReleaseNo,r.Status,r.ReleaseType,r.RequestedBy,r.CreatedAt,r.BaselineConfigVersion,r.ToConfigVersion,r.DeploymentSequence,r.RollbackOf,view,hash,tasks.Select(t=>new ApprovalTaskDto(t.Id,t.StepOrder,rules.Single(x=>x.StepOrder==t.StepOrder).RoleCode,t.Status,t.AssigneeUserId,t.Comment,t.ActedAt)).ToArray(),targetEntities.Select(t=>{var ack=acks.SingleOrDefault(a=>a.NodeId==t.NodeId);return new ReleaseTargetDto(t.NodeId,t.InstanceId,ack?.Success==true,ack?.ConfigVersion,ack?.DeploymentSequence,ack?.ErrorCode);}).ToArray(),r.FailureCode is null?Array.Empty<string>():[r.FailureCode],r.RecoveryOf);
    }
    private static FrozenCandidateView SafeView(FrozenReleaseCandidate c)=>new(c.Versions,c.Routes,c.Clusters,c.Applications.Select(a=>new FrozenApplicationView(a.Application,a.Credentials.Select(k=>new CredentialDto(k.Id,a.Application.Id,k.AccessKey,k.SecretLast4,k.Status,k.ValidFrom,k.ExpiresAt,null,null,k.Revision)).ToArray(),a.Permissions)).ToArray(),c.ResourceRevisions,c.Policies,c.Bindings);
    public async Task<FrozenCandidateView> PreviewAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await ReadScopeAsync(id,actor,ct);
        return await commands.ExecuteAsync(actor,scope,"release.preview",async(_,token)=>{
            await auth.RequireAsync(actor,"release.read",new("release",id,scope),token);var r=await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==id,token);
            if(r.CandidateBytes is null) throw new ApiException(409,"candidate_unavailable","发布候选不可用。");
            if(r.ReleaseType=="rollback"||r.ApprovalPolicy is not null) return SafeView(JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes,CanonicalJson.Options)!);
            var stored=JsonSerializer.Deserialize<CreateReleaseRequest>(r.CandidateBytes,CanonicalJson.Options)!;
            var baseline=await db.Set<EnvironmentRecord>().Where(e=>e.Id==r.EnvironmentId).Select(e=>e.DesiredConfigVersion??0).SingleAsync(token);
            var candidate=await candidates.PreviewAsync(r.EnvironmentId,new(baseline,stored.VersionIds),token);
            return SafeView(candidate) with {PreconditionsCurrent=stored.BaseConfigVersion==baseline&&ReleaseCandidateBuilder.RevisionsCurrent(stored.ResourceRevisions,candidate.ResourceRevisions)};
        },ct);
    }
    public async Task<FrozenCandidateView> PreviewSelectionAsync(Guid environmentId,PreviewReleaseRequest request,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.EnvironmentAsync(environmentId,ct);
        return await commands.ExecuteAsync(actor,scope,"release.selection.preview",async(_,token)=>{
            await auth.RequireAsync(actor,"release.read",new("environment",environmentId,scope),token);
            return SafeView(await candidates.PreviewAsync(environmentId,request,token)) with {PreconditionsCurrent=true};
        },ct);
    }
    public Task<ReleaseDto> RefreshPreconditionsAsync(Guid id,RefreshReleasePreconditionsRequest request,ActorContext actor,CancellationToken ct=default)=>RunCommandAsync(id,actor,"release.refresh-preconditions","release.create",request,async(r,_,token)=>{
        ReleaseStateMachine.Require(r.Status,"Draft");if(r.RequestedBy!=actor.UserId) throw new ApiException(403,"not_applicant","仅申请人可刷新草稿。");
        if(r.ReleaseType!="publish"||r.ApprovalPolicy is not null) throw new ApiException(409,"frozen_candidate","该候选不能刷新工作修订。");
        var stored=JsonSerializer.Deserialize<CreateReleaseRequest>(r.CandidateBytes!,CanonicalJson.Options)!;
        var refreshed=stored with {ResourceRevisions=request.ResourceRevisions};await candidates.BuildAsync(r.EnvironmentId,refreshed,token);
        r.CandidateBytes=CanonicalJson.Serialize(refreshed);return await DtoAsync(r,token);
    },ct);
    public async Task<ReleaseDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct=default) {await ReadScopeAsync(id,actor,ct);return await DtoAsync(await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==id,ct),ct);}
    public async Task<PageResult<ReleaseDto>> ListAsync(Guid envId,ActorContext actor,int page,int size,CancellationToken ct=default)
    {var scope=await scopes.EnvironmentAsync(envId,ct);if(!await auth.CanAsync(actor,"release.read",new("environment",envId,scope),ct)) throw ScopeResolver.Missing();var rows=await db.Set<ReleaseRecord>().AsNoTracking().Where(r=>r.EnvironmentId==envId).OrderByDescending(r=>r.CreatedAt).ToArrayAsync(ct);var slice=Pagination.Slice(rows,page,size);var items=new List<ReleaseDto>();foreach(var r in slice.Items) items.Add(await DtoAsync(r,ct));return new(items,slice.Total,slice.Page,slice.PageSize);}
    private async Task<ReleaseDto> RunCommandAsync(Guid id,ActorContext actor,string operation,string permission,object normalized,Func<ReleaseRecord,ScopeRef,CancellationToken,Task<ReleaseDto>> command,CancellationToken ct)
    {
        var original=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(original.EnvironmentId,ct);
        return await commands.ExecuteAsync(actor,scope,operation,async(_,token)=>{
            await auth.RequireAsync(actor,permission,new("release",id,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,operation,requestContext.IdempotencyKey),CanonicalJson.Serialize(new {releaseId=id,request=normalized}),async inner=>await command(await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id,inner),scope,inner),token);
        },ct);
    }
    public async Task<ReleaseDto> CreateAsync(Guid environmentId,CreateReleaseRequest request,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await scopes.EnvironmentAsync(environmentId,ct);return await commands.ExecuteAsync(actor,scope,"release.create",async(_,token)=>{
            await auth.RequireAsync(actor,"release.create",new("environment",environmentId,scope),token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"release.create",requestContext.IdempotencyKey),CanonicalJson.Serialize(request),async inner=>{
                var initial=await candidates.BuildAsync(environmentId,request,inner);var storedRequest=request with {ResourceRevisions=initial.ResourceRevisions};var release=new ReleaseRecord {EnvironmentId=environmentId,ReleaseNo="REL-"+Guid.NewGuid().ToString("N"),ReleaseType="publish",Status="Draft",RequestedBy=actor.UserId,BaselineConfigVersion=request.BaseConfigVersion,FromConfigVersion=request.BaseConfigVersion,CandidateBytes=CanonicalJson.Serialize(storedRequest)};db.Add(release);return await DtoAsync(release,inner);
            },token);
        },ct);
    }
    public Task<ReleaseDto> SubmitAsync(Guid id,ActorContext actor,CancellationToken ct=default)=>RunCommandAsync(id,actor,"release.submit","release.create",new {},async(r,scope,token)=>{
        ReleaseStateMachine.Require(r.Status,"Draft");if(r.RequestedBy!=actor.UserId) throw new ApiException(403,"not_applicant","仅申请人可提交。");
        var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==r.EnvironmentId,token);FrozenReleaseCandidate candidate;
        if(r.ReleaseType=="rollback") {if((env.DesiredConfigVersion??0)!=r.BaselineConfigVersion) throw new ApiException(409,"stale_baseline","回滚基线已变化。");candidate=JsonSerializer.Deserialize<FrozenReleaseCandidate>(r.CandidateBytes!,CanonicalJson.Options)!;}
        else {var input=JsonSerializer.Deserialize<CreateReleaseRequest>(r.CandidateBytes!,CanonicalJson.Options)!;candidate=await candidates.BuildAsync(r.EnvironmentId,input,token);}
        var rules=Array.Empty<ApprovalRule>();if(env.IsProduction)
        {
            if(env.ReleasePolicyId is not Guid flowId) throw new ApiException(422,"approval_policy_required","生产环境必须配置两级审批流程。");var flow=await db.Set<ApprovalFlow>().AsNoTracking().SingleOrDefaultAsync(f=>f.Id==flowId&&f.OrganizationId==scope.OrganizationId&&f.Enabled,token)??throw new ApiException(422,"invalid_approval_policy","生产审批流程不可用。");
            rules=await db.Set<ApprovalStep>().Where(s=>s.FlowId==flowId).OrderBy(s=>s.StepOrder).Select(s=>new ApprovalRule(s.StepOrder,s.RoleCode,s.RequiredCount)).ToArrayAsync(token);ValidateRules(rules);
            foreach(var rule in rules) for(var n=0;n<rule.RequiredCount;n++) db.Add(new ApprovalTask {FlowId=flow.Id,ReleaseId=r.Id,StepOrder=rule.StepOrder,Status="Pending"});r.Status="WaitingApproval";
        }else r.Status="Ready";
        r.CandidateBytes=CanonicalJson.Serialize(candidate);r.ApprovalPolicy=JsonSerializer.Serialize(rules,CanonicalJson.Options);
        foreach(var version in candidate.Versions) db.Add(new ReleaseItem {ReleaseId=r.Id,ResourceType="version",ResourceId=version.Version.Id,ChangeType=version.Version.ChangeType,AfterJson=JsonSerializer.Serialize(new {version=version.Version.Version,revision=version.Version.Revision},CanonicalJson.Options)});
        // Return pending tasks from tracked entries as well as committed records.
        return await DtoWithPendingAsync(r,rules,token);
    },ct);
    private async Task<ReleaseDto> DtoWithPendingAsync(ReleaseRecord r,IReadOnlyList<ApprovalRule> rules,CancellationToken ct)
    {var dto=await DtoAsync(r,ct);var added=db.ChangeTracker.Entries<ApprovalTask>().Where(e=>e.State==EntityState.Added&&e.Entity.ReleaseId==r.Id).Select(e=>e.Entity).ToArray();return dto with {ApprovalSteps=dto.ApprovalSteps.Concat(added.Select(t=>new ApprovalTaskDto(t.Id,t.StepOrder,rules.Single(s=>s.StepOrder==t.StepOrder).RoleCode,t.Status,t.AssigneeUserId,t.Comment,t.ActedAt))).OrderBy(t=>t.StepOrder).ThenBy(t=>t.Id).ToArray()};}
    private static void ValidateRules(IReadOnlyList<ApprovalRule> rules) {if(rules.Count!=2||rules[0].StepOrder!=1||rules[1].StepOrder!=2||rules.Any(r=>r.RequiredCount is <1 or >5||string.IsNullOrWhiteSpace(r.RoleCode))) throw new ApiException(422,"invalid_approval_policy","首期生产审批需要顺序1和2两级，每级1到5名独立审核人。");}
    private async Task<ApprovalTask> CurrentTaskAsync(ReleaseRecord r,ScopeRef scope,ActorContext actor,CancellationToken ct)
    {
        ReleaseStateMachine.Require(r.Status,"WaitingApproval");if(r.RequestedBy==actor.UserId) throw new ApiException(403,"self_approval","申请人不能审批自己的申请。");var tasks=await db.Set<ApprovalTask>().Where(t=>t.ReleaseId==r.Id).OrderBy(t=>t.StepOrder).ThenBy(t=>t.Id).ToArrayAsync(ct);
        if(tasks.Any(t=>t.Status=="Approved"&&t.AssigneeUserId==actor.UserId)) throw new ApiException(403,"independent_approval_required","同一人不能再次占用审批步骤。");var task=tasks.First(t=>t.Status=="Pending");var rule=JsonSerializer.Deserialize<ApprovalRule[]>(r.ApprovalPolicy!,CanonicalJson.Options)!.Single(s=>s.StepOrder==task.StepOrder);
        var eligible=await(from ur in db.Set<UserRole>() join role in db.Set<Role>() on ur.RoleId equals role.Id where ur.UserId==actor.UserId&&(role.OrganizationId==null||role.OrganizationId==scope.OrganizationId)&&(role.Code==rule.RoleCode||role.Code=="PlatformAdmin"&&role.IsSystem&&role.OrganizationId==null) select role.Id).AnyAsync(ct);
        if(!eligible) throw new ApiException(403,"approval_role_required","用户不属于当前审批角色。");return task;
    }
    public Task<ReleaseDto> ApproveAsync(Guid id,string comment,ActorContext actor,CancellationToken ct=default)=>RunCommandAsync(id,actor,"release.approve","approval.act",new {comment},async(r,scope,token)=>{
        if(comment.Length>10000) throw new ApiException(422,"comment_too_long","意见过长。");var task=await CurrentTaskAsync(r,scope,actor,token);task.Status="Approved";task.AssigneeUserId=actor.UserId;task.Comment=comment;task.ActedAt=DateTimeOffset.UtcNow;
        var all=await db.Set<ApprovalTask>().Where(t=>t.ReleaseId==id).ToArrayAsync(token);if(all.All(t=>t.Status=="Approved")) {r.Status="Ready";r.ApprovedBy=actor.UserId;}return await DtoAsync(r,token);
    },ct);
    public Task<ReleaseDto> RejectAsync(Guid id,string comment,ActorContext actor,CancellationToken ct=default)=>RunCommandAsync(id,actor,"release.reject","approval.act",new {comment},async(r,scope,token)=>{if(comment.Length>10000) throw new ApiException(422,"comment_too_long","意见过长。");var task=await CurrentTaskAsync(r,scope,actor,token);task.Status="Rejected";task.AssigneeUserId=actor.UserId;task.Comment=comment;task.ActedAt=DateTimeOffset.UtcNow;r.Status="Rejected";r.CompletedAt=DateTimeOffset.UtcNow;return await DtoAsync(r,token);},ct);
    public Task<ReleaseDto> CancelAsync(Guid id,ActorContext actor,CancellationToken ct=default)=>RunCommandAsync(id,actor,"release.cancel","release.create",new {},async(r,_,token)=>{ReleaseStateMachine.Require(r.Status,"Draft","WaitingApproval","Ready");if(r.RequestedBy!=actor.UserId) throw new ApiException(403,"not_applicant","仅申请人可取消。");r.Status="Cancelled";r.CompletedAt=DateTimeOffset.UtcNow;return await DtoAsync(r,token);},ct);
}

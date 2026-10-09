using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Delivery.Pipelines;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery.Pipelines;
public sealed class PipelineDefinitionService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,
    AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,DeliveryLockCoordinator locks)
{
    internal static string Json<T>(T value)=>JsonSerializer.Serialize(value,CanonicalJson.Options);
    internal static string Hash<T>(T value)=>Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(value)));
    internal static PipelineVersionContent Content(ReleasePipelineVersion version)=>JsonSerializer.Deserialize<PipelineVersionContent>(version.ContentJson,CanonicalJson.Options)??throw Invalid();
    internal static PipelineDefinition Draft(ReleasePipeline pipeline)=>JsonSerializer.Deserialize<PipelineDefinition>(pipeline.DraftJson,CanonicalJson.Options)??throw Invalid();
    internal static PipelineVersionDto View(ReleasePipelineVersion row)=>new(row.Id,row.PipelineId,row.VersionNo,row.DefinitionHash,Content(row),row.CreatedAt,row.CreatedBy);
    private static PipelineDto View(ReleasePipeline row,ReleasePipelineVersion? latest)=>new(row.Id,row.ProjectId,row.Name,row.Description,row.Revision,row.Status,"Full",Draft(row),latest is null?null:View(latest));
    private async Task RequireManageAsync(Guid projectId,ActorContext actor,CancellationToken ct)
    {var scope=await scopes.ProjectAsync(projectId,ct);await auth.RequireAsync(actor,"pipeline.manage",new("project",projectId,scope),ct);await auth.RequireAsync(actor,"project.write",new("project",projectId,scope),ct);}
    internal async Task RequireEnvironmentReadsAsync(Guid projectId,PipelineDefinition definition,ActorContext actor,CancellationToken ct,bool hide=false)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);
        foreach(var id in definition.Stages.Select(s=>s.EnvironmentId).Distinct())
        {
            if(hide){if(!await auth.CanAsync(actor,"environment.read",new("environment",id,scope with{EnvironmentId=id}),ct))throw ScopeResolver.Missing();}
            else await auth.RequireAsync(actor,"environment.read",new("environment",id,scope with{EnvironmentId=id}),ct);
        }
    }
    internal async Task<PipelineVersionContent> FreezeAsync(Guid projectId,PipelineDefinition request,ActorContext actor,CancellationToken ct)
    {
        if(request?.Stages is null)throw Invalid();
        var ids=request.Stages.Where(s=>s is not null).Select(s=>s.EnvironmentId).Distinct().ToArray();
        var environments=await db.Set<EnvironmentRecord>().AsNoTracking().Where(e=>ids.Contains(e.Id)&&e.ProjectId==projectId).ToArrayAsync(ct);
        try{PipelineDefinitionRules.Validate(request,environments.Select(e=>new PipelineEnvironmentFact(e.Id,e.ProjectId,e.Status=="Active",e.IsProduction)).ToArray());}
        catch(ArgumentException){throw Invalid();}
        await RequireEnvironmentReadsAsync(projectId,request,actor,ct);
        var definition=request with{Name=request.Name.Trim(),Stages=request.Stages.Select(s=>s with{RequiredTestTypes=s.RequiredTestTypes.Order(StringComparer.Ordinal).ToArray()}).ToArray()};
        var organization=(await scopes.ProjectAsync(projectId,ct)).OrganizationId;var profiles=new List<PipelineStageProfile>();
        foreach(var stage in definition.Stages)
        {
            var environment=environments.Single(e=>e.Id==stage.EnvironmentId);PipelineApprovalProfile? approval=null;
            if(environment.IsProduction&&environment.ReleasePolicyId!=stage.ApprovalFlowId)throw Invalid();
            if(stage.ApprovalFlowId is Guid flowId)
            {
                var flow=await db.Set<ApprovalFlow>().AsNoTracking().SingleOrDefaultAsync(f=>f.Id==flowId&&f.OrganizationId==organization&&f.Enabled,ct)??throw Invalid();
                var rules=await db.Set<ApprovalStep>().Where(s=>s.FlowId==flowId).OrderBy(s=>s.StepOrder).Select(s=>new ApprovalRule(s.StepOrder,s.RoleCode,s.RequiredCount)).ToArrayAsync(ct);
                if(rules.Length!=2||rules[0].StepOrder!=1||rules[1].StepOrder!=2||rules.Any(r=>r.RequiredCount is <1 or >5||string.IsNullOrWhiteSpace(r.RoleCode)))throw Invalid();
                approval=new(flowId,flow.Revision,rules);
            }
            profiles.Add(new(stage.Order,environment.Id,environment.IsProduction,stage.RequiredTestTypes,stage.EvidenceValidityMinutes,stage.WaitTimeoutMinutes,approval));
        }
        return new(definition,profiles);
    }
    public async Task<PipelineDto> CreateAsync(Guid projectId,PipelineDefinition request,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);
        return await commands.ExecuteAsync(actor,scope,"pipeline.draft.create",async(_,token)=>{
            await RequireManageAsync(projectId,actor,token);await locks.LockProjectAsync(projectId,token);
            if(request?.Stages is null)throw Invalid();await locks.LockEnvironmentsAsync(request.Stages.Where(s=>s is not null).Select(s=>s.EnvironmentId).ToArray(),token);
            var frozen=await FreezeAsync(projectId,request,actor,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.draft.create",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{projectId,definition=frozen.Definition}),_=>{
                var row=new ReleasePipeline{ProjectId=projectId,OrganizationId=scope.OrganizationId,Name=frozen.Definition.Name,Description=frozen.Definition.Description,DraftJson=Json(frozen.Definition),CreatedBy=actor.UserId,UpdatedBy=actor.UserId};db.Add(row);return Task.FromResult(View(row,null));
            },token);
        },ct);
    }
    public async Task<PipelineDto> SaveAsync(Guid id,PipelineDefinition request,string? etag,ActorContext actor,CancellationToken ct)
    {
        var initial=await RowAsync(id,ct);var scope=await scopes.ProjectAsync(initial.ProjectId,ct);
        return await commands.ExecuteAsync(actor,scope,"pipeline.draft.save",async(_,token)=>{
            await RequireManageAsync(initial.ProjectId,actor,token);await locks.LockProjectAsync(initial.ProjectId,token);
            if(request?.Stages is null)throw Invalid();await locks.LockEnvironmentsAsync(request.Stages.Where(s=>s is not null).Select(s=>s.EnvironmentId).ToArray(),token);
            var frozen=await FreezeAsync(initial.ProjectId,request,actor,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.draft.save",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,definition=frozen.Definition,etag}),async inner=>{
                var row=await db.Set<ReleasePipeline>().SingleAsync(p=>p.Id==id,inner);RevisionTag.Require(etag,row.Revision);RequireActive(row);
                row.Name=frozen.Definition.Name;row.Description=frozen.Definition.Description;row.DraftJson=Json(frozen.Definition);row.Revision++;row.UpdatedBy=actor.UserId;row.UpdatedAt=DateTimeOffset.UtcNow;
                return View(row,await LatestAsync(id,inner));
            },token);
        },ct);
    }
    public async Task<PipelineVersionDto> PublishVersionAsync(Guid id,string? etag,ActorContext actor,CancellationToken ct)
    {
        var initial=await RowAsync(id,ct);var scope=await scopes.ProjectAsync(initial.ProjectId,ct);
        return await commands.ExecuteAsync(actor,scope,"pipeline.version.publish",async(_,token)=>{
            await RequireManageAsync(initial.ProjectId,actor,token);await locks.LockProjectAsync(initial.ProjectId,token);
            var row=await db.Set<ReleasePipeline>().SingleAsync(p=>p.Id==id,token);await locks.LockEnvironmentsAsync(Draft(row).Stages.Select(s=>s.EnvironmentId).ToArray(),token);
            await RequireEnvironmentReadsAsync(initial.ProjectId,Draft(row),actor,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.version.publish",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,etag}),async inner=>{
                RevisionTag.Require(etag,row.Revision);RequireActive(row);var content=await FreezeAsync(row.ProjectId,Draft(row),actor,inner);
                var number=(await db.Set<ReleasePipelineVersion>().Where(v=>v.PipelineId==id).Select(v=>(int?)v.VersionNo).MaxAsync(inner)??0)+1;
                var version=new ReleasePipelineVersion{PipelineId=id,ProjectId=row.ProjectId,VersionNo=number,ContentJson=Json(content),DefinitionHash=Hash(content),CreatedBy=actor.UserId};db.Add(version);row.Revision++;row.UpdatedAt=DateTimeOffset.UtcNow;row.UpdatedBy=actor.UserId;return View(version);
            },token);
        },ct);
    }
    public async Task<PipelineDto> ArchiveAsync(Guid id,string? etag,ActorContext actor,CancellationToken ct)
    {
        var initial=await RowAsync(id,ct);var scope=await scopes.ProjectAsync(initial.ProjectId,ct);
        return await commands.ExecuteAsync(actor,scope,"pipeline.archive",async(_,token)=>{
            await RequireManageAsync(initial.ProjectId,actor,token);await locks.LockProjectAsync(initial.ProjectId,token);await RequireEnvironmentReadsAsync(initial.ProjectId,Draft(initial),actor,token);
            return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.archive",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,etag}),async inner=>{
                var row=await db.Set<ReleasePipeline>().SingleAsync(p=>p.Id==id,inner);RevisionTag.Require(etag,row.Revision);RequireActive(row);
                if(await (from p in db.Set<ProjectDeliveryPolicy>() join v in db.Set<ReleasePipelineVersion>() on p.ActivePipelineVersionId equals v.Id where v.PipelineId==id select p.Id).AnyAsync(inner))throw new ApiException(409,"pipeline_active","生效流水线须先合法停用，才能归档。");
                row.Status="Archived";row.Revision++;row.UpdatedAt=DateTimeOffset.UtcNow;row.UpdatedBy=actor.UserId;return View(row,await LatestAsync(id,inner));
            },token);
        },ct);
    }
    private async Task RequireReadAsync(ReleasePipeline row,ActorContext actor,CancellationToken ct)
    {var scope=await scopes.ProjectAsync(row.ProjectId,ct);foreach(var code in new[]{"pipeline.read","project.read"})if(!await auth.CanAsync(actor,code,new("project",row.ProjectId,scope),ct))throw ScopeResolver.Missing();await RequireEnvironmentReadsAsync(row.ProjectId,Draft(row),actor,ct,hide:true);}
    public async Task<PipelineDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct)
    {var row=await RowAsync(id,ct);await RequireReadAsync(row,actor,ct);var latest=await LatestAsync(id,ct);if(latest is not null)await RequireEnvironmentReadsAsync(row.ProjectId,Content(latest).Definition,actor,ct,hide:true);return View(row,latest);}
    public async Task<IReadOnlyList<PipelineVersionDto>> VersionsAsync(Guid id,ActorContext actor,CancellationToken ct)
    {var row=await RowAsync(id,ct);await RequireReadAsync(row,actor,ct);var versions=await db.Set<ReleasePipelineVersion>().AsNoTracking().Where(v=>v.PipelineId==id).OrderByDescending(v=>v.VersionNo).Take(100).ToArrayAsync(ct);foreach(var v in versions)await RequireEnvironmentReadsAsync(row.ProjectId,Content(v).Definition,actor,ct,hide:true);return versions.Select(View).ToArray();}
    public async Task<PipelinePageDto<PipelineDto>> ListAsync(Guid projectId,int page,int size,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);foreach(var code in new[]{"pipeline.read","project.read"})if(!await auth.CanAsync(actor,code,new("project",projectId,scope),ct))throw ScopeResolver.Missing();
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(10));var token=budget.Token;
        try{
            var rows=await db.Set<ReleasePipeline>().AsNoTracking().Where(p=>p.ProjectId==projectId).OrderByDescending(p=>p.CreatedAt).ThenBy(p=>p.Id).ToArrayAsync(token);
            var visible=new List<PipelineDto>();var partial=false;
            foreach(var row in rows)try{await RequireReadAsync(row,actor,token);var latest=await LatestAsync(row.Id,token);if(latest is not null)await RequireEnvironmentReadsAsync(projectId,Content(latest).Definition,actor,token,hide:true);visible.Add(View(row,latest));}catch(ApiException e)when(e.Status==404){partial=true;}
            page=Math.Clamp(page,1,1000000);size=Math.Clamp(size,1,100);return new(visible.Skip((page-1)*size).Take(size).ToArray(),partial?null:visible.Count,page,size,partial?"Partial":"Full");
        }catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new ApiException(503,"query_budget_exceeded","查询超出预算，请缩小范围重试。");}
    }
    private Task<ReleasePipelineVersion?> LatestAsync(Guid id,CancellationToken ct)=>db.Set<ReleasePipelineVersion>().AsNoTracking().Where(v=>v.PipelineId==id).OrderByDescending(v=>v.VersionNo).FirstOrDefaultAsync(ct);
    private async Task<ReleasePipeline> RowAsync(Guid id,CancellationToken ct)=>await db.Set<ReleasePipeline>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();
    private static void RequireActive(ReleasePipeline row){if(row.Status!="Active")throw new ApiException(409,"pipeline_archived","流水线已归档，不能编辑或发布新版本。");}
    private static ApiException Invalid()=>new(422,"invalid_pipeline_definition","流水线环境链、阶段规则或审批模板不合法。");
}

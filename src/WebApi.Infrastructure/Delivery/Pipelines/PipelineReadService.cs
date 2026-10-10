using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery.Pipelines;
public sealed class PipelineReadService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,ReleaseArtifactService? artifacts=null,DeliveryGateContextResolver? gates=null,PipelineStageService? evidence=null,WebApi.Infrastructure.Releases.RunningDeploymentReader? running=null)
{
 public Task<PipelinePageDto<PipelineDto>> ListPipelinesAsync(Guid projectId,int page,int size,ActorContext actor,CancellationToken ct)=>BudgetAsync(async token=>{
  var scope=await scopes.ProjectAsync(projectId,token);foreach(var code in new[]{"pipeline.read","project.read"})if(!await auth.CanAsync(actor,code,new("project",projectId,scope),token))throw ScopeResolver.Missing();
  var ids=await ReadableEnvironments(actor,["environment.read"]).Where(e=>e.ProjectId==projectId).Select(e=>e.Id).ToArrayAsync(token);
  var query=db.Set<ReleasePipeline>().FromSqlInterpolated($"SELECT p.* FROM release_pipelines p WHERE p.project_id={projectId} AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements(p.draft_json->'stages') s WHERE NOT ((s->>'environmentId')::uuid=ANY({ids}))) AND NOT EXISTS (SELECT 1 FROM jsonb_array_elements((SELECT v.content_json->'definition'->'stages' FROM release_pipeline_versions v WHERE v.pipeline_id=p.id ORDER BY v.version_no DESC LIMIT 1)) s WHERE NOT ((s->>'environmentId')::uuid=ANY({ids})))").AsNoTracking();
  var coverage=await auth.CanAsync(actor,"environment.read",new("project",projectId,scope),token)?"Full":ids.Length==0?"Restricted":"Partial";
  var total=coverage=="Full"?await query.TagWith("pipeline-authorized-definition-count").CountAsync(token):(int?)null;
  page=Math.Clamp(page,1,1000000);size=Math.Clamp(size,1,100);var rows=await query.OrderByDescending(p=>p.CreatedAt).ThenBy(p=>p.Id).Skip((page-1)*size).Take(size).TagWith("pipeline-definition-bounded-page").ToArrayAsync(token);
  var pipelineIds=rows.Select(r=>r.Id).ToArray();var latest=await db.Set<ReleasePipelineVersion>().AsNoTracking().Where(v=>pipelineIds.Contains(v.PipelineId)&&!db.Set<ReleasePipelineVersion>().Any(n=>n.PipelineId==v.PipelineId&&n.VersionNo>v.VersionNo)).ToArrayAsync(token);
  return new PipelinePageDto<PipelineDto>(rows.Select(r=>new PipelineDto(r.Id,r.ProjectId,r.Name,r.Description,r.Revision,r.Status,"Full",JsonSerializer.Deserialize<PipelineDefinition>(r.DraftJson,CanonicalJson.Options),latest.FirstOrDefault(v=>v.PipelineId==r.Id) is{} v?new(v.Id,v.PipelineId,v.VersionNo,v.DefinitionHash,PipelineDefinitionService.Content(v),v.CreatedAt,v.CreatedBy):null)).ToArray(),total,page,size,coverage);
 },actor,ct);
 public Task<PipelinePageDto<PipelineRunDto>> ListRunsAsync(Guid? projectId,int page,int size,ActorContext actor,CancellationToken ct)=>BudgetAsync(async token=>{
  var visible=ReadableEnvironments(actor,["pipeline.read","environment.read","release.read"]);
  if(projectId is Guid project){await scopes.ProjectAsync(project,token);if(!await visible.AnyAsync(e=>e.ProjectId==project,token))throw ScopeResolver.Missing();}
  var query=db.Set<ReleasePipelineRun>().AsNoTracking().Where(r=>(projectId==null||r.ProjectId==projectId)&&db.Set<ReleasePipelineRunStage>().Any(s=>s.RunId==r.Id&&visible.Select(e=>e.Id).Contains(s.EnvironmentId)));
  var access=await AccessAsync(actor,projectId,token);var fullIds=FullQuery(access).Select(f=>f.Id);var completeScope=await HasCompleteReadScopeAsync(actor,projectId,access,token);var coverage=access.Basic.Length==0||access.Documents.Length==0?"Restricted":!completeScope||await query.AnyAsync(r=>!fullIds.Contains(r.Id),token)?"Partial":"Full";
  var total=coverage=="Full"?await query.TagWith("pipeline-authorized-run-count").CountAsync(token):(int?)null;
  page=Math.Clamp(page,1,1000000);size=Math.Clamp(size,1,100);var rows=await query.OrderByDescending(r=>r.CreatedAt).ThenBy(r=>r.Id).Skip((page-1)*size).Take(size).TagWith("pipeline-run-bounded-page").ToArrayAsync(token);
  var result=new List<PipelineRunDto>();foreach(var row in rows)result.Add(await RunProjectionAsync(row,access,actor,token));return new PipelinePageDto<PipelineRunDto>(result,total,page,size,coverage);
 },actor,ct);
 public Task<PipelineRunDto> GetRunAsync(Guid id,ActorContext actor,CancellationToken ct)=>BudgetAsync(async token=>{
  var run=await db.Set<ReleasePipelineRun>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,token)??throw ScopeResolver.Missing();var access=await AccessAsync(actor,run.ProjectId,token);
  if(!await db.Set<ReleasePipelineRunStage>().AnyAsync(s=>s.RunId==id&&access.Basic.Contains(s.EnvironmentId),token))throw ScopeResolver.Missing();return await RunProjectionAsync(run,access,actor,token);
 },actor,ct);
 private sealed record Access(Guid[] Basic,Guid[] Documents,Guid[] Policy);
 private async Task<Access> AccessAsync(ActorContext actor,Guid? project,CancellationToken ct)
 {
  var basic=await ReadableEnvironments(actor,["pipeline.read","environment.read","release.read"]).Where(e=>project==null||e.ProjectId==project).Select(e=>e.Id).ToArrayAsync(ct);
  var documents=await ReadableEnvironments(actor,["environment.read","release.read","route.read","api.read","api.version.read","api.schema.read"]).Where(e=>project==null||e.ProjectId==project).Select(e=>e.Id).ToArrayAsync(ct);
  var policy=await ReadableEnvironments(actor,["policy.read"]).Where(e=>project==null||e.ProjectId==project).Select(e=>e.Id).ToArrayAsync(ct);
  return new(basic,documents,policy);
 }
 private async Task<bool> HasCompleteReadScopeAsync(ActorContext actor,Guid? project,Access access,CancellationToken ct)
 {
  Guid[] projects=project is Guid id?[id]:await db.Set<EnvironmentRecord>().Where(e=>access.Basic.Contains(e.Id)).Select(e=>e.ProjectId).Distinct().ToArrayAsync(ct);
  foreach(var projectId in projects){var scope=await scopes.ProjectAsync(projectId,ct);foreach(var code in new[]{"pipeline.read","environment.read","release.read","route.read","api.read","api.version.read","api.schema.read"})if(!await auth.CanAsync(actor,code,new("project",projectId,scope),ct))return false;}
  return projects.Length>0;
 }
 private IQueryable<ReleaseArtifact> ArtifactQuery(Access access)=>db.Set<ReleaseArtifact>().FromSqlInterpolated($"SELECT a.* FROM release_artifacts a WHERE a.source_environment_id=ANY({access.Documents}) AND (a.source_environment_id=ANY({access.Policy}) OR NOT jsonb_path_exists(a.canonical_content, '$.routes[*].policies[*]'))").AsNoTracking();
 private IQueryable<ReleasePipelineRun> FullQuery(Access access)
 {var artifactIds=ArtifactQuery(access).Select(a=>a.Id);return db.Set<ReleasePipelineRun>().AsNoTracking().Where(r=>artifactIds.Contains(r.RootArtifactId)&&!db.Set<ReleasePipelineRunStage>().Any(s=>s.RunId==r.Id&&(!access.Basic.Contains(s.EnvironmentId)||(s.StageArtifactId!=null&&!artifactIds.Contains(s.StageArtifactId.Value)))));}
 private IQueryable<ReleasePipelineRunStage> StageQuery(Access access)
 {var artifactIds=ArtifactQuery(access).Select(a=>a.Id);return db.Set<ReleasePipelineRunStage>().AsNoTracking().Where(s=>access.Basic.Contains(s.EnvironmentId)&&(s.StageArtifactId==null||artifactIds.Contains(s.StageArtifactId.Value))&&(s.SourceStageId==null||db.Set<ReleasePipelineRunStage>().Any(p=>p.Id==s.SourceStageId&&p.RunId==s.RunId&&access.Basic.Contains(p.EnvironmentId)&&access.Documents.Contains(p.EnvironmentId)&&(p.StageArtifactId==null||artifactIds.Contains(p.StageArtifactId.Value)))));}
 private async Task<PipelineRunDto> RunProjectionAsync(ReleasePipelineRun run,Access access,ActorContext actor,CancellationToken ct)
 {
  var full=await FullQuery(access).AnyAsync(r=>r.Id==run.Id,ct);var coverage=full?"Full":access.Documents.Length==0?"Restricted":"Partial";
  var rows=await db.Set<ReleasePipelineRunStage>().AsNoTracking().Where(s=>s.RunId==run.Id&&access.Basic.Contains(s.EnvironmentId)).OrderBy(s=>s.StageOrder).Take(8).ToArrayAsync(ct);var ids=rows.Select(s=>s.Id).ToArray();
  var currentIds=rows.Where(s=>s.CurrentAttemptId!=null).Select(s=>s.CurrentAttemptId!.Value).ToArray();var attempts=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().Where(a=>ids.Contains(a.RunStageId)&&currentIds.Contains(a.Id)).ToArrayAsync(ct);
  var artifactsOnPage=rows.Where(s=>s.StageArtifactId!=null).Select(s=>s.StageArtifactId!.Value).ToArray();var visibleArtifacts=await ArtifactQuery(access).Where(a=>artifactsOnPage.Contains(a.Id)).Select(a=>a.Id).ToArrayAsync(ct);var view=PipelineRunService.View(run,rows,attempts);var readableIds=rows.Select(s=>s.Id).ToHashSet();var projections=view.Stages.Select(s=>s with{SourceStageId=s.SourceStageId is Guid source&&readableIds.Contains(source)?source:null,StageArtifactId=s.StageArtifactId is Guid artifact&&visibleArtifacts.Contains(artifact)?artifact:null,Profile=access.Documents.Contains(s.EnvironmentId)?s.Profile:null}).ToArray();
  var canControl=full&&await CanControlAsync(run,actor,ct);var active=rows.SingleOrDefault(s=>s.StageOrder==run.CurrentStageOrder);var current=attempts.SingleOrDefault(a=>a.Id==active?.CurrentAttemptId);var canResume=canControl&&run.Status=="Paused"&&current?.Status=="Active"&&current.DeadlineAt>DateTimeOffset.UtcNow;
  if(canResume)canResume=await ResumePrerequisitesAsync(run,active!,current!,actor,ct);
  var eligibility=new PipelineRunEligibility(canControl&&run.Status=="Active",canResume,canControl&&!WebApi.Domain.Delivery.Pipelines.PipelineStateRules.IsTerminal(run.Status),canControl?[]:["run_control_restricted"]);
  return view with{RootArtifactId=full?view.RootArtifactId:null,RootArtifactHash=full?view.RootArtifactHash:null,SourceEnvironmentId=full?view.SourceEnvironmentId:null,SourceReleaseId=full?view.SourceReleaseId:null,SourceConfigVersion=full?view.SourceConfigVersion:null,SourceDeploymentSequence=full?view.SourceDeploymentSequence:null,Coverage=coverage,Stages=projections,Eligibility=eligibility};
 }
 private async Task<bool> ResumePrerequisitesAsync(ReleasePipelineRun run,ReleasePipelineRunStage stage,ReleasePipelineStageAttempt attempt,ActorContext actor,CancellationToken ct)
 {
  try{
   var actual=attempt.ActualReleaseId is Guid id?await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==id,ct):null;
   if(actual?.Status is "Failed" or "Rejected" or "Cancelled")return false;
   var formal=await PipelineAttemptFacts.FormalAsync(db,attempt,ct);
   if(formal?.Status is "Rejected" or "DeploymentFailed" or "VerificationFailed" or "Cancelled" or "Invalidated")return false;
   await gates!.ResolveStageFactsAsync(stage.Id,null,ct);var profile=JsonSerializer.Deserialize<PipelineStageProfile>(stage.ProfileJson,CanonicalJson.Options)!;var environment=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==stage.EnvironmentId,ct);if(environment.IsProduction!=profile.IsProduction)return false;
   if(actual?.Status=="Succeeded"){
    await running!.ReadAsync(stage.EnvironmentId,actual.Id,ct);
    if(!profile.IsProduction)return WebApi.Domain.Delivery.ReleaseArtifactCanonicalizer.Hash((await artifacts!.RequireSourceAsync(actual.Id,actor,ct)).Content)==run.RootArtifactHash;
    return formal!=null&&(await PromotionReadService.ResolvePromotionWithinAsync(db,actual.Id,ct))?.Id==formal.Id;
   }
   if(stage.SourceStageId is not Guid source)return false;var previousStage=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleAsync(s=>s.Id==source&&s.RunId==run.Id,ct);
   if(previousStage.Status!="Passed"||previousStage.CurrentAttemptId is not Guid previousAttempt)return false;var accepted=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().Where(a=>a.Id==previousAttempt).Select(a=>a.AcceptanceId).SingleAsync(ct);if(accepted is not Guid acceptance)return false;
   await evidence!.RequireAcceptedCurrentAsync(await db.Set<ReleaseTestAcceptance>().AsNoTracking().SingleAsync(a=>a.Id==acceptance,ct),actor,ct);await WebApi.Infrastructure.Releases.ReleaseService.RequirePipelineApprovalAsync(db,run.OrganizationId,environment,profile.Approval,ct);return true;
  }catch(ApiException e)when(e.Status is 401 or 403 or 404 or 409 or 422){return false;}
 }
 private async Task<bool> CanControlAsync(ReleasePipelineRun run,ActorContext actor,CancellationToken ct)
 {var scope=await scopes.ProjectAsync(run.ProjectId,ct);return await auth.CanAsync(actor,"pipeline.run",new("project",run.ProjectId,scope),ct)&&await auth.CanAsync(actor,"project.write",new("project",run.ProjectId,scope),ct);}
 public Task<PipelineStageDto> GetStageAsync(Guid id,ActorContext actor,CancellationToken ct)=>BudgetAsync(async token=>{
  var stage=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleOrDefaultAsync(s=>s.Id==id,token)??throw ScopeResolver.Missing();var access=await AccessAsync(actor,stage.ProjectId,token);
  if(!await StageQuery(access).AnyAsync(s=>s.Id==id,token))throw ScopeResolver.Missing();await RequireStageArtifactsAsync(stage,actor,token);
  var run=await db.Set<ReleasePipelineRun>().AsNoTracking().SingleAsync(r=>r.Id==stage.RunId,token);var environment=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==stage.EnvironmentId,token);var profile=JsonSerializer.Deserialize<PipelineStageProfile>(stage.ProfileJson,CanonicalJson.Options)!;
  var attempts=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().Where(a=>a.RunStageId==id).OrderByDescending(a=>a.AttemptNo).Take(100).ToArrayAsync(token);var current=attempts.FirstOrDefault(a=>a.Id==stage.CurrentAttemptId);
  var formal=current is null?null:await PipelineAttemptFacts.FormalAsync(db,current,token);
  var actual=current?.ActualReleaseId is Guid release?await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==release&&r.EnvironmentId==stage.EnvironmentId,token):null;
  var scope=await scopes.EnvironmentAsync(stage.EnvironmentId,token);var nodes=await auth.CanAsync(actor,"gateway.read",new("environment",stage.EnvironmentId,scope),token)?await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==stage.EnvironmentId&&n.Enabled).OrderBy(n=>n.Id).Select(n=>new PromotionNodeState(n.Id,n.NodeName,n.CurrentConfigVersion,n.CurrentDeploymentSequence,n.Status,n.LastHeartbeatAt)).ToArrayAsync(token):null;
  var facts=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>v.PipelineRunStageId==id&&v.StageAttemptId==stage.CurrentAttemptId).OrderByDescending(v=>v.CreatedAt).ThenBy(v=>v.Id).Take(100).ToArrayAsync(token);
  var accepted=current?.AcceptanceId is Guid acceptance?await db.Set<ReleaseTestAcceptance>().AsNoTracking().SingleOrDefaultAsync(a=>a.Id==acceptance&&a.PipelineRunStageId==id,token):null;
  var events=await db.Set<ReleasePipelineEvent>().AsNoTracking().Where(e=>e.StageId==id&&e.RunId==run.Id).OrderByDescending(e=>e.CreatedAt).ThenBy(e=>e.Id).Take(100).Select(e=>new PipelineEventDto(e.Id,e.StageId,e.AttemptId,e.FromStatus,e.ToStatus,e.ReasonCode,e.ActorId,e.CreatedAt)).ToArrayAsync(token);
  var eligibility=await EligibilityAsync(run,stage,current,formal,actual,profile,access,actor,token);var trace=await TraceCoreAsync(stage,run,access,actor,token);
  var acceptanceDto=accepted is null?null:await evidence!.AcceptanceViewAsync(accepted,actor,token);
  return new PipelineStageDto(stage.Id,run.Id,run.ProjectId,stage.StageOrder,stage.EnvironmentId,environment.Name,stage.SourceStageId,stage.StageArtifactId,stage.Status,run.Status,run.CurrentStageOrder,stage.Revision,stage.CurrentAttemptId,current?.ActivatedAt,current?.DeadlineAt,stage.ProfileHash,profile,formal?.Id,actual?.Id,actual?.Status,actual?.ToConfigVersion,actual?.DeploymentSequence,actual?.DeadlineAt,nodes,facts.Select(ReleaseVerificationService.View).ToArray(),acceptanceDto,attempts.Select(a=>new PipelineAttemptDto(a.Id,a.AttemptNo,a.OriginAttemptId,a.Status,a.ActivatedAt,a.DeadlineAt,a.ArtifactId,a.AcceptanceId,a.PromotionId,a.ActualReleaseId,a.Revision)).ToArray(),events,eligibility,trace);
 },actor,ct);
 private async Task RequireStageArtifactsAsync(ReleasePipelineRunStage stage,ActorContext actor,CancellationToken ct)
 {if(stage.StageArtifactId is Guid own)await artifacts!.GetAsync(own,actor,ct);if(stage.SourceStageId is Guid source){var previous=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleAsync(p=>p.Id==source&&p.RunId==stage.RunId,ct);if(previous.StageArtifactId is Guid id)await artifacts!.GetAsync(id,actor,ct);}}
 private async Task<PipelineStageEligibility> EligibilityAsync(ReleasePipelineRun run,ReleasePipelineRunStage stage,ReleasePipelineStageAttempt? attempt,ReleasePromotion? formal,ReleaseRecord? actual,PipelineStageProfile profile,Access access,ActorContext actor,CancellationToken ct)
 {
  var scope=await scopes.EnvironmentAsync(stage.EnvironmentId,ct);var reasons=new List<string>();var writable=false;
  try{var gate=await gates!.ResolveStageAsync(stage.Id,actor,ct);await gates.RequireStageWritableAsync(gate,actor,ct);writable=true;}catch(ApiException e)when(e.Status is 403 or 404 or 409 or 422){reasons.Add(e.Code);}
  var advance=await auth.CanAsync(actor,"pipeline.run",new("environment",stage.EnvironmentId,scope),ct)&&await auth.CanAsync(actor,"release.create",new("environment",stage.EnvironmentId,scope),ct);
  var freshSource=false;if(stage.SourceStageId is Guid sourceId){var previous=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleAsync(s=>s.Id==sourceId,ct);var accepted=previous.CurrentAttemptId is Guid previousAttempt?await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().Where(a=>a.Id==previousAttempt).Select(a=>a.AcceptanceId).SingleAsync(ct):null;if(previous.Status=="Passed"&&accepted is Guid acceptance)try{await evidence!.RequireAcceptedCurrentAsync(await db.Set<ReleaseTestAcceptance>().AsNoTracking().SingleAsync(a=>a.Id==acceptance,ct),actor,ct);freshSource=true;}catch(ApiException e)when(e.Status is 401 or 403 or 404 or 409 or 422){reasons.Add(e.Code);}}
  var currentDelivery=false;if(actual?.Status=="Succeeded")try{await running!.ReadAsync(stage.EnvironmentId,actual.Id,ct);currentDelivery=true;}catch(ApiException e)when(e.Status is 409 or 422){reasons.Add(e.Code);}
  var record=writable&&!profile.IsProduction&&currentDelivery&&stage.StageArtifactId!=null&&await auth.CanAsync(actor,"release.test.record",new("environment",stage.EnvironmentId,scope),ct);
  if(record)try{await evidence!.GetVerificationContextAsync(stage.Id,actor,ct);}catch(ApiException e)when(e.Status is 403 or 404 or 409 or 422){record=false;reasons.Add(e.Code);}
  var requestAcceptance=false;if(record)try{requestAcceptance=await evidence!.CanRequestAcceptanceAsync(stage.Id,actor,ct);}catch(ApiException e)when(e.Status is 401 or 403 or 404 or 409 or 422){reasons.Add(e.Code);}
  var publishing=await db.Set<ReleaseRecord>().AnyAsync(r=>r.EnvironmentId==stage.EnvironmentId&&r.Status=="Publishing",ct);
  var canReopen=!WebApi.Domain.Delivery.Pipelines.PipelineStateRules.IsTerminal(run.Status)&&run.CurrentStageOrder==stage.StageOrder&&attempt!=null&&advance&&!publishing&&(attempt.Status!="Active"||attempt.DeadlineAt<=DateTimeOffset.UtcNow||actual?.Status is "Failed" or "Rejected" or "Cancelled"||formal?.Status is "Rejected" or "VerificationFailed" or "DeploymentFailed" or "Cancelled");
  var control=await FullQuery(access).AnyAsync(r=>r.Id==run.Id,ct)&&await CanControlAsync(run,actor,ct);
  var verify=writable&&profile.IsProduction&&currentDelivery&&formal!=null&&actor.UserId!=run.CreatedBy&&actor.UserId!=formal.RequestedBy&&actor.UserId!=actual!.PublishRequestedBy&&await auth.CanAsync(actor,"release.verify",new("environment",stage.EnvironmentId,scope),ct);
  return new(writable&&advance&&stage.SourceStageId!=null&&freshSource&&attempt?.PromotionId==null,writable&&advance&&!profile.IsProduction&&currentDelivery&&stage.StageArtifactId==null,record,requestAcceptance,canReopen,control&&!WebApi.Domain.Delivery.Pipelines.PipelineStateRules.IsTerminal(run.Status),reasons.Distinct().ToArray(),verify);
 }
 public Task<PipelinePageDto<PipelineTraceDto>> ListArtifactTracesAsync(Guid id,int page,int size,ActorContext actor,CancellationToken ct)=>BudgetAsync(async token=>{
  var artifact=await artifacts!.GetAsync(id,actor,token);var access=await AccessAsync(actor,artifact.ProjectId,token);var query=StageQuery(access).Where(s=>s.StageArtifactId==id);var total=await query.CountAsync(token);page=Math.Clamp(page,1,1000000);size=Math.Clamp(size,1,100);var rows=await query.OrderByDescending(s=>s.RunId).ThenBy(s=>s.Id).Skip((page-1)*size).Take(size).TagWith("pipeline-artifact-trace-bounded-page").ToArrayAsync(token);var result=new List<PipelineTraceDto>();foreach(var stage in rows)result.Add(await TraceCoreAsync(stage,await db.Set<ReleasePipelineRun>().AsNoTracking().SingleAsync(r=>r.Id==stage.RunId,token),access,actor,token));return new PipelinePageDto<PipelineTraceDto>(result,total,page,size,"Full");
 },actor,ct);
 public async Task<PipelineTraceDto?> TraceStageAsync(Guid id,ActorContext actor,CancellationToken ct)
 {var stage=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleOrDefaultAsync(s=>s.Id==id,ct);if(stage is null)return null;var access=await AccessAsync(actor,stage.ProjectId,ct);if(!await StageQuery(access).AnyAsync(s=>s.Id==id,ct))return null;try{await RequireStageArtifactsAsync(stage,actor,ct);}catch(ApiException e)when(e.Status is 403 or 404 or 422){return null;}return await TraceCoreAsync(stage,await db.Set<ReleasePipelineRun>().AsNoTracking().SingleAsync(r=>r.Id==stage.RunId,ct),access,actor,ct);}
 private async Task<PipelineTraceDto> TraceCoreAsync(ReleasePipelineRunStage stage,ReleasePipelineRun run,Access access,ActorContext actor,CancellationToken ct)
 {var full=await FullQuery(access).AnyAsync(r=>r.Id==run.Id,ct);var scope=await scopes.ProjectAsync(run.ProjectId,ct);var definition=full&&await auth.CanAsync(actor,"project.read",new("project",run.ProjectId,scope),ct)&&await auth.CanAsync(actor,"pipeline.read",new("project",run.ProjectId,scope),ct);var pipelineId=definition?await db.Set<ReleasePipelineVersion>().Where(v=>v.Id==run.PipelineVersionId).Select(v=>(Guid?)v.PipelineId).SingleAsync(ct):null;return new(pipelineId,run.Id,stage.Id,stage.StageOrder,full?"Full":"Partial");}
 internal async Task<(PipelinePageDto<PipelineRunDto>? Page,PipelineRunCounts? Counts,string Visibility)> OverviewAsync(Guid project,int page,int size,ActorContext actor,CancellationToken ct)
 {var access=await AccessAsync(actor,project,ct);if(access.Basic.Length==0)return(null,null,"Restricted");var list=await ListRunsAsync(project,page,size,actor,ct);if(list.Coverage!="Full")return(list,null,list.Coverage);var query=FullQuery(access).Where(r=>r.ProjectId==project);var counts=await query.GroupBy(r=>1).Select(g=>new PipelineRunCounts(g.Count(),g.Count(r=>r.Status=="Active"),g.Count(r=>r.Status=="Paused"),g.Count(r=>r.Status=="TimedOut"),g.Count(r=>r.Status=="Completed"))).FirstOrDefaultAsync(ct)??new(0,0,0,0,0);return(list,counts,"Full");}

 private IQueryable<EnvironmentRecord> ReadableEnvironments(ActorContext actor,string[] codes)=>
  from env in db.Set<EnvironmentRecord>() join project in db.Set<Project>() on env.ProjectId equals project.Id join organization in db.Set<Organization>() on project.OrganizationId equals organization.Id
  where project.Status=="Active"&&organization.Status=="Active"&&(!codes.Any(c=>!c.StartsWith("environment."))||env.Status=="Active")
   &&db.Set<UserProjectScope>().Any(g=>g.UserId==actor.UserId&&g.OrganizationId==organization.Id&&(g.ProjectId==null||g.ProjectId==project.Id)&&(g.EnvironmentId==null||g.EnvironmentId==env.Id))
   &&db.Set<Permission>().Where(p=>codes.Contains(p.Code)&&(from ur in db.Set<UserRole>() join role in db.Set<Role>() on ur.RoleId equals role.Id join rp in db.Set<RolePermission>() on role.Id equals rp.RoleId where ur.UserId==actor.UserId&&rp.PermissionId==p.Id&&(role.OrganizationId==null||role.OrganizationId==organization.Id) select rp.PermissionId).Any()).Count()==codes.Length
  select env;
 private async Task<T> BudgetAsync<T>(Func<CancellationToken,Task<T>> query,ActorContext actor,CancellationToken ct)
 {
  using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(10));var token=budget.Token;
  try{
   await using var transaction=db.Database.CurrentTransaction is null?await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,token):null;
   await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '10000'",token);
   if(!await db.Set<UserRecord>().AnyAsync(u=>u.Id==actor.UserId&&u.Status=="Active",token))throw new ApiException(401,"inactive_session","会话已失效。");
   var result=await query(token);if(transaction is not null)await transaction.CommitAsync(token);return result;
  }catch(OperationCanceledException)when(!ct.IsCancellationRequested&&budget.IsCancellationRequested){throw Timeout();}catch(PostgresException e)when(e.SqlState=="57014"&&!ct.IsCancellationRequested){throw Timeout();}
 }
 private static ApiException Timeout()=>new(503,"pipeline_query_timeout","流水线查询超时，请稍后重试。");
}

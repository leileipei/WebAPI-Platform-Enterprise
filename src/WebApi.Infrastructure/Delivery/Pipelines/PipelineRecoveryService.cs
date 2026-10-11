using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Delivery;
using WebApi.Domain.Delivery.Pipelines;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery.Pipelines;
public sealed class PipelineRecoveryService(WebApiDbContext db,PipelineRunService runs,DeliveryGateContextResolver gates,
 ScopeResolver scopes,AuthorizationService auth,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,
 CommandRequestContext requestContext,DeliveryLockCoordinator locks,PipelineStageService evidence,ReleaseArtifactService artifacts,
 ReleaseRecoveryService recovery,PromotionReadService promotions,RunningDeploymentReader running,HistoricalSnapshotService history,TimeProvider clock)
{
 private sealed record ReopenResult(PipelineRunStageDto? Stage,string? FailureCode=null);
 private static ApiException Unavailable()=>new(409,"pipeline_recovery_unavailable","当前状态不能办理该操作；失败或超时需重新办理，正在下发时需先等待真实结果。");
 public Task<PipelineRunDto> PauseAsync(Guid id,PipelineActionRequest request,string? etag,ActorContext actor,CancellationToken ct)=>RunActionAsync(id,"pause",request,etag,actor,ct);
 public Task<PipelineRunDto> ResumeAsync(Guid id,PipelineActionRequest request,string? etag,ActorContext actor,CancellationToken ct)=>RunActionAsync(id,"resume",request,etag,actor,ct);
 public Task<PipelineRunDto> CancelAsync(Guid id,PipelineActionRequest request,string? etag,ActorContext actor,CancellationToken ct)=>RunActionAsync(id,"cancel",request,etag,actor,ct);
 private static void Validate(PipelineActionRequest request){if(request is null||(request.Comment?.Length??0)>4000)throw new ApiException(422,"invalid_pipeline_action","操作说明最长4000字符。");}
 private async Task<PipelineRunDto> RunActionAsync(Guid id,string action,PipelineActionRequest request,string? etag,ActorContext actor,CancellationToken ct)
 {
  Validate(request);var initial=await runs.GetAsync(id,actor,ct);var scope=await scopes.ProjectAsync(initial.ProjectId,ct);
  return await commands.ExecuteAsync(actor,scope,"pipeline.run."+action,async(_,token)=>{
   await locks.LockProjectAsync(initial.ProjectId,token);var environments=await db.Set<ReleasePipelineRunStage>().Where(s=>s.RunId==id).Select(s=>s.EnvironmentId).ToArrayAsync(token);await locks.LockEnvironmentsAsync(environments,token);
   await auth.RequireAsync(actor,"pipeline.run",new("project",initial.ProjectId,scope),token);await auth.RequireAsync(actor,"project.write",new("project",initial.ProjectId,scope),token);await runs.GetAsync(id,actor,token);
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.run."+action,requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,request,etag}),async inner=>{
    var run=await db.Set<ReleasePipelineRun>().SingleAsync(r=>r.Id==id,inner);RevisionTag.Require(etag,run.Revision);if(PipelineStateRules.IsTerminal(run.Status))throw Unavailable();
    var stage=await db.Set<ReleasePipelineRunStage>().SingleAsync(s=>s.RunId==id&&s.StageOrder==run.CurrentStageOrder,inner);var attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==stage.CurrentAttemptId,inner);var now=clock.GetUtcNow();var before=run.Status;
    if(action=="pause"){if(run.Status!="Active")throw Unavailable();run.Status="Paused";}
    else if(action=="resume")
    {
     if(run.Status!="Paused"||attempt.Status!="Active"||attempt.DeadlineAt<=now)throw Unavailable();
     var formal=await FormalAsync(attempt,inner);var actual=attempt.ActualReleaseId is Guid actualId?await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==actualId,inner):null;
     if(actual?.Status is "Failed" or "Rejected" or "Cancelled"||formal?.Status is "Rejected" or "DeploymentFailed" or "VerificationFailed" or "Cancelled" or "Invalidated")throw Unavailable();
     await gates.ResolveStageFactsAsync(stage.Id,null,inner);var profile=Profile(stage);var environment=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==stage.EnvironmentId,inner);if(environment.IsProduction!=profile.IsProduction)throw Unavailable();
     if(actual?.Status=="Succeeded"){await RequireSameRunningAsync(stage,actual.Id,run.RootArtifactHash,actor,inner);}
     else{await RequirePredecessorAsync(stage,actor,inner);await ReleaseService.RequirePipelineApprovalAsync(db,run.OrganizationId,environment,profile.Approval,inner);}
     run.Status="Active";
    }
    else
    {
     await CancelCandidatesAsync(run,stage:null,actor,"pipeline_run_cancelled",now,inner);run.Status="Cancelled";run.CompletedAt=now;
     foreach(var remaining in await db.Set<ReleasePipelineRunStage>().Where(s=>s.RunId==run.Id&&s.Status!="Passed").ToArrayAsync(inner)){var old=remaining.Status;remaining.Status="Cancelled";remaining.Revision++;if(remaining.CurrentAttemptId is Guid current){var a=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==current,inner);if(a.Status!="Passed"){a.Status="Cancelled";a.Revision++;}}Append(run,remaining,remaining.CurrentAttemptId,old,"Cancelled","stage_cancelled",actor.UserId,now);}
    }
    run.Revision++;Append(run,stage,attempt.Id,before,run.Status,"pipeline_"+action,actor.UserId,now);return await RunViewAsync(run,inner);
   },token);
  },ct);
 }
 public async Task<PipelineRunStageDto> ReopenAsync(Guid id,PipelineActionRequest request,string? etag,ActorContext actor,CancellationToken ct)
 {
  Validate(request);var initial=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleOrDefaultAsync(s=>s.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);
  var result=await commands.ExecuteAsync(actor,scope,"pipeline.stage.reopen",async(_,token)=>{
   await locks.LockProjectAsync(initial.ProjectId,token);var environments=new List<Guid>{initial.EnvironmentId};if(initial.SourceStageId is Guid source)environments.Add(await db.Set<ReleasePipelineRunStage>().Where(s=>s.Id==source&&s.RunId==initial.RunId).Select(s=>s.EnvironmentId).SingleAsync(token));await locks.LockEnvironmentsAsync(environments,token);
   await RequireStageAuthorityAsync(initial,actor,token);
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.stage.reopen",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,request,etag}),async inner=>{
    var stage=await db.Set<ReleasePipelineRunStage>().SingleAsync(s=>s.Id==id,inner);RevisionTag.Require(etag,stage.Revision);var run=await db.Set<ReleasePipelineRun>().SingleAsync(r=>r.Id==stage.RunId,inner);
    if(PipelineStateRules.IsTerminal(run.Status)||run.CurrentStageOrder!=stage.StageOrder||stage.CurrentAttemptId is not Guid priorId)throw Unavailable();var prior=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==priorId,inner);var now=clock.GetUtcNow();
    var formal=await FormalAsync(prior,inner);var referenced=prior.ActualReleaseId is Guid referencedId?await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==referencedId,inner):null;
    if(await db.Set<ReleaseRecord>().AnyAsync(r=>r.EnvironmentId==stage.EnvironmentId&&r.Status=="Publishing",inner))throw Unavailable();
    if(prior.Status=="Active"&&prior.DeadlineAt>now&&referenced?.Status is not("Failed" or "Rejected" or "Cancelled")&&formal?.Status is not("Rejected" or "VerificationFailed" or "DeploymentFailed" or "Cancelled"))throw Unavailable();
    try{await gates.ResolveStageFactsAsync(stage.Id,null,inner);}catch(ApiException e)when(e.Status==409){return Invalidate(run,stage,prior,"pipeline_definition_changed",actor,now);}
    var profile=Profile(stage);var environment=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==stage.EnvironmentId,inner);if(environment.IsProduction!=profile.IsProduction)return Invalidate(run,stage,prior,"pipeline_definition_changed",actor,now);
    ReleaseRecord? actual=null;
    if(environment.DesiredConfigVersion is long config&&environment.DeploymentSequence>0)actual=await db.Set<ReleaseRecord>().Where(r=>r.EnvironmentId==environment.Id&&r.ToConfigVersion==config&&r.DeploymentSequence==environment.DeploymentSequence).SingleOrDefaultAsync(inner);
    var emitted=referenced?.DeploymentSequence is >0;
    if(stage.StageOrder>1&&emitted){if(actual is null||(await promotions.ResolvePromotionAsync(actual.Id,inner))?.Id!=formal?.Id)throw new ApiException(409,"pipeline_actual_deployment_changed","目标当前部署已属于其他申请，不能复用原阶段窗口。");}
    if(stage.StageOrder==1||emitted&&actual?.Status=="Succeeded")
    {
     if(actual is null||actual.Status!="Succeeded")throw Unavailable();
     if(!profile.IsProduction){var source=await artifacts.RequireSourceAsync(actual.Id,actor,inner);if(ReleaseArtifactCanonicalizer.Hash(source.Content)!=run.RootArtifactHash)return Invalidate(run,stage,prior,"pipeline_root_behavior_changed",actor,now);}
     else{await gates.ResolvePromotionFactsAsync(formal!,inner);var current=await running.ReadAsync(stage.EnvironmentId,actual.Id,inner);var original=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==formal!.TargetReleaseId,inner);if(original.ToConfigVersion!=actual.ToConfigVersion||(await history.ReadAsync(stage.EnvironmentId,original.ToConfigVersion,inner)).Hash!=current.Snapshot.Hash)throw Unavailable();}
    }
    if(stage.StageOrder>1&&!emitted){await RequirePredecessorAsync(stage,actor,inner);await ReleaseService.RequirePipelineApprovalAsync(db,run.OrganizationId,environment,profile.Approval,inner);await CancelCandidatesAsync(run,stage,actor,"pipeline_attempt_reopened",now,inner);}
    if(emitted&&actual?.Status=="Failed")await auth.RequireAsync(actor,"release.publish",new("environment",stage.EnvironmentId,scope),inner);
    else if(emitted&&actual?.Status is not "Succeeded")throw Unavailable();
    if(prior.Status=="Active"){prior.Status=prior.DeadlineAt<=now?"TimedOut":referenced?.Status=="Failed"?"DeploymentFailed":"Rejected";prior.Revision++;}
    await db.SaveChangesAsync(inner);
    var next=new ReleasePipelineStageAttempt{RunStageId=stage.Id,RunId=run.Id,ProjectId=run.ProjectId,EnvironmentId=stage.EnvironmentId,AttemptNo=prior.AttemptNo+1,OriginAttemptId=prior.Id,ActivatedAt=now,DeadlineAt=now.AddMinutes(profile.WaitTimeoutMinutes),ActualReleaseId=stage.StageOrder==1||emitted?actual?.Id:null,ArtifactId=actual?.Id==referenced?.Id?stage.StageArtifactId:null};db.Add(next);
    // Persist the terminal old attempt before the unique Active attempt, then break the FK cycle.
    await db.SaveChangesAsync(inner);stage.CurrentAttemptId=next.Id;stage.StageArtifactId=next.ArtifactId;
    var before=stage.Status;stage.Status=stage.StageOrder==1?"AwaitingEvidence":emitted?"AwaitingVerification":"AwaitingMapping";stage.Revision++;run.Status="Active";run.CompletedAt=null;run.Revision++;
    if(emitted&&actual?.Status=="Failed"){var retried=await recovery.RetryWithinTransactionAsync(actual.Id,actor,inner);next.ActualReleaseId=retried.Id;next.ArtifactId=null;stage.StageArtifactId=null;stage.Status="Deploying";next.Revision++;}
    else if(emitted&&formal is not null&&formal.Status is "VerificationFailed" or "Completed"){var old=formal.Status;formal.Status="Verifying";formal.CompletedAt=null;formal.Revision++;db.Add(new ReleasePromotionEvent{OrganizationId=formal.OrganizationId,ProjectId=formal.ProjectId,EnvironmentId=formal.TargetEnvironmentId,PromotionId=formal.Id,Phase="Verification",FromStatus=old,ToStatus="Verifying",ReasonCode="pipeline_verification_window_reopened",ActorId=actor.UserId,ReleaseId=actual?.Id});}
    Append(run,stage,next.Id,before,stage.Status,"stage_reopened",actor.UserId,now,prior.Id);
    db.Add(new AuditLog{UserId=actor.UserId,OrganizationId=run.OrganizationId,ProjectId=run.ProjectId,EnvironmentId=stage.EnvironmentId,Action="pipeline.stage.reopen.binding",ResourceType=nameof(ReleasePipelineStageAttempt),ResourceId=next.Id.ToString(),TraceId=actor.TraceId,AfterJson=PipelineDefinitionService.Json(new{runId=run.Id,stageId=stage.Id,priorAttemptId=prior.Id,attemptId=next.Id,formalPromotionId=formal?.Id,formalAttemptId=formal?.StageAttemptId,actualReleaseId=next.ActualReleaseId,profileHash=stage.ProfileHash,run.DefinitionHash,run.RootArtifactHash,run.PolicyRevision})});
    return new ReopenResult(StageView(stage,next));
   },token);
  },ct);
  if(result.FailureCode is{} code)throw new ApiException(409,code,"冻结定义或根行为已无法保全，本运行已失效，请核对后新建运行。");return result.Stage!;
 }
 private async Task RequireStageAuthorityAsync(ReleasePipelineRunStage stage,ActorContext actor,CancellationToken ct)
 {var scope=await scopes.EnvironmentAsync(stage.EnvironmentId,ct);await auth.RequireAsync(actor,"pipeline.run",new("environment",stage.EnvironmentId,scope),ct);await auth.RequireAsync(actor,"release.create",new("environment",stage.EnvironmentId,scope),ct);foreach(var permission in new[]{"environment.read","release.read"})await auth.RequireAsync(actor,permission,new("environment",stage.EnvironmentId,scope),ct);if(stage.SourceStageId is Guid id){var source=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleAsync(s=>s.Id==id&&s.RunId==stage.RunId,ct);var previous=await scopes.EnvironmentAsync(source.EnvironmentId,ct);foreach(var permission in new[]{"environment.read","release.read"})await auth.RequireAsync(actor,permission,new("environment",source.EnvironmentId,previous),ct);}}
 private async Task RequirePredecessorAsync(ReleasePipelineRunStage stage,ActorContext actor,CancellationToken ct)
 {if(stage.SourceStageId is not Guid id){var attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==stage.CurrentAttemptId,ct);if(attempt.ActualReleaseId is not Guid source)throw Unavailable();var run=await db.Set<ReleasePipelineRun>().SingleAsync(r=>r.Id==stage.RunId,ct);await RequireSameRunningAsync(stage,source,run.RootArtifactHash,actor,ct);return;}var previous=await db.Set<ReleasePipelineRunStage>().SingleAsync(s=>s.Id==id&&s.RunId==stage.RunId,ct);if(previous.Status!="Passed"||previous.CurrentAttemptId is not Guid prior)throw Unavailable();var attemptRow=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==prior,ct);if(attemptRow.AcceptanceId is not Guid accepted)throw Unavailable();await evidence.RequireAcceptedCurrentAsync(await db.Set<ReleaseTestAcceptance>().SingleAsync(a=>a.Id==accepted,ct),actor,ct);}
 private async Task RequireSameRunningAsync(ReleasePipelineRunStage stage,Guid releaseId,string hash,ActorContext actor,CancellationToken ct)
 {var current=await running.ReadAsync(stage.EnvironmentId,releaseId,ct);if(!Profile(stage).IsProduction){if(ReleaseArtifactCanonicalizer.Hash((await artifacts.RequireSourceAsync(current.Release.Id,actor,ct)).Content)!=hash)throw Unavailable();}else{var attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==stage.CurrentAttemptId,ct);var formal=await FormalAsync(attempt,ct);if(formal is null||(await promotions.ResolvePromotionAsync(current.Release.Id,ct))?.Id!=formal.Id)throw Unavailable();}}
 private Task<ReleasePromotion?> FormalAsync(ReleasePipelineStageAttempt attempt,CancellationToken ct)=>PipelineAttemptFacts.FormalAsync(db,attempt,ct);
 private async Task CancelCandidatesAsync(ReleasePipelineRun run,ReleasePipelineRunStage? stage,ActorContext actor,string reason,DateTimeOffset now,CancellationToken ct)
 {var stageId=stage?.Id;var ids=await db.Set<ReleasePipelineRunStage>().Where(s=>s.RunId==run.Id&&(stageId==null||s.Id==stageId)).Select(s=>s.Id).ToArrayAsync(ct);var rows=await db.Set<ReleasePromotion>().Where(p=>p.PipelineRunStageId!=null&&ids.Contains(p.PipelineRunStageId.Value)&&p.Status!="Cancelled"&&p.Status!="Completed").ToArrayAsync(ct);foreach(var p in rows){var release=p.TargetReleaseId is Guid id?await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id,ct):null;if(release?.DeploymentSequence is >0||release?.Status=="Publishing")continue;var before=p.Status;p.Status="Cancelled";p.CompletedAt=now;p.Revision++;if(release is not null){release.Status="Cancelled";release.CompletedAt=now;release.FailureCode??=reason;}db.Add(new ReleasePromotionEvent{OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,EnvironmentId=p.TargetEnvironmentId,PromotionId=p.Id,Phase="PreExecution",FromStatus=before,ToStatus="Cancelled",ReasonCode=reason,ActorId=actor.UserId,ReleaseId=release?.Id});}}
 private ReopenResult Invalidate(ReleasePipelineRun run,ReleasePipelineRunStage stage,ReleasePipelineStageAttempt attempt,string code,ActorContext actor,DateTimeOffset now)
 {var before=run.Status;run.Status="Invalidated";run.CompletedAt=now;run.Revision++;stage.Status="Invalidated";stage.Revision++;attempt.Status="Invalidated";attempt.Revision++;Append(run,stage,attempt.Id,before,"Invalidated",code,actor.UserId,now);return new(null,code);}
 private void Append(ReleasePipelineRun run,ReleasePipelineRunStage stage,Guid? attempt,string from,string to,string reason,Guid actor,DateTimeOffset now,Guid? related=null)=>db.Add(new ReleasePipelineEvent{RunId=run.Id,ProjectId=run.ProjectId,StageId=stage.Id,AttemptId=attempt,FromStatus=from,ToStatus=to,ReasonCode=reason,ActorId=actor,CreatedAt=now,RelatedId=related});
 private async Task<PipelineRunDto> RunViewAsync(ReleasePipelineRun run,CancellationToken ct)
 {var stages=await db.Set<ReleasePipelineRunStage>().Where(s=>s.RunId==run.Id).OrderBy(s=>s.StageOrder).Take(8).ToArrayAsync(ct);var ids=stages.Where(s=>s.CurrentAttemptId!=null).Select(s=>s.CurrentAttemptId!.Value).ToArray();var attempts=await db.Set<ReleasePipelineStageAttempt>().Where(a=>ids.Contains(a.Id)).ToArrayAsync(ct);return PipelineRunService.View(run,stages,attempts);}
 private static PipelineStageProfile Profile(ReleasePipelineRunStage stage)=>System.Text.Json.JsonSerializer.Deserialize<PipelineStageProfile>(stage.ProfileJson,CanonicalJson.Options)!;
 private static PipelineRunStageDto StageView(ReleasePipelineRunStage stage,ReleasePipelineStageAttempt attempt)=>new(stage.Id,stage.RunId,stage.StageOrder,stage.EnvironmentId,stage.SourceStageId,stage.StageArtifactId,stage.Status,stage.Revision,stage.CurrentAttemptId,attempt.ActivatedAt,attempt.DeadlineAt,stage.ProfileHash,Profile(stage));
}

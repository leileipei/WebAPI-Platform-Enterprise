using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Delivery.Pipelines;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Delivery.Pipelines;

/// <summary>Projects committed facts. It never submits, approves, publishes or materializes on behalf of a person.</summary>
public sealed class PipelineProjectionService(WebApiDbContext db,DeliveryLockCoordinator locks,
 PipelineStageService stageEvidence,ProductionVerificationService production,TimeProvider clock)
{
 public async Task ProjectRunAsync(Guid runId,CancellationToken ct)
 {
  if(db.Database.CurrentTransaction is not null)throw new InvalidOperationException("Use the caller-owned projection core within a command.");
  await using var tx=await db.Database.BeginTransactionAsync(ct);
  await ProjectWithinTransactionAsync(runId,ct);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);db.ChangeTracker.Clear();
 }
 public async Task<int> ScanAsync(int batchSize=50,CancellationToken ct=default)
 {
  if(batchSize is <1 or >100)throw new ArgumentOutOfRangeException(nameof(batchSize));
  var now=clock.GetUtcNow();var due=now.AddSeconds(-5);
  var ids=await db.Set<ReleasePipelineRun>().AsNoTracking().Where(r=>(r.Status=="Active"||r.Status=="Paused"||r.Status=="TimedOut")&&(r.ProjectionCheckedAt==null||r.ProjectionCheckedAt<=due))
   .OrderBy(r=>r.ProjectionCheckedAt??r.CreatedAt).ThenBy(r=>r.Id).Select(r=>r.Id).Take(batchSize).ToArrayAsync(ct);var count=0;
  foreach(var id in ids){await using var tx=await db.Database.BeginTransactionAsync(ct);var initial=await db.Set<ReleasePipelineRun>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct);if(initial is null){await tx.CommitAsync(ct);continue;}await locks.LockProjectAsync(initial.ProjectId,ct);var run=await db.Set<ReleasePipelineRun>().SingleAsync(r=>r.Id==id,ct);if(PipelineStateRules.IsTerminal(run.Status)||run.ProjectionCheckedAt>due){await tx.CommitAsync(ct);db.ChangeTracker.Clear();continue;}await ProjectWithinTransactionAsync(id,ct);run.ProjectionCheckedAt=clock.GetUtcNow();await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);db.ChangeTracker.Clear();count++;}return count;
 }
 internal async Task ProjectWithinTransactionAsync(Guid runId,CancellationToken ct)
 {
  if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Projection requires a transaction.");
  var initial=await db.Set<ReleasePipelineRun>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==runId,ct);if(initial is null)return;
  await locks.LockProjectAsync(initial.ProjectId,ct);
  var environments=await db.Set<ReleasePipelineRunStage>().Where(s=>s.RunId==runId).Select(s=>s.EnvironmentId).ToArrayAsync(ct);await locks.LockEnvironmentsAsync(environments,ct);
  var run=await db.Set<ReleasePipelineRun>().SingleAsync(r=>r.Id==runId,ct);
  var stages=await db.Set<ReleasePipelineRunStage>().Where(s=>s.RunId==runId).OrderBy(s=>s.StageOrder).ToArrayAsync(ct);
  var revoked=await db.Set<ReleaseTestAcceptance>().Where(a=>a.Status=="Revoked"&&db.Set<ReleasePipelineRunStage>().Any(s=>s.Id==a.PipelineRunStageId&&s.RunId==runId&&s.StageOrder<=run.CurrentStageOrder)).ToArrayAsync(ct);
  foreach(var acceptance in revoked)await RevokeWithinTransactionAsync(db,acceptance,acceptance.ActedBy,clock.GetUtcNow(),ct);
  if(PipelineStateRules.IsTerminal(run.Status))return;
  var stage=stages.Single(s=>s.StageOrder==run.CurrentStageOrder);if(stage.CurrentAttemptId is not Guid attemptId)return;
  var attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==attemptId&&a.RunStageId==stage.Id,ct);var now=clock.GetUtcNow();
  if(attempt.DeadlineAt<=now&&run.Status is "Active" or "Paused"){Stop(run,stage,attempt,"TimedOut","TimedOut","stage_wait_timeout",now);return;}
  if(run.Status!="Active"||attempt.Status!="Active")return;
  var profile=System.Text.Json.JsonSerializer.Deserialize<PipelineStageProfile>(stage.ProfileJson,CanonicalJson.Options)!;
  var promotion=await PipelineAttemptFacts.FormalAsync(db,attempt,ct);
  ReleaseRecord? release=attempt.ActualReleaseId is Guid releaseId?await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==releaseId&&r.EnvironmentId==stage.EnvironmentId,ct):null;
  if((promotion?.Status=="Cancelled"||release?.Status=="Cancelled")&&release?.DeploymentSequence is not >0){Stop(run,stage,attempt,"Paused","Rejected","stage_candidate_cancelled",now);return;}
  if(promotion?.Status=="Rejected"||release?.Status=="Rejected"){Stop(run,stage,attempt,"Paused","Rejected","stage_approval_rejected",now);return;}
  if(release?.Status=="Failed"){Stop(run,stage,attempt,"Paused","DeploymentFailed","stage_deployment_failed",now);return;}
  if(release?.Status is "Building" or "Publishing"){Change(stage,attempt,"Deploying","stage_deploying",now,release.Id);return;}
  if(release?.Status=="Succeeded")
  {
   if(profile.IsProduction)
   {
    if(promotion is null)return;
    var qualified=false;try{qualified=await production.IsCurrentStageCompletedAsync(promotion.Id,ct);}catch(ApiException e)when(e.Status is 401 or 403 or 404 or 409 or 422){Change(stage,attempt,"AwaitingVerification","stage_actual_context_unconfirmed",now,release.Id);return;}
    if(promotion.Status=="VerificationFailed"){Stop(run,stage,attempt,"Paused","VerificationFailed","stage_production_verification_failed",now);return;}
    if(!qualified){Change(stage,attempt,"AwaitingVerification","stage_acknowledged",now,release.Id);return;}
   }
   else
   {
    try{if(stage.StageArtifactId is not null&&await stageEvidence.HasCurrentFailedEvidenceAsync(stage.Id,ct)){Stop(run,stage,attempt,"Paused","VerificationFailed","stage_test_failed",now);return;}}catch(ApiException e)when(e.Status is 401 or 403 or 404 or 409 or 422){/* Unqualified or stale evidence cannot project a failure. */}
    if(attempt.AcceptanceId is not Guid acceptanceId){Change(stage,attempt,stage.StageOrder==1?"AwaitingEvidence":"AwaitingVerification","stage_evidence_required",now,release.Id);return;}
    var acceptance=await db.Set<ReleaseTestAcceptance>().SingleAsync(a=>a.Id==acceptanceId&&a.PipelineRunStageId==stage.Id,ct);
    if(acceptance.Status=="Rejected"){Stop(run,stage,attempt,"Paused","Rejected","stage_acceptance_rejected",now);return;}
    if(acceptance.Status!="Accepted"){Change(stage,attempt,"AwaitingAcceptance","stage_acceptance_required",now,acceptance.Id);return;}
    try{await stageEvidence.RequireAcceptedCurrentAsync(acceptance,new ActorContext(acceptance.RequestedBy,"pipeline-projection"),ct);}
    catch(ApiException e)when(e.Status is 401 or 403 or 404 or 409 or 422){Change(stage,attempt,"AwaitingAcceptance","stage_evidence_not_current",now,acceptance.Id);return;}
   }
   Change(stage,attempt,"Passed","stage_passed",now,release.Id);attempt.Status="Passed";attempt.Revision++;
   var next=stages.SingleOrDefault(s=>s.StageOrder==stage.StageOrder+1);run.Revision++;
   if(next is null){var before=run.Status;run.Status="Completed";run.CompletedAt=now;Event(run,stage,attempt,before,"Completed","pipeline_completed",now);return;}
   if(next.Status!="Pending"||next.CurrentAttemptId is not null)throw new InvalidOperationException("Future stage was activated before its predecessor passed.");
   var nextProfile=System.Text.Json.JsonSerializer.Deserialize<PipelineStageProfile>(next.ProfileJson,CanonicalJson.Options)!;
   var activated=new ReleasePipelineStageAttempt{RunId=run.Id,ProjectId=run.ProjectId,RunStageId=next.Id,EnvironmentId=next.EnvironmentId,AttemptNo=1,ActivatedAt=now,DeadlineAt=now.AddMinutes(nextProfile.WaitTimeoutMinutes)};db.Add(activated);
   await db.SaveChangesAsync(ct);next.CurrentAttemptId=activated.Id;Change(next,activated,"AwaitingMapping","stage_activated",now,stage.Id);run.CurrentStageOrder=next.StageOrder;return;
  }
  if(promotion is not null){var next=promotion.Status switch{"WaitingApproval"=>"AwaitingApproval","Ready"=>"ReadyToDeploy","Draft" when promotion.MappingRevision>0=>"AwaitingPrecheck",_=>"AwaitingMapping"};Change(stage,attempt,next,"stage_candidate_state",now,promotion.Id);}
 }
 private void Change(ReleasePipelineRunStage stage,ReleasePipelineStageAttempt attempt,string next,string reason,DateTimeOffset now,Guid? related=null)
 {if(stage.Status==next)return;var before=stage.Status;stage.Status=next;stage.Revision++;db.Add(new ReleasePipelineEvent{RunId=stage.RunId,ProjectId=stage.ProjectId,StageId=stage.Id,AttemptId=attempt.Id,FromStatus=before,ToStatus=next,ReasonCode=reason,RelatedId=related,CreatedAt=now});}
 private void Stop(ReleasePipelineRun run,ReleasePipelineRunStage stage,ReleasePipelineStageAttempt attempt,string runStatus,string stageStatus,string reason,DateTimeOffset now)
 {var before=run.Status;run.Status=runStatus;run.Revision++;Change(stage,attempt,stageStatus,reason,now);attempt.Status=stageStatus;attempt.Revision++;Event(run,stage,attempt,before,runStatus,"pipeline_"+reason,now);}
 private void Event(ReleasePipelineRun run,ReleasePipelineRunStage stage,ReleasePipelineStageAttempt attempt,string from,string to,string reason,DateTimeOffset now)=>db.Add(new ReleasePipelineEvent{RunId=run.Id,ProjectId=run.ProjectId,StageId=stage.Id,AttemptId=attempt.Id,FromStatus=from,ToStatus=to,ReasonCode=reason,CreatedAt=now});
 internal static async Task RevokeWithinTransactionAsync(WebApiDbContext db,ReleaseTestAcceptance acceptance,Guid? actor,DateTimeOffset now,CancellationToken ct)
 {
  if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Revocation requires the caller transaction and project/environment locks.");
  if(acceptance.PipelineRunStageId is not Guid stageId)return;var source=await db.Set<ReleasePipelineRunStage>().SingleAsync(s=>s.Id==stageId,ct);var run=await db.Set<ReleasePipelineRun>().SingleAsync(r=>r.Id==source.RunId,ct);
  if(await db.Set<ReleasePipelineEvent>().AnyAsync(e=>e.RunId==run.Id&&e.RelatedId==acceptance.Id&&e.ReasonCode=="pipeline_acceptance_revoked",ct)||db.ChangeTracker.Entries<ReleasePipelineEvent>().Any(e=>e.Entity.RunId==run.Id&&e.Entity.RelatedId==acceptance.Id&&e.Entity.ReasonCode=="pipeline_acceptance_revoked"))return;
  var before=run.Status;if(!PipelineStateRules.IsTerminal(run.Status)){run.Status="Invalidated";run.Revision++;run.CompletedAt=now;}
  db.Add(new ReleasePipelineEvent{RunId=run.Id,ProjectId=run.ProjectId,StageId=source.Id,AttemptId=acceptance.StageAttemptId,FromStatus=before,ToStatus=run.Status,ReasonCode="pipeline_acceptance_revoked",ActorId=actor,RelatedId=acceptance.Id,CreatedAt=now});
  if(before=="Completed")return;
  var candidates=await db.Set<ReleasePromotion>().Where(p=>p.GateOrigin=="PipelineRunStage"&&db.Set<ReleasePipelineRunStage>().Any(s=>s.Id==p.PipelineRunStageId&&s.RunId==run.Id&&s.StageOrder>source.StageOrder)&&p.Status!="Cancelled"&&p.Status!="Completed").ToArrayAsync(ct);
  foreach(var p in candidates){var release=p.TargetReleaseId is Guid id?await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id,ct):null;if(release is not null&&release.Status is not("Draft" or "WaitingApproval" or "Ready" or "Building"))continue;var prior=p.Status;p.Status="Cancelled";p.Revision++;p.CompletedAt=now;if(release is not null){release.Status="Cancelled";release.CompletedAt=now;release.FailureCode="pipeline_acceptance_revoked";}db.Add(new ReleasePromotionEvent{OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,EnvironmentId=p.TargetEnvironmentId,PromotionId=p.Id,Phase="PreExecution",FromStatus=prior,ToStatus="Cancelled",ReasonCode="pipeline_acceptance_revoked",ActorId=actor,ReleaseId=release?.Id});}
 }
}

using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Delivery;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery.Pipelines;
public sealed class PipelineRunService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,
 AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,
 DeliveryLockCoordinator locks,PipelineDefinitionService definitions,ReleaseArtifactService artifacts)
{
 private static ApiException Changed()=>new(409,"pipeline_start_conditions_changed","流水线版本或来源运行事实已变化，请核对生效版本与当前制品。");
 public async Task<PipelineRunDto> StartAsync(CreatePipelineRunRequest request,ActorContext actor,CancellationToken ct)
 {
  var version=await VersionAsync(request.PipelineVersionId,ct);var content=Content(version);var sourceId=content.Profiles[0].EnvironmentId;var scope=await scopes.EnvironmentAsync(sourceId,ct);
  if(scope.ProjectId!=version.ProjectId)throw ScopeResolver.Missing();
  return await commands.ExecuteAsync(actor,scope,"pipeline.run.start",async(_,token)=>{
   await locks.LockProjectAsync(version.ProjectId,token);await locks.LockEnvironmentsAsync(content.Profiles.Select(p=>p.EnvironmentId).ToArray(),token);
   await auth.RequireAsync(actor,"pipeline.run",new("environment",sourceId,scope),token);await auth.RequireAsync(actor,"release.create",new("environment",sourceId,scope),token);
   await definitions.RequireEnvironmentReadsAsync(version.ProjectId,content.Definition,actor,token);
   var artifact=await artifacts.GetAsync(request.RootArtifactId,actor,token);if(artifact.ProjectId!=version.ProjectId||artifact.OrganizationId!=scope.OrganizationId)throw ScopeResolver.Missing();
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.run.start",requestContext.IdempotencyKey),CanonicalJson.Serialize(request),async inner=>{
    var policy=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(p=>p.ProjectId==version.ProjectId,inner);
    if(policy is null||policy.Mode!="PipelineRequired"||policy.ActivePipelineVersionId!=version.Id)throw Changed();
    await PipelineActivationService.RequireIdleAsync(db,version.ProjectId,inner);
    if(artifact.SourceEnvironmentId!=sourceId)throw new ApiException(422,"pipeline_root_environment_mismatch","根制品必须来自本流水线第一个非生产环境。");
    var live=await definitions.FreezeAsync(version.ProjectId,content.Definition,actor,inner);if(PipelineDefinitionService.Hash(live)!=version.DefinitionHash)throw Changed();
    var source=await artifacts.RequireSourceAsync(artifact.SourceReleaseId,actor,inner);
    if(source.Deployment.Snapshot.Hash!=artifact.SourceSnapshotHash||ReleaseArtifactCanonicalizer.Hash(source.Content)!=artifact.ArtifactHash)throw Changed();
    var now=DateTimeOffset.UtcNow;var run=new ReleasePipelineRun{OrganizationId=scope.OrganizationId,ProjectId=version.ProjectId,PipelineVersionId=version.Id,DefinitionHash=version.DefinitionHash,RootArtifactId=artifact.Id,RootArtifactHash=artifact.ArtifactHash,SourceEnvironmentId=sourceId,SourceReleaseId=source.Deployment.Release.Id,SourceConfigVersion=source.Deployment.Release.ToConfigVersion,SourceDeploymentSequence=source.Deployment.Release.DeploymentSequence!.Value,PolicyRevision=policy.Revision,CreatedBy=actor.UserId,CreatedAt=now};db.Add(run);
    var stages=new List<ReleasePipelineRunStage>();Guid? previous=null;
    foreach(var profile in content.Profiles.OrderBy(p=>p.Order)){var stage=new ReleasePipelineRunStage{RunId=run.Id,ProjectId=run.ProjectId,StageOrder=profile.Order,EnvironmentId=profile.EnvironmentId,SourceStageId=previous,ProfileJson=PipelineDefinitionService.Json(profile),ProfileHash=PipelineDefinitionService.Hash(profile),Status=profile.Order==1?"AwaitingEvidence":"Pending",StageArtifactId=profile.Order==1?artifact.Id:null};stages.Add(stage);db.Add(stage);previous=stage.Id;}
    var first=stages[0];var attempt=new ReleasePipelineStageAttempt{RunStageId=first.Id,RunId=run.Id,ProjectId=run.ProjectId,EnvironmentId=first.EnvironmentId,AttemptNo=1,ActivatedAt=now,DeadlineAt=now.AddMinutes(content.Profiles[0].WaitTimeoutMinutes),ArtifactId=artifact.Id,ActualReleaseId=source.Deployment.Release.Id,ContextJson=PipelineDefinitionService.Json(new{artifact.Id,artifact.ArtifactHash,run.SourceReleaseId,run.SourceConfigVersion,run.SourceDeploymentSequence,first.ProfileHash})};db.Add(attempt);
    db.Add(new ReleasePipelineEvent{RunId=run.Id,ProjectId=run.ProjectId,StageId=first.Id,AttemptId=attempt.Id,ToStatus="AwaitingEvidence",ReasonCode="source_actual_imported",ActorId=actor.UserId,RelatedId=source.Deployment.Release.Id,CreatedAt=now});
    // Break the Stage/current-attempt FK cycle without leaving this command's transaction.
    await db.SaveChangesAsync(inner);first.CurrentAttemptId=attempt.Id;return View(run,stages,[attempt]);
   },token);
  },ct);
 }
 // P10 extends this full-chain projection to restricted, database-filtered reads.
 public async Task<PipelineRunDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct)
 {
  using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(10));var token=budget.Token;
  var run=await db.Set<ReleasePipelineRun>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,token)??throw ScopeResolver.Missing();
  var scope=await scopes.EnvironmentAsync(run.SourceEnvironmentId,token);if(!await auth.CanAsync(actor,"pipeline.read",new("environment",run.SourceEnvironmentId,scope),token))throw ScopeResolver.Missing();
  var version=await VersionAsync(run.PipelineVersionId,token);var content=Content(version);await definitions.RequireEnvironmentReadsAsync(run.ProjectId,content.Definition,actor,token,true);await artifacts.GetAsync(run.RootArtifactId,actor,token);
  var stages=await db.Set<ReleasePipelineRunStage>().AsNoTracking().Where(s=>s.RunId==id&&s.ProjectId==run.ProjectId).OrderBy(s=>s.StageOrder).Take(8).ToArrayAsync(token);var ids=stages.Where(s=>s.CurrentAttemptId!=null).Select(s=>s.CurrentAttemptId!.Value).ToArray();var attempts=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().Where(a=>ids.Contains(a.Id)&&a.RunId==id).ToArrayAsync(token);return View(run,stages,attempts);
 }
 internal static PipelineRunDto View(ReleasePipelineRun run,IReadOnlyList<ReleasePipelineRunStage> stages,IReadOnlyList<ReleasePipelineStageAttempt> attempts)=>new(run.Id,run.ProjectId,run.PipelineVersionId,run.DefinitionHash,run.RootArtifactId,run.RootArtifactHash,run.SourceEnvironmentId,run.SourceReleaseId,run.SourceConfigVersion,run.SourceDeploymentSequence,run.PolicyRevision,run.Status,run.CurrentStageOrder,run.Revision,run.CreatedBy,run.CreatedAt,stages.OrderBy(s=>s.StageOrder).Select(s=>{var a=attempts.SingleOrDefault(a=>a.Id==s.CurrentAttemptId&&a.RunStageId==s.Id);return new PipelineRunStageDto(s.Id,s.RunId,s.StageOrder,s.EnvironmentId,s.SourceStageId,s.StageArtifactId,s.Status,s.Revision,s.CurrentAttemptId,a?.ActivatedAt,a?.DeadlineAt,s.ProfileHash,System.Text.Json.JsonSerializer.Deserialize<PipelineStageProfile>(s.ProfileJson,CanonicalJson.Options));}).ToArray());
 private async Task<ReleasePipelineVersion> VersionAsync(Guid id,CancellationToken ct)=>await db.Set<ReleasePipelineVersion>().AsNoTracking().SingleOrDefaultAsync(v=>v.Id==id,ct)??throw ScopeResolver.Missing();
 private static PipelineVersionContent Content(ReleasePipelineVersion version){var c=PipelineDefinitionService.Content(version);if(c.Definition?.Stages is null||c.Profiles is null||c.Profiles.Count is <2 or >8||PipelineDefinitionService.Hash(c)!=version.DefinitionHash)throw Changed();return c;}
}

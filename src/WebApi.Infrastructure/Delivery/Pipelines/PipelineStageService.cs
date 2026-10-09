using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery.Pipelines;
public sealed class PipelineStageService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,AuditedCommandExecutor commands,
 IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,DeliveryLockCoordinator locks,DeliveryGateContextResolver gates,
 ReleaseArtifactService artifacts,VerificationReportStore reports)
{
 private sealed record Materialization(ReleaseArtifactDto? Artifact,string? FailureCode=null);
 private static ApiException Stale()=>new(409,"pipeline_evidence_not_current","阶段证据、尝试、实际部署或入口已变化，请刷新上下文后重新办理。");
 private async Task<ReleasePipelineRunStage> StageAsync(Guid id,CancellationToken ct)=>await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleOrDefaultAsync(s=>s.Id==id,ct)??throw ScopeResolver.Missing();
 private async Task LockAsync(ReleasePipelineRunStage stage,CancellationToken ct)
 {await locks.LockProjectAsync(stage.ProjectId,ct);var environments=new List<Guid>{stage.EnvironmentId};if(stage.SourceStageId is Guid source)environments.Add(await db.Set<ReleasePipelineRunStage>().Where(s=>s.Id==source&&s.RunId==stage.RunId).Select(s=>s.EnvironmentId).SingleAsync(ct));await locks.LockEnvironmentsAsync(environments,ct);}
 public async Task<PipelineVerificationContextDto> GetVerificationContextAsync(Guid stageId,ActorContext actor,CancellationToken ct)
 {var gate=await gates.ResolveStageAsync(stageId,actor,ct);return await RuntimeContextAsync(stageId,gate,actor,ct);}
 private async Task<PipelineVerificationContextDto> RuntimeContextAsync(Guid stageId,DeliveryGateContext gate,ActorContext actor,CancellationToken ct)
 {
  var stage=await StageAsync(stageId,ct);var profile=System.Text.Json.JsonSerializer.Deserialize<PipelineStageProfile>(stage.ProfileJson,CanonicalJson.Options)!;
  if(profile.IsProduction)throw new ApiException(422,"pipeline_production_verification_required","生产阶段请使用正式晋级的生产验证入口。");
  if(stage.StageArtifactId is not Guid artifactId||stage.CurrentAttemptId is not Guid attemptId)throw new ApiException(409,"pipeline_stage_artifact_required","当前非生产阶段需先从实际已确认发布生成并绑定制品。");
  var attempt=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().SingleAsync(a=>a.Id==attemptId&&a.RunStageId==stage.Id,ct);var artifact=await artifacts.GetAsync(artifactId,actor,ct);
  if(artifact.SourceEnvironmentId!=stage.EnvironmentId||attempt.ArtifactId!=artifactId||attempt.ActualReleaseId!=artifact.SourceReleaseId||artifact.ArtifactHash!=gate.Pipeline!.RootArtifactHash)throw Stale();
  var source=await artifacts.RequireSourceAsync(artifact.SourceReleaseId,actor,ct);if(source.Deployment.Snapshot.Hash!=artifact.SourceSnapshotHash||WebApi.Domain.Delivery.ReleaseArtifactCanonicalizer.Hash(source.Content)!=artifact.ArtifactHash||source.Deployment.Release.CompletedAt is not DateTimeOffset deployed)throw Stale();
  var floor=attempt.AttemptNo>1&&attempt.ActivatedAt>deployed?attempt.ActivatedAt:deployed;var environment=source.Deployment.Environment;
  var value=new PipelineVerificationContextDto("",stage.RunId,stage.Id,attempt.Id,artifact.Id,gate.PolicyRevision,gate.Pipeline.DefinitionHash,gate.Pipeline.RootArtifactHash,source.Deployment.Release.Id,source.Deployment.Release.ToConfigVersion,source.Deployment.Release.DeploymentSequence!.Value,source.Deployment.Snapshot.Hash,environment.AccessAddressRevision,environment.GatewayPublicUrl??"",environment.BasePath,stage.ProfileHash,profile.RequiredTypes,profile.EvidenceValidityMinutes,floor,attempt.DeadlineAt);
  return value with{ContextHash=Hash(value)};
 }
 private static string Hash<T>(T value)=>Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(value)));
 private static void RequireHash(string? expected)
 {if(expected is null||expected.Length!=64||expected.Any(c=>c is not(>= '0' and <= '9') and not(>= 'a' and <= 'f')))throw new ApiException(422,"pipeline_verification_context_required","请先查看当前阶段的实际部署及尝试上下文。");}
 public async Task<ReleaseVerificationDto> RecordVerificationAsync(Guid stageId,PipelineStageVerificationRequest request,ActorContext actor,CancellationToken ct)
 {
  var initial=await StageAsync(stageId,ct);var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);RequireHash(request.ExpectedContextHash);
  if(request.Evidence.ExpectedContextHash is{} nested&&nested!=request.ExpectedContextHash)throw new ApiException(422,"pipeline_context_conflict","验证正文与阶段摘要不一致，请只使用当前阶段摘要。");
  return await commands.ExecuteAsync(actor,scope,"pipeline.stage.verification",async(_,token)=>{
   await LockAsync(initial,token);var gate=await gates.ResolveStageAsync(stageId,actor,token);await gates.RequireStageWritableAsync(gate,actor,token);await auth.RequireAsync(actor,"release.test.record",new("environment",initial.EnvironmentId,scope),token);
   var context=await RuntimeContextAsync(stageId,gate,actor,token);if(context.ContextHash!=request.ExpectedContextHash)throw Stale();ReleaseVerificationService.Validate(request.Evidence,context.RequiredTypes);
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.stage.verification",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{stageId,request}),async inner=>{
    if(request.Evidence.FinishedAt<context.EvidenceNotBefore)throw new ApiException(422,"verification_predates_deployment","测试完成时间不能早于实际部署完成或本次重新办理窗口。");
    VerificationReport? report=null;if(request.Evidence.ReportId is Guid reportId){report=await reports.GetMetadataAsync(reportId,actor,inner);if(report.ArtifactId!=context.ArtifactId||report.PromotionId is not null||report.EnvironmentId!=initial.EnvironmentId)throw ScopeResolver.Missing();}
    var row=new ReleaseVerification{OrganizationId=scope.OrganizationId,ProjectId=initial.ProjectId,ArtifactId=context.ArtifactId,ReleaseId=context.ReleaseId,EnvironmentId=initial.EnvironmentId,ConfigVersion=context.ConfigVersion,DeploymentSequence=context.DeploymentSequence,SnapshotHash=context.SnapshotHash,AccessAddressRevision=context.AccessAddressRevision,AccessContextJson=PipelineDefinitionService.Json(context),PolicyRevision=gate.PolicyRevision,Phase="SourceTest",Type=request.Evidence.Type,Result=request.Evidence.Result,IsManual=true,ReportId=report?.Id,ReportHash=report?.Sha256,Comment=request.Evidence.Comment??"",StartedAt=request.Evidence.StartedAt.ToUniversalTime(),FinishedAt=request.Evidence.FinishedAt.ToUniversalTime(),ExpiresAt=request.Evidence.FinishedAt.ToUniversalTime().AddMinutes(context.EvidenceValidityMinutes),CreatedBy=actor.UserId,PipelineRunStageId=stageId,StageAttemptId=context.CurrentAttemptId,ProfileHash=context.ProfileHash};db.Add(row);Append(initial,context.CurrentAttemptId,"stage_test_"+row.Result.ToLowerInvariant(),actor.UserId,row.Id);return ReleaseVerificationService.View(row);
   },token);
  },ct);
 }
 public async Task<TestAcceptanceDto> RequestAcceptanceAsync(Guid stageId,PipelineAcceptanceRequest request,ActorContext actor,CancellationToken ct)
 {
  RequireHash(request.ExpectedContextHash);if(request.VerificationIds is null||request.VerificationIds.Count is <1 or >3||request.VerificationIds.Distinct().Count()!=request.VerificationIds.Count)throw new ApiException(422,"invalid_test_evidence","请提供本阶段不重复的完整测试证据。");
  var initial=await StageAsync(stageId,ct);var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);
  return await commands.ExecuteAsync(actor,scope,"pipeline.stage.acceptance.request",async(_,token)=>{
   await LockAsync(initial,token);var gate=await gates.ResolveStageAsync(stageId,actor,token);await gates.RequireStageWritableAsync(gate,actor,token);await auth.RequireAsync(actor,"release.test.record",new("environment",initial.EnvironmentId,scope),token);var context=await RuntimeContextAsync(stageId,gate,actor,token);if(context.ContextHash!=request.ExpectedContextHash)throw Stale();
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.stage.acceptance.request",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{stageId,request= request with{VerificationIds=request.VerificationIds.Order().ToArray()}}),async inner=>{
    var artifact=await artifacts.GetAsync(context.ArtifactId,actor,inner);var facts=await RequireEvidenceAsync(context,request.VerificationIds,actor,inner);
    var row=new ReleaseTestAcceptance{OrganizationId=scope.OrganizationId,ProjectId=initial.ProjectId,ArtifactId=context.ArtifactId,SourceEnvironmentId=initial.EnvironmentId,VerificationIds=request.VerificationIds.Order().ToArray(),EvidenceHash=TestAcceptanceService.EvidenceHash(artifact,facts),PolicyRevision=gate.PolicyRevision,RequestedBy=actor.UserId,PipelineRunStageId=stageId,StageAttemptId=context.CurrentAttemptId,ProfileHash=context.ProfileHash};db.Add(row);
    var stage=await db.Set<ReleasePipelineRunStage>().SingleAsync(s=>s.Id==stageId,inner);var before=stage.Status;stage.Status="AwaitingAcceptance";stage.Revision++;var attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==context.CurrentAttemptId,inner);attempt.AcceptanceId=row.Id;attempt.Revision++;Append(stage,attempt.Id,"stage_acceptance_requested",actor.UserId,row.Id,before);return TestAcceptanceService.View(row,artifact,facts);
   },token);
  },ct);
 }
 private async Task<ReleaseVerification[]> RequireEvidenceAsync(PipelineVerificationContextDto context,IReadOnlyList<Guid> ids,ActorContext actor,CancellationToken ct)
 {
  var facts=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>ids.Contains(v.Id)&&v.PipelineRunStageId==context.StageId&&v.ArtifactId==context.ArtifactId&&v.Phase=="SourceTest").ToArrayAsync(ct);if(facts.Length!=ids.Count)throw ScopeResolver.Missing();
  if(facts.Select(v=>v.Type).Distinct().Count()!=facts.Length||context.RequiredTypes.Except(facts.Select(v=>v.Type)).Any())throw new ApiException(422,"test_evidence_incomplete","本阶段必需测试尚未齐全。");var now=DateTimeOffset.UtcNow;
  var evidenceEnvironment=(await StageAsync(context.StageId,ct)).EnvironmentId;var evidenceScope=await scopes.EnvironmentAsync(evidenceEnvironment,ct);
  foreach(var recorder in facts.Select(v=>v.CreatedBy).Distinct()){try{var author=new ActorContext(recorder,actor.TraceId);await auth.RequireAsync(author,"release.test.record",new("environment",evidenceEnvironment,evidenceScope),ct);await artifacts.GetAsync(context.ArtifactId,author,ct);}catch(ApiException e)when(e.Status is 401 or 403 or 404 or 409){throw new ApiException(409,"pipeline_evidence_authority_changed","阶段测试登记人的当前资格已变化，请重新登记证据。");}}
  foreach(var fact in facts){var latest=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>v.PipelineRunStageId==context.StageId&&v.StageAttemptId==context.CurrentAttemptId&&v.ArtifactId==context.ArtifactId&&v.Phase=="SourceTest"&&v.Type==fact.Type).OrderByDescending(v=>v.CreatedAt).ThenByDescending(v=>v.Id).Select(v=>v.Id).FirstOrDefaultAsync(ct);
   if(fact.StageAttemptId!=context.CurrentAttemptId||fact.ProfileHash!=context.ProfileHash||fact.PolicyRevision!=context.PolicyRevision||fact.Result!="Passed"||!fact.IsManual||fact.ExpiresAt<=now||fact.FinishedAt<context.EvidenceNotBefore||fact.StartedAt>fact.FinishedAt||fact.FinishedAt>now||fact.ReleaseId!=context.ReleaseId||fact.ConfigVersion!=context.ConfigVersion||fact.DeploymentSequence!=context.DeploymentSequence||fact.SnapshotHash!=context.SnapshotHash||fact.AccessAddressRevision!=context.AccessAddressRevision||!ContextMatches(fact.AccessContextJson,context)||fact.Id!=latest)throw Stale();
   if(fact.ReportId is Guid reportId){var report=await reports.GetMetadataAsync(reportId,actor,ct);if(report.ArtifactId!=context.ArtifactId||report.PromotionId is not null||report.EnvironmentId!=fact.EnvironmentId||report.Sha256!=fact.ReportHash)throw Stale();}
  }return facts;
 }
 internal async Task<bool> CanRequestAcceptanceAsync(Guid id,ActorContext actor,CancellationToken ct)
 {
  var context=await GetVerificationContextAsync(id,actor,ct);var ids=new List<Guid>();
  foreach(var type in context.RequiredTypes){var latest=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>v.PipelineRunStageId==id&&v.StageAttemptId==context.CurrentAttemptId&&v.ArtifactId==context.ArtifactId&&v.Phase=="SourceTest"&&v.Type==type).OrderByDescending(v=>v.CreatedAt).ThenByDescending(v=>v.Id).Select(v=>(Guid?)v.Id).FirstOrDefaultAsync(ct);if(latest is not Guid fact)return false;ids.Add(fact);}
  await RequireEvidenceAsync(context,ids,actor,ct);return true;
 }
 private static bool ContextMatches(string json,PipelineVerificationContextDto current)
 {try{var stored=System.Text.Json.JsonSerializer.Deserialize<PipelineVerificationContextDto>(json,CanonicalJson.Options);return stored is not null&&stored.ContextHash==current.ContextHash&&Hash(stored with{ContextHash=""})==current.ContextHash;}catch(System.Text.Json.JsonException){return false;}}
 private async Task RequireIndependentAsync(ReleaseTestAcceptance row,DeliveryGateContext gate,ActorContext actor,CancellationToken ct)
 {
  var attempt=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().SingleAsync(a=>a.Id==row.StageAttemptId&&a.RunStageId==row.PipelineRunStageId,ct);Guid? applicant=null;
  var origin=attempt;while(origin.OriginAttemptId is Guid prior){var previous=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().SingleAsync(a=>a.Id==prior&&a.RunStageId==origin.RunStageId,ct);if(previous.AttemptNo>=origin.AttemptNo)throw Stale();origin=previous;}
  if(origin.PromotionId is Guid p)applicant=await db.Set<ReleasePromotion>().Where(x=>x.Id==p).Select(x=>(Guid?)x.RequestedBy).SingleAsync(ct);
  else if(attempt.ActualReleaseId is Guid release)applicant=await db.Set<ReleaseRecord>().Where(r=>r.Id==release).Select(r=>(Guid?)r.RequestedBy).SingleAsync(ct);
  if(actor.UserId==row.RequestedBy||actor.UserId==gate.Pipeline!.RunCreatedBy||actor.UserId==applicant)throw new ApiException(403,"independent_test_acceptor_required","阶段验收人须不同于运行创建人、验收请求人和本阶段发布申请人，管理员同样适用。");
 }
 internal async Task<TestAcceptanceDto> AcceptanceViewAsync(ReleaseTestAcceptance row,ActorContext actor,CancellationToken ct)
 {
  var gate=await gates.ResolveHistoricalStageAsync(row.PipelineRunStageId!.Value,actor,ct);var artifact=await artifacts.GetAsync(row.ArtifactId,actor,ct);var facts=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>row.VerificationIds.Contains(v.Id)&&v.PipelineRunStageId==row.PipelineRunStageId&&v.ArtifactId==row.ArtifactId).ToArrayAsync(ct);var reasons=new List<string>();var allowed=true;
  try{await RequireIndependentAsync(row,gate,actor,ct);await auth.RequireAsync(actor,"release.test.accept",new("environment",row.SourceEnvironmentId,await scopes.EnvironmentAsync(row.SourceEnvironmentId,ct)),ct);}catch(ApiException e)when(e.Status==403){allowed=false;reasons.Add(e.Code);}
  var current=false;try{var live=await gates.ResolveStageAsync(row.PipelineRunStageId.Value,actor,ct);await gates.RequireStageWritableAsync(live,actor,ct);var context=await RuntimeContextAsync(row.PipelineRunStageId.Value,live,actor,ct);var evidence=await RequireEvidenceAsync(context,row.VerificationIds,actor,ct);var attempt=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().SingleAsync(a=>a.Id==context.CurrentAttemptId,ct);current=attempt.AcceptanceId==row.Id&&row.ArtifactId==context.ArtifactId&&row.StageAttemptId==context.CurrentAttemptId&&row.ProfileHash==context.ProfileHash&&row.PolicyRevision==live.PolicyRevision&&row.EvidenceHash==TestAcceptanceService.EvidenceHash(artifact,evidence);}catch(ApiException e)when(e.Status is 409 or 422){reasons.Add(e.Code);}
  if(!current&&!reasons.Contains("pipeline_acceptance_not_current"))reasons.Add("pipeline_acceptance_not_current");
  return TestAcceptanceService.View(row,artifact,facts) with{CanAccept=allowed&&current&&row.Status=="Requested",CanReject=allowed&&current&&row.Status=="Requested",CanRevoke=allowed&&row.Status=="Accepted",ReasonCodes=reasons};
 }
 internal async Task<TestAcceptanceDto> ActAcceptanceWithinTransactionAsync(ReleaseTestAcceptance row,string action,string comment,ActorContext actor,CancellationToken ct)
 {
  if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Stage acceptance requires the caller transaction.");var gate=await gates.ResolveHistoricalStageAsync(row.PipelineRunStageId!.Value,actor,ct);await RequireIndependentAsync(row,gate,actor,ct);if(action=="revoke"?row.Status!="Accepted":row.Status!="Requested")throw new ApiException(409,"test_acceptance_state","当前验收状态不允许该操作。");
  var artifact=await artifacts.GetAsync(row.ArtifactId,actor,ct);ReleaseVerification[] evidence;
  if(action!="revoke"){var live=await gates.ResolveStageAsync(row.PipelineRunStageId.Value,actor,ct);await gates.RequireStageWritableAsync(live,actor,ct);if(row.StageAttemptId!=live.Pipeline!.CurrentAttemptId||!await db.Set<ReleasePipelineStageAttempt>().AnyAsync(a=>a.Id==row.StageAttemptId&&a.AcceptanceId==row.Id,ct))throw Stale();if(action=="accept"){var context=await RuntimeContextAsync(row.PipelineRunStageId.Value,live,actor,ct);evidence=await RequireEvidenceAsync(context,row.VerificationIds,actor,ct);if(row.ArtifactId!=context.ArtifactId||row.ProfileHash!=context.ProfileHash||row.PolicyRevision!=live.PolicyRevision||row.EvidenceHash!=TestAcceptanceService.EvidenceHash(artifact,evidence))throw Stale();}else evidence=await db.Set<ReleaseVerification>().Where(v=>row.VerificationIds.Contains(v.Id)&&v.PipelineRunStageId==row.PipelineRunStageId).ToArrayAsync(ct);}
  else evidence=await db.Set<ReleaseVerification>().Where(v=>row.VerificationIds.Contains(v.Id)&&v.PipelineRunStageId==row.PipelineRunStageId).ToArrayAsync(ct);
  row.Status=action switch{"accept"=>"Accepted","reject"=>"Rejected",_=>"Revoked"};row.Revision++;row.ActedBy=actor.UserId;row.ActedAt=DateTimeOffset.UtcNow;row.Comment=comment;var stage=await StageAsync(row.PipelineRunStageId.Value,ct);Append(stage,row.StageAttemptId,"stage_acceptance_"+row.Status.ToLowerInvariant(),actor.UserId,row.Id);if(action=="revoke")await PipelineProjectionService.RevokeWithinTransactionAsync(db,row,actor.UserId,DateTimeOffset.UtcNow,ct);return TestAcceptanceService.View(row,artifact,evidence);
 }
 internal async Task<bool> HasCurrentFailedEvidenceAsync(Guid stageId,CancellationToken ct)
 {
  var stage=await StageAsync(stageId,ct);var gate=await gates.ResolveStageFactsAsync(stageId,null,ct);var now=DateTimeOffset.UtcNow;
  foreach(var type in gate.TargetEvidence.RequiredTypes){var row=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>v.PipelineRunStageId==stageId&&v.StageAttemptId==stage.CurrentAttemptId&&v.Phase=="SourceTest"&&v.Type==type).OrderByDescending(v=>v.CreatedAt).ThenByDescending(v=>v.Id).FirstOrDefaultAsync(ct);
   if(row is null||row.Result!="Failed")continue;
   try{
    var recorder=new ActorContext(row.CreatedBy,"pipeline-evidence-authority");await auth.RequireAsync(recorder,"release.test.record",new("environment",stage.EnvironmentId,await scopes.EnvironmentAsync(stage.EnvironmentId,ct)),ct);var context=await RuntimeContextAsync(stageId,gate,recorder,ct);
    if(!row.IsManual||row.ExpiresAt<=now||row.FinishedAt<context.EvidenceNotBefore||row.StartedAt>row.FinishedAt||row.FinishedAt>now||row.ArtifactId!=context.ArtifactId||row.ReleaseId!=context.ReleaseId||row.ProfileHash!=context.ProfileHash||row.PolicyRevision!=context.PolicyRevision||row.ConfigVersion!=context.ConfigVersion||row.DeploymentSequence!=context.DeploymentSequence||row.SnapshotHash!=context.SnapshotHash||row.AccessAddressRevision!=context.AccessAddressRevision||!ContextMatches(row.AccessContextJson,context))continue;
    if(row.ReportId is Guid reportId){var report=await reports.GetMetadataAsync(reportId,recorder,ct);if(report.ArtifactId!=context.ArtifactId||report.EnvironmentId!=stage.EnvironmentId||report.PromotionId is not null||report.Sha256!=row.ReportHash)continue;}return true;
   }catch(ApiException e)when(e.Status is 401 or 403 or 404 or 409 or 422){/* Inspect other required types; unqualified facts cannot project a failure. */}
  }return false;
 }
 internal async Task<ReleaseTestAcceptance> RequireAcceptedCurrentAsync(ReleaseTestAcceptance row,ActorContext actor,CancellationToken ct)
 {
  var gate=await gates.ResolveStageFactsAsync(row.PipelineRunStageId!.Value,null,ct);if(row.Status!="Accepted"||row.ActedBy is not Guid acceptedBy)throw Stale();var context=await RuntimeContextAsync(row.PipelineRunStageId.Value,gate,actor,ct);var artifact=await artifacts.GetAsync(row.ArtifactId,actor,ct);var evidence=await RequireEvidenceAsync(context,row.VerificationIds,actor,ct);if(row.ArtifactId!=context.ArtifactId||row.StageAttemptId!=context.CurrentAttemptId||row.ProfileHash!=context.ProfileHash||row.PolicyRevision!=gate.PolicyRevision||row.EvidenceHash!=TestAcceptanceService.EvidenceHash(artifact,evidence)||!await db.Set<ReleasePipelineStageAttempt>().AnyAsync(a=>a.Id==context.CurrentAttemptId&&a.AcceptanceId==row.Id,ct))throw Stale();
  var acceptor=new ActorContext(acceptedBy,actor.TraceId);await artifacts.GetAsync(row.ArtifactId,acceptor,ct);await auth.RequireAsync(acceptor,"release.test.accept",new("environment",row.SourceEnvironmentId,await scopes.EnvironmentAsync(row.SourceEnvironmentId,ct)),ct);await RequireIndependentAsync(row,gate,acceptor,ct);return row;
 }
 public async Task<ReleaseArtifactDto> MaterializeArtifactAsync(Guid stageId,ActorContext actor,CancellationToken ct)
 {
  var initial=await StageAsync(stageId,ct);var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);
  var result=await commands.ExecuteAsync(actor,scope,"pipeline.stage.artifact",async(_,token)=>{
   await LockAsync(initial,token);var gate=await gates.ResolveStageAsync(stageId,actor,token);await gates.RequireStageWritableAsync(gate,actor,token);await auth.RequireAsync(actor,"pipeline.run",new("environment",initial.EnvironmentId,scope),token);await auth.RequireAsync(actor,"release.create",new("environment",initial.EnvironmentId,scope),token);
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.stage.artifact",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{stageId,gate.Pipeline!.CurrentAttemptId}),async inner=>{
    var stage=await db.Set<ReleasePipelineRunStage>().SingleAsync(s=>s.Id==stageId,inner);var profile=System.Text.Json.JsonSerializer.Deserialize<PipelineStageProfile>(stage.ProfileJson,CanonicalJson.Options)!;if(profile.IsProduction)throw new ApiException(422,"pipeline_nonproduction_artifact_only","生产阶段使用正式生产验证，不生成后续测试制品。");
    var attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==stage.CurrentAttemptId&&a.RunStageId==stage.Id,inner);if(attempt.ActualReleaseId is not Guid releaseId)throw new ApiException(409,"pipeline_actual_release_required","请等待当前阶段实际发布完成全节点确认。");
    var artifact=await artifacts.CreateWithinTransactionAsync(releaseId,actor,inner);if(artifact.ArtifactHash!=gate.Pipeline.RootArtifactHash){var before=stage.Status;var run=await db.Set<ReleasePipelineRun>().SingleAsync(r=>r.Id==stage.RunId,inner);run.Status="Invalidated";run.Revision++;run.CompletedAt=DateTimeOffset.UtcNow;stage.Status="Invalidated";stage.Revision++;attempt.Status="Invalidated";attempt.Revision++;Append(stage,attempt.Id,"stage_artifact_root_mismatch",actor.UserId,artifact.Id,before);return new Materialization(null,"pipeline_artifact_root_mismatch");}
    if(artifact.SourceEnvironmentId!=stage.EnvironmentId)throw Stale();if(stage.StageArtifactId==artifact.Id&&attempt.ArtifactId==artifact.Id)return new Materialization(artifact);var previous=stage.Status;stage.StageArtifactId=artifact.Id;stage.Status=stage.StageOrder==1?"AwaitingEvidence":"AwaitingVerification";stage.Revision++;attempt.ArtifactId=artifact.Id;attempt.Revision++;Append(stage,attempt.Id,"stage_artifact_materialized",actor.UserId,artifact.Id,previous);return new Materialization(artifact);
   },token);
  },ct);
  // The invalidation fact must commit before the caller receives the conflict.
  if(result.Artifact is null)throw new ApiException(409,result.FailureCode!,"阶段制品行为与根制品不一致，运行已失效，请重新建立流水线运行。");return result.Artifact;
 }
 public async Task<PromotionDto> PreparePromotionAsync(Guid stageId,ActorContext actor,CancellationToken ct)
 {
  var initial=await StageAsync(stageId,ct);var scope=await scopes.EnvironmentAsync(initial.EnvironmentId,ct);
  return await commands.ExecuteAsync(actor,scope,"pipeline.stage.prepare",async(_,token)=>{
   await LockAsync(initial,token);var gate=await gates.ResolveStageAsync(stageId,actor,token);await gates.RequireStageWritableAsync(gate,actor,token);
   await auth.RequireAsync(actor,"pipeline.run",new("environment",initial.EnvironmentId,scope),token);await auth.RequireAsync(actor,"release.create",new("environment",initial.EnvironmentId,scope),token);
   if(initial.SourceStageId is not Guid sourceId)throw new ApiException(409,"pipeline_source_imported","来源阶段已导入实际发布，不建立部署晋级申请。");
   var source=await StageAsync(sourceId,token);if(source.RunId!=initial.RunId||source.ProjectId!=initial.ProjectId||source.StageOrder!=initial.StageOrder-1||source.Status!="Passed"||source.StageArtifactId is not Guid artifactId||source.CurrentAttemptId is not Guid sourceAttemptId)throw new ApiException(409,"pipeline_predecessor_not_passed","只允许当前阶段的直接前置已通过阶段推进。");
   var sourceAttempt=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().SingleAsync(a=>a.Id==sourceAttemptId&&a.RunStageId==source.Id,token);
   if(sourceAttempt.Status!="Passed"||sourceAttempt.AcceptanceId is not Guid acceptanceId)throw Stale();
   var acceptance=await db.Set<ReleaseTestAcceptance>().AsNoTracking().SingleAsync(a=>a.Id==acceptanceId&&a.PipelineRunStageId==source.Id,token);await RequireAcceptedCurrentAsync(acceptance,actor,token);
   var artifact=await artifacts.GetAsync(artifactId,actor,token);if(artifact.ArtifactHash!=gate.Pipeline!.RootArtifactHash||artifact.SourceEnvironmentId!=source.EnvironmentId)throw Stale();
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"pipeline.stage.prepare",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{stageId,gate.Pipeline.CurrentAttemptId}),async inner=>{
    var attempt=await db.Set<ReleasePipelineStageAttempt>().SingleAsync(a=>a.Id==gate.Pipeline.CurrentAttemptId&&a.RunStageId==stageId,inner);
    if(attempt.PromotionId is Guid existing){var prior=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==existing&&p.PipelineRunStageId==stageId&&p.StageAttemptId==attempt.Id,inner);return PromotionMappingService.View(prior,artifact.ArtifactHash);}
    var promotion=await ReleasePromotionService.CreateForStageWithinTransactionAsync(db,stageId,gate,artifact,acceptance,actor,inner);attempt.PromotionId=promotion.Id;attempt.Revision++;Append(initial,attempt.Id,"stage_promotion_prepared",actor.UserId,promotion.Id);return PromotionMappingService.View(promotion,artifact.ArtifactHash);
   },token);
  },ct);
 }
 private void Append(ReleasePipelineRunStage stage,Guid? attempt,string reason,Guid actor,Guid related,string? from=null)=>db.Add(new ReleasePipelineEvent{RunId=stage.RunId,ProjectId=stage.ProjectId,StageId=stage.Id,AttemptId=attempt,FromStatus=from??stage.Status,ToStatus=stage.Status,ReasonCode=reason,ActorId=actor,RelatedId=related});
}

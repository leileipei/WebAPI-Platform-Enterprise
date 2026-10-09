using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
public sealed class DeliveryReleaseGuard(WebApiDbContext db,DeliveryLockCoordinator locks,ScopeResolver scopes,AuthorizationService auth,ReleaseArtifactService artifacts,TestAcceptanceService acceptances,PromotionMappingService mappings,PromotionCredentialSelection credentials,WebApi.Infrastructure.Releases.ReleaseCandidateBuilder candidates,PromotionReadService reads,DeliveryGateContextResolver gates,WebApi.Infrastructure.Delivery.Pipelines.PipelineReadService pipelines)
{
 public async Task RequireNormalPublishAsync(Guid environmentId,CancellationToken ct)
 {var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==environmentId,ct);if(env.IsProduction&&await db.Set<ProjectDeliveryPolicy>().AsNoTracking().AnyAsync(p=>p.ProjectId==env.ProjectId&&(p.Mode=="PromotionRequired"||p.Mode=="PipelineRequired"),ct))throw new ApiException(409,"promotion_required","该项目已启用生产晋级门禁，请从已验收制品建立生产晋级申请。");}
 public async Task RequireReadyAsync(ReleaseRecord release,ActorContext actor,CancellationToken ct)
 {
  if(release.ReleaseType!="publish")return;if(release.PromotionId is not Guid id){await RequireNormalPublishAsync(release.EnvironmentId,ct);return;}
  var p=await db.Set<ReleasePromotion>().SingleOrDefaultAsync(p=>p.Id==id&&p.TargetReleaseId==release.Id&&p.TargetEnvironmentId==release.EnvironmentId&&p.ArtifactId==release.ArtifactId&&p.SourceReleaseId==release.SourceReleaseId,ct)??throw new ApiException(409,"invalid_promotion_release","发布的正式晋级关联不完整。");
  await locks.LockAsync(p.SourceEnvironmentId,p.TargetEnvironmentId,ct);await artifacts.GetAsync(p.ArtifactId,actor,ct);
  if(p.Status is not("WaitingApproval" or "Ready" or "Deploying")||p.AcceptanceId is not Guid accepted||release.CandidateBytes is null||Convert.ToHexStringLower(SHA256.HashData(release.CandidateBytes))!=p.CandidateHash)throw PromotionPrecheckService.Stale();
  await gates.ResolvePromotionFactsAsync(p,ct);await RequirePipelineExecutionAsync(release.Id,actor,ct);var target=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==p.TargetEnvironmentId,ct);
  if(target.AccessAddressRevision!=p.TargetAccessAddressRevision||(target.DesiredConfigVersion??0)!=p.BaselineConfigVersion||release.BaselineConfigVersion!=p.BaselineConfigVersion)throw PromotionPrecheckService.Stale();
  var requester=new ActorContext(p.RequestedBy,actor.TraceId);
  try{foreach(var environment in p.GateOrigin=="PipelineRunStage"?new[]{p.TargetEnvironmentId}:new[]{p.SourceEnvironmentId,p.TargetEnvironmentId})await auth.RequireAsync(requester,"release.create",new("environment",environment,await scopes.EnvironmentAsync(environment,ct)),ct);if(p.GateOrigin=="PipelineRunStage"){await gates.ResolvePromotionAsync(p.Id,requester,ct);await auth.RequireAsync(requester,"pipeline.run",new("environment",p.TargetEnvironmentId,await scopes.EnvironmentAsync(p.TargetEnvironmentId,ct)),ct);}await acceptances.RequireAcceptedCurrentAsync(accepted,p.ArtifactId,requester,ct);await RequireResourcesAsync(p,release,requester,ct);}
  catch(ApiException e)when(e.Status is 401 or 403 or 404){throw new ApiException(409,"promotion_actor_authority_changed","晋级申请人或测试验收人的资格已变化，需重新核对。");}
 }
 public async Task<DeliveryGateContext?> RequirePipelineExecutionAsync(Guid releaseId,ActorContext actor,CancellationToken ct)
 {
  var release=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==releaseId,ct);if(release.ReleaseType!="publish")return null;
  if(release.PromotionId is not Guid promotionId){await RequireNormalPublishAsync(release.EnvironmentId,ct);return null;}
  var promotion=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==promotionId,ct);if(promotion.GateOrigin!="PipelineRunStage")return null;
  var gate=await gates.ResolvePromotionAsync(promotion.Id,actor,ct);if(release.Status=="Building")await gates.RequireStageBuildWritableAsync(gate,actor,ct);else await gates.RequireStageWritableAsync(gate,actor,ct);
  if(gate.Pipeline is not{} binding||promotion.TargetReleaseId!=release.Id||promotion.TargetEnvironmentId!=release.EnvironmentId||promotion.ArtifactId!=release.ArtifactId||promotion.SourceReleaseId!=release.SourceReleaseId||!await db.Set<ReleasePipelineStageAttempt>().AnyAsync(a=>a.Id==binding.CurrentAttemptId&&a.RunStageId==binding.StageId&&a.ActualReleaseId==release.Id,ct))throw new ApiException(409,"pipeline_release_binding_changed","正式发布与当前阶段尝试关联已变化。");
  await gates.RequirePassedPredecessorAsync(gate,promotion.AcceptanceId,ct);var environment=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==release.EnvironmentId,ct);await WebApi.Infrastructure.Releases.ReleaseService.RequirePipelineApprovalAsync(db,promotion.OrganizationId,environment,binding.Approval,ct);
  if(release.Status is "Ready" or "Building")await auth.RequireAsync(actor,"pipeline.run",new("environment",environment.Id,await scopes.EnvironmentAsync(environment.Id,ct)),ct);return gate;
 }
 private async Task RequireResourcesAsync(ReleasePromotion p,ReleaseRecord release,ActorContext actor,CancellationToken ct)
 {
  var artifact=await artifacts.GetAsync(p.ArtifactId,actor,ct);var mapping=await mappings.LoadAsync(p.Id,ct);await mappings.ValidateAsync(p,artifact.Content,mapping,actor,ct);var selection=await credentials.SelectAsync(p,artifact.Content,mapping.Applications,actor,ct);var rows=await db.Set<ReleasePromotionMapping>().AsNoTracking().Where(m=>m.PromotionId==p.Id&&m.Kind=="Route").ToArrayAsync(ct);var routes=rows.Select(m=>m.TargetRouteId).ToArray();if(routes.Any(r=>r==null))throw PromotionPrecheckService.Stale();
  foreach(var row in rows){var state=PromotionMappingService.Read<PromotionRouteState>(row.ParametersJson);if(state.PreparedRevision!=p.MappingRevision||state.PrivatePolicyIds is null||await db.Set<RoutePolicyBinding>().AnyAsync(b=>state.PrivatePolicyIds.Contains(b.PolicyId)&&b.RouteId!=row.TargetRouteId,ct))throw new ApiException(409,"prepared_policy_changed","晋级独立策略的准备状态或共享范围在审批后变化。");}
  var frozen=System.Text.Json.JsonSerializer.Deserialize<FrozenReleaseCandidate>(release.CandidateBytes!,CanonicalJson.Options)!;var current=await candidates.BuildPromotionAsync(p.TargetEnvironmentId,new(p.BaselineConfigVersion,frozen.VersionIds),routes.Select(r=>r!.Value).ToArray(),selection,ct);var extra=new List<ResourceRevision>();
  foreach(var slot in mapping.Routes.SelectMany(r=>r.Policies).Where(p=>p.TargetPolicyId!=null)){var policy=await db.Set<Policy>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==slot.TargetPolicyId,ct);if(policy is null)throw PromotionPrecheckService.Stale();extra.Add(new("policy",policy.Id,policy.VersionNo));}
  current=current with{ResourceRevisions=current.ResourceRevisions.Concat(extra).DistinctBy(r=>(r.Type,r.Id)).OrderBy(r=>r.Type,StringComparer.Ordinal).ThenBy(r=>r.Id).ToArray(),RiskReviewReferences=frozen.RiskReviewReferences};
  if(Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(current)))!=p.CandidateHash)throw new ApiException(409,"promotion_resources_changed","目标资源或绑定在审批后变化，请重新准备并审批。");
 }
 public async Task<ApprovalDeliverySummaryDto?> TraceAsync(ReleaseRecord release,ActorContext? actor,CancellationToken ct)
 {if(release.PromotionId is null&&release.RecoveryOf is null&&release.RollbackOf is null)return null;if(actor is null)return null;var promotion=await reads.ResolvePromotionAsync(release.Id,ct);if(promotion is null)return null;try{var artifact=await artifacts.GetAsync(promotion.ArtifactId,actor,ct);var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==promotion.SourceEnvironmentId,ct);return new("Visible",promotion.Id,artifact.Id,artifact.ArtifactHash,new(env.Id,env.Code,env.Name),promotion.PipelineRunStageId is Guid stage?await pipelines.TraceStageAsync(stage,actor,ct):null);}catch(ApiException e)when(e.Status is 403 or 404){return new("Restricted",null,null,null,null);}}
 public async Task RequireApprovalVisibilityAsync(ReleaseRecord release,ActorContext actor,CancellationToken ct)
 {if(release.PromotionId is not Guid id)return;var p=await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id&&p.TargetReleaseId==release.Id&&p.TargetEnvironmentId==release.EnvironmentId,ct)??throw ScopeResolver.Missing();await locks.LockAsync(p.SourceEnvironmentId,p.TargetEnvironmentId,ct);await artifacts.GetAsync(p.ArtifactId,actor,ct);}
 internal async Task ProjectApprovalAsync(ReleaseRecord release,Guid actor,CancellationToken ct)
 {
  if(release.PromotionId is not Guid id)return;var p=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==id&&p.TargetReleaseId==release.Id,ct);if(release.Status is not("Ready" or "Rejected" or "Cancelled")||p.Status==release.Status)return;
  var before=p.Status;p.Status=release.Status;p.Revision++;if(release.Status is "Rejected" or "Cancelled")p.CompletedAt=DateTimeOffset.UtcNow;
  db.Add(new ReleasePromotionEvent{OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,EnvironmentId=p.TargetEnvironmentId,PromotionId=p.Id,Phase="Approval",FromStatus=before,ToStatus=p.Status,ReasonCode="production_"+p.Status.ToLowerInvariant(),ActorId=actor,ReleaseId=release.Id});
 }
}

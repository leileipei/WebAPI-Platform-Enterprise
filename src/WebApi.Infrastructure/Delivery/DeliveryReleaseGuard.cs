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
public sealed class DeliveryReleaseGuard(WebApiDbContext db,DeliveryLockCoordinator locks,ScopeResolver scopes,AuthorizationService auth,ReleaseArtifactService artifacts,TestAcceptanceService acceptances)
{
 public async Task RequireNormalPublishAsync(Guid environmentId,CancellationToken ct)
 {var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==environmentId,ct);if(env.IsProduction&&await db.Set<ProjectDeliveryPolicy>().AsNoTracking().AnyAsync(p=>p.ProjectId==env.ProjectId&&p.Mode=="PromotionRequired",ct))throw new ApiException(409,"promotion_required","该项目已启用生产晋级门禁，请从已验收制品建立生产晋级申请。");}
 public async Task RequireReadyAsync(ReleaseRecord release,ActorContext actor,CancellationToken ct)
 {
  if(release.ReleaseType!="publish")return;if(release.PromotionId is not Guid id){await RequireNormalPublishAsync(release.EnvironmentId,ct);return;}
  var p=await db.Set<ReleasePromotion>().SingleOrDefaultAsync(p=>p.Id==id&&p.TargetReleaseId==release.Id&&p.TargetEnvironmentId==release.EnvironmentId&&p.ArtifactId==release.ArtifactId&&p.SourceReleaseId==release.SourceReleaseId,ct)??throw new ApiException(409,"invalid_promotion_release","发布的正式晋级关联不完整。");
  await locks.LockAsync(p.SourceEnvironmentId,p.TargetEnvironmentId,ct);await artifacts.GetAsync(p.ArtifactId,actor,ct);
  if(p.Status is not("WaitingApproval" or "Ready" or "Deploying")||p.AcceptanceId is not Guid accepted||release.CandidateBytes is null||Convert.ToHexStringLower(SHA256.HashData(release.CandidateBytes))!=p.CandidateHash)throw PromotionPrecheckService.Stale();
  var policy=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(row=>row.ProjectId==p.ProjectId&&row.OrganizationId==p.OrganizationId,ct);var frozen=PromotionMappingService.Read<DeliveryPolicyDto>(p.FrozenPolicyJson);var target=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==p.TargetEnvironmentId,ct);
  if(policy is null||policy.Revision!=frozen.Revision||policy.Mode!=frozen.Mode||policy.SourceEnvironmentId!=p.SourceEnvironmentId||policy.TargetEnvironmentId!=p.TargetEnvironmentId||target.AccessAddressRevision!=p.TargetAccessAddressRevision||(target.DesiredConfigVersion??0)!=p.BaselineConfigVersion||release.BaselineConfigVersion!=p.BaselineConfigVersion)throw PromotionPrecheckService.Stale();
  var requester=new ActorContext(p.RequestedBy,actor.TraceId);
  try{foreach(var environment in new[]{p.SourceEnvironmentId,p.TargetEnvironmentId})await auth.RequireAsync(requester,"release.create",new("environment",environment,await scopes.EnvironmentAsync(environment,ct)),ct);await acceptances.RequireAcceptedCurrentAsync(accepted,p.ArtifactId,requester,ct);}
  catch(ApiException e)when(e.Status is 401 or 403 or 404){throw new ApiException(409,"promotion_actor_authority_changed","晋级申请人或测试验收人的资格已变化，需重新核对。");}
 }
 public async Task RequireApprovalVisibilityAsync(ReleaseRecord release,ActorContext actor,CancellationToken ct)
 {if(release.PromotionId is not Guid id)return;var p=await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id&&p.TargetReleaseId==release.Id&&p.TargetEnvironmentId==release.EnvironmentId,ct)??throw ScopeResolver.Missing();await locks.LockAsync(p.SourceEnvironmentId,p.TargetEnvironmentId,ct);await artifacts.GetAsync(p.ArtifactId,actor,ct);}
 internal async Task ProjectApprovalAsync(ReleaseRecord release,Guid actor,CancellationToken ct)
 {
  if(release.PromotionId is not Guid id)return;var p=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==id&&p.TargetReleaseId==release.Id,ct);if(release.Status is not("Ready" or "Rejected" or "Cancelled")||p.Status==release.Status)return;
  var before=p.Status;p.Status=release.Status;p.Revision++;if(release.Status is "Rejected" or "Cancelled")p.CompletedAt=DateTimeOffset.UtcNow;
  db.Add(new ReleasePromotionEvent{OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,EnvironmentId=p.TargetEnvironmentId,PromotionId=p.Id,Phase="Approval",FromStatus=before,ToStatus=p.Status,ReasonCode="production_"+p.Status.ToLowerInvariant(),ActorId=actor,ReleaseId=release.Id});
 }
}

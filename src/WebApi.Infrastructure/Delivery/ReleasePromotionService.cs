using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
public sealed class ReleasePromotionService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,DeliveryLockCoordinator locks,ReleaseArtifactService artifacts,TestAcceptanceService acceptances,PromotionMappingService mapping,PromotionPrecheckService prechecks,ReleaseService releases)
{
 public async Task<PromotionDto> CreateAsync(Guid artifactId,ActorContext actor,CancellationToken ct)
 {
  var initial=await artifacts.GetAsync(artifactId,actor,ct);var connection=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(p=>p.ProjectId==initial.ProjectId&&p.OrganizationId==initial.OrganizationId&&p.SourceEnvironmentId==initial.SourceEnvironmentId,ct)??throw PromotionPrecheckService.Stale();var scope=await scopes.EnvironmentAsync(connection.TargetEnvironmentId,ct);
  return await commands.ExecuteAsync(actor,scope,"promotion.create",async(_,token)=>{
   await locks.LockAsync(connection.SourceEnvironmentId,connection.TargetEnvironmentId,token);var artifact=await artifacts.GetAsync(artifactId,actor,token);var policy=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleAsync(p=>p.ProjectId==artifact.ProjectId,token);if(policy.SourceEnvironmentId!=connection.SourceEnvironmentId||policy.TargetEnvironmentId!=connection.TargetEnvironmentId||policy.Revision!=connection.Revision)throw PromotionPrecheckService.Stale();
   foreach(var environment in new[]{policy.SourceEnvironmentId,policy.TargetEnvironmentId})await auth.RequireAsync(actor,"release.create",new("environment",environment,await scopes.EnvironmentAsync(environment,token)),token);
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"promotion.create",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{artifactId}),async inner=>{
    var source=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==policy.SourceEnvironmentId,inner);var target=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==policy.TargetEnvironmentId,inner);if(source.IsProduction||!target.IsProduction||source.Status!="Active"||target.Status!="Active"||target.ProjectId!=artifact.ProjectId)throw PromotionPrecheckService.Stale();
    var acceptance=await db.Set<ReleaseTestAcceptance>().AsNoTracking().Where(a=>a.ArtifactId==artifactId&&a.Status=="Accepted").OrderByDescending(a=>a.CreatedAt).ThenByDescending(a=>a.Id).FirstOrDefaultAsync(inner)??throw new ApiException(409,"test_acceptance_required","制品需有效独立测试验收后才能建立晋级。");await acceptances.RequireAcceptedCurrentAsync(acceptance.Id,artifact.Id,actor,inner);
    var p=new ReleasePromotion{OrganizationId=artifact.OrganizationId,ProjectId=artifact.ProjectId,ArtifactId=artifact.Id,SourceEnvironmentId=policy.SourceEnvironmentId,TargetEnvironmentId=policy.TargetEnvironmentId,SourceReleaseId=artifact.SourceReleaseId,AcceptanceId=acceptance.Id,RequestedBy=actor.UserId,BaselineConfigVersion=target.DesiredConfigVersion??0,TargetAccessAddressRevision=target.AccessAddressRevision,FrozenPolicyJson=PromotionMappingService.Json(ProjectDeliveryPolicyService.View(policy.ProjectId,policy))};db.Add(p);Append(p,null,"Draft","promotion_created",actor.UserId);return PromotionMappingService.View(p,artifact.ArtifactHash);
   },token);
  },ct);
 }
 public async Task<PromotionDto> GetAsync(Guid id,ActorContext actor,CancellationToken ct)
 {var p=await ReadAsync(id,actor,ct);var artifact=await artifacts.GetAsync(p.ArtifactId,actor,ct);return PromotionMappingService.View(p,artifact.ArtifactHash,await mapping.LoadAsync(id,ct));}
 internal async Task<ReleasePromotion> ReadAsync(Guid id,ActorContext actor,CancellationToken ct)
 {var p=await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();foreach(var environment in new[]{p.SourceEnvironmentId,p.TargetEnvironmentId}){var scope=await scopes.EnvironmentAsync(environment,ct);if(scope.OrganizationId!=p.OrganizationId||scope.ProjectId!=p.ProjectId||!await auth.CanAsync(actor,"release.read",new("environment",environment,scope),ct)||!await auth.CanAsync(actor,"environment.read",new("environment",environment,scope),ct))throw ScopeResolver.Missing();}await artifacts.GetAsync(p.ArtifactId,actor,ct);return p;}
 public async Task<PromotionDto> SubmitAsync(Guid id,string? etag,ActorContext actor,CancellationToken ct)
 {
  var initial=await ReadAsync(id,actor,ct);var scope=await scopes.EnvironmentAsync(initial.TargetEnvironmentId,ct);
  return await commands.ExecuteAsync(actor,scope,"promotion.submit",async(_,token)=>{
   await locks.LockAsync(initial.SourceEnvironmentId,initial.TargetEnvironmentId,token);var p=await db.Set<ReleasePromotion>().SingleAsync(row=>row.Id==id,token);var artifact=await mapping.RequireAsync(p,actor,token);if(p.RequestedBy!=actor.UserId)throw new ApiException(403,"not_applicant","仅申请人可提交晋级。");
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"promotion.submit",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,etag}),async inner=>{
    RevisionTag.Require(etag,p.Revision);if(p.Status!="Draft"||p.TargetReleaseId is not null)throw PromotionMappingService.NotDraft();if(p.PrecheckJson is null)throw new ApiException(409,"promotion_precheck_required","请先准备目标候选并完成预检。");var prior=PromotionMappingService.Read<PromotionPrecheckState>(p.PrecheckJson);if(!prior.Result.CanSubmit||prior.Result.MappingRevision!=p.MappingRevision||prior.Result.CandidateHash!=p.CandidateHash||prior.Result.BaselineConfigVersion!=p.BaselineConfigVersion||prior.Result.TargetAccessAddressRevision!=p.TargetAccessAddressRevision)throw PromotionPrecheckService.Stale();
    var evaluated=await prechecks.EvaluateAsync(p,prior.Input,actor,inner);if(!evaluated.State.Result.CanSubmit||evaluated.State.Result.CandidateHash!=prior.Result.CandidateHash||evaluated.State.Result.PolicyRevision!=prior.Result.PolicyRevision||evaluated.State.AcceptanceRevision!=prior.AcceptanceRevision||evaluated.State.ApprovalFlowId!=prior.ApprovalFlowId||evaluated.State.ApprovalFlowRevision!=prior.ApprovalFlowRevision||evaluated.State.ApprovalRulesHash!=prior.ApprovalRulesHash||evaluated.State.ReportHash!=prior.ReportHash)throw PromotionPrecheckService.Stale();
    var target=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==p.TargetEnvironmentId,inner);var release=new ReleaseRecord{EnvironmentId=target.Id,ReleaseNo="REL-"+Guid.NewGuid().ToString("N"),ReleaseType="publish",Status="Draft",RequestedBy=actor.UserId,BaselineConfigVersion=p.BaselineConfigVersion,FromConfigVersion=p.BaselineConfigVersion,ArtifactId=p.ArtifactId,PromotionId=p.Id,SourceReleaseId=p.SourceReleaseId};db.Add(release);p.TargetReleaseId=release.Id;p.CandidateHash=evaluated.State.Result.CandidateHash;p.ResourceRevisionsJson=PromotionMappingService.Json(evaluated.Candidate.ResourceRevisions);
    await releases.FreezeApprovalAsync(release,scope,target,evaluated.Candidate,inner);var before=p.Status;p.Status=release.Status;p.Revision++;p.PrecheckJson=PromotionMappingService.Json(evaluated.State with{Result=evaluated.State.Result with{Revision=p.Revision}});Append(p,before,p.Status,"promotion_submitted",actor.UserId,release.Id);return PromotionMappingService.View(p,artifact.ArtifactHash,await mapping.LoadAsync(id,inner),evaluated.Candidate.SharedCredentialImpact);
   },token);
  },ct);
 }
 public async Task<PromotionDto> CancelAsync(Guid id,string? etag,ActorContext actor,CancellationToken ct)
 {
  var initial=await ReadAsync(id,actor,ct);var scope=await scopes.EnvironmentAsync(initial.TargetEnvironmentId,ct);return await commands.ExecuteAsync(actor,scope,"promotion.cancel",async(_,token)=>{await locks.LockAsync(initial.SourceEnvironmentId,initial.TargetEnvironmentId,token);await auth.RequireAsync(actor,"release.create",new("environment",initial.TargetEnvironmentId,scope),token);return await idempotency.ExecuteAsync(new(actor.UserId,scope,"promotion.cancel",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,etag}),async inner=>{var p=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==id,inner);RevisionTag.Require(etag,p.Revision);if(p.RequestedBy!=actor.UserId)throw new ApiException(403,"not_applicant","仅申请人可取消晋级。");if(p.Status is not("Draft" or "WaitingApproval" or "Ready"))throw new ApiException(409,"promotion_cannot_cancel","该晋级已执行或结束，不能取消。");if(p.TargetReleaseId is Guid releaseId){var release=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==releaseId,inner);if(release.Status is not("Draft" or "WaitingApproval" or "Ready"))throw new ApiException(409,"promotion_cannot_cancel","该发布已开始执行，不能取消。");release.Status="Cancelled";release.CompletedAt=DateTimeOffset.UtcNow;}var before=p.Status;p.Status="Cancelled";p.Revision++;p.CompletedAt=DateTimeOffset.UtcNow;Append(p,before,p.Status,"promotion_cancelled",actor.UserId);return PromotionMappingService.View(p,(await artifacts.GetAsync(p.ArtifactId,actor,inner)).ArtifactHash);},token);},ct);
 }
 internal void Append(ReleasePromotion p,string? before,string after,string reason,Guid actor,Guid? release=null)=>db.Add(new ReleasePromotionEvent{OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,EnvironmentId=p.TargetEnvironmentId,PromotionId=p.Id,Phase="Submission",FromStatus=before,ToStatus=after,ReasonCode=reason,ActorId=actor,ReleaseId=release});
}

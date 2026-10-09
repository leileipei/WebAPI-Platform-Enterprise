using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
public sealed class PromotionReadService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,ReleaseArtifactService artifacts,PromotionMappingService mappings,TestAcceptanceService acceptances,WebApi.Infrastructure.Releases.RunningDeploymentReader running)
{
 public async Task<ReleasePromotion?> ResolvePromotionAsync(Guid releaseId,CancellationToken ct)
 {
  var visited=new HashSet<Guid>();Guid? environment=null;var id=releaseId;
  for(var depth=0;depth<100;depth++){
   if(!visited.Add(id))throw InvalidChain();var release=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct)??throw InvalidChain();environment??=release.EnvironmentId;if(release.EnvironmentId!=environment)throw InvalidChain();
   if(release.PromotionId is Guid promotionId){if(release.RecoveryOf is not null||release.RollbackOf is not null||release.ReleaseType!="publish")throw InvalidChain();return await db.Set<ReleasePromotion>().SingleOrDefaultAsync(p=>p.Id==promotionId&&p.TargetReleaseId==release.Id&&p.TargetEnvironmentId==environment&&p.ArtifactId==release.ArtifactId&&p.SourceReleaseId==release.SourceReleaseId,ct)??throw InvalidChain();}
   if(release.RecoveryOf is Guid recovery){if(release.ReleaseType!="retry"||release.RollbackOf is not null)throw InvalidChain();id=recovery;continue;}
   if(release.RollbackOf is Guid rollback){if(release.ReleaseType!="rollback")throw InvalidChain();id=rollback;continue;}return null;
  }throw InvalidChain();
 }
 public async Task<PromotionDto> ReadAsync(Guid id,ActorContext actor,CancellationToken ct)
 {
  var p=await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();foreach(var environment in new[]{p.SourceEnvironmentId,p.TargetEnvironmentId}){var scope=await scopes.EnvironmentAsync(environment,ct);if(scope.OrganizationId!=p.OrganizationId||scope.ProjectId!=p.ProjectId||!await auth.CanAsync(actor,"release.read",new("environment",environment,scope),ct)||!await auth.CanAsync(actor,"environment.read",new("environment",environment,scope),ct))throw ScopeResolver.Missing();}
  var artifact=await artifacts.GetAsync(p.ArtifactId,actor,ct);var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==p.TargetEnvironmentId&&n.Enabled).OrderBy(n=>n.Id).Select(n=>new PromotionNodeState(n.Id,n.NodeName,n.CurrentConfigVersion,n.CurrentDeploymentSequence,n.Status,n.LastHeartbeatAt)).ToArrayAsync(ct);
  var latest=await db.Set<ReleasePromotionEvent>().AsNoTracking().Where(e=>e.PromotionId==id&&e.Phase=="Deployment"&&e.ReleaseId!=null).OrderByDescending(e=>e.OccurredAt).ThenByDescending(e=>e.Id).Select(e=>e.ReleaseId).FirstOrDefaultAsync(ct);
  var targetScope=await scopes.EnvironmentAsync(p.TargetEnvironmentId,ct);var sourceScope=await scopes.EnvironmentAsync(p.SourceEnvironmentId,ct);var create=await auth.CanAsync(actor,"release.create",new("environment",p.TargetEnvironmentId,targetScope),ct);var actualMapping=await mappings.LoadAsync(id,ct);var mappingVisible=await auth.CanAsync(actor,"route.read",new("environment",p.TargetEnvironmentId,targetScope),ct)&&await auth.CanAsync(actor,"cluster.read",new("environment",p.TargetEnvironmentId,targetScope),ct)&&(actualMapping.Applications.Count==0||await auth.CanAsync(actor,"app.read",new("environment",p.TargetEnvironmentId,targetScope),ct))&&(!actualMapping.Routes.Any(r=>r.Policies.Any(p=>p.TargetPolicyId!=null))||await auth.CanAsync(actor,"policy.read",new("environment",p.TargetEnvironmentId,targetScope),ct));var mappingAllowed=mappingVisible&&create&&await auth.CanAsync(actor,"route.write",new("environment",p.TargetEnvironmentId,targetScope),ct);var submitAllowed=mappingAllowed&&p.RequestedBy==actor.UserId&&await auth.CanAsync(actor,"release.create",new("environment",p.SourceEnvironmentId,sourceScope),ct);var reasons=new List<string>();if(!mappingVisible)reasons.Add("target_mapping_restricted");var contextCurrent=false;
  try{var policy=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(r=>r.ProjectId==p.ProjectId,ct);var frozen=PromotionMappingService.Read<DeliveryPolicyDto>(p.FrozenPolicyJson);var target=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==p.TargetEnvironmentId,ct);contextCurrent=policy is not null&&policy.Revision==frozen.Revision&&policy.Mode==frozen.Mode&&policy.SourceEnvironmentId==p.SourceEnvironmentId&&policy.TargetEnvironmentId==p.TargetEnvironmentId&&target.AccessAddressRevision==p.TargetAccessAddressRevision&&(target.DesiredConfigVersion??0)==p.BaselineConfigVersion;if(p.AcceptanceId is Guid accepted)await acceptances.RequireAcceptedCurrentAsync(accepted,p.ArtifactId,actor,ct);else contextCurrent=false;}catch(ApiException e)when(e.Status is 403 or 404 or 409 or 422){contextCurrent=false;reasons.Add(e.Code);}
  var precheckState=p.PrecheckJson is null?null:PromotionMappingService.Read<PromotionPrecheckState>(p.PrecheckJson);var precheck=mappingVisible?precheckState?.Result:null;var impact=await auth.CanAsync(actor,"app.read",new("environment",p.TargetEnvironmentId,targetScope),ct)?precheckState?.CredentialImpact:null;var canSubmit=p.Status=="Draft"&&submitAllowed&&contextCurrent&&precheck?.CanSubmit==true&&precheck.CandidateHash==p.CandidateHash&&precheck.MappingRevision==p.MappingRevision;
  Guid? publisher=null;var currentDelivery=false;try{var current=await running.ReadAsync(p.TargetEnvironmentId,null,ct);publisher=current.Release.PublishRequestedBy;currentDelivery=(await ResolvePromotionAsync(current.Release.Id,ct))?.Id==p.Id&&p.TargetReleaseId is Guid formal&&await db.Set<ReleaseRecord>().AnyAsync(r=>r.Id==formal&&r.ToConfigVersion==current.Release.ToConfigVersion,ct);}catch(ApiException e)when(e.Status is 409 or 422){reasons.Add(e.Code);}
  var canVerify=(p.Status is "Verifying" or "VerificationFailed")&&currentDelivery&&publisher is not null&&publisher!=actor.UserId&&p.RequestedBy!=actor.UserId&&await auth.CanAsync(actor,"release.verify",new("environment",p.TargetEnvironmentId,targetScope),ct);
  var eligibility=new PromotionActionEligibility(p.Status=="Draft"&&mappingAllowed,canSubmit,p.Status=="Ready"&&contextCurrent&&await auth.CanAsync(actor,"release.publish",new("environment",p.TargetEnvironmentId,targetScope),ct),canVerify,p.RequestedBy==actor.UserId&&create&&(p.Status is "Draft" or "WaitingApproval" or "Ready"),publisher,reasons.Distinct().ToArray());
  return PromotionMappingService.View(p,artifact.ArtifactHash,mappingVisible?actualMapping:null,impact) with{Deployment=new(latest??p.TargetReleaseId,nodes),Eligibility=eligibility,Precheck=precheck};
 }
 private static ApiException InvalidChain()=>new(409,"invalid_delivery_recovery_chain","发布恢复追溯存在循环、跨环境或无效正式关联。");
}

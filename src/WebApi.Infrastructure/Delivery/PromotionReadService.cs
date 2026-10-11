using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
public sealed class PromotionReadService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,ReleaseArtifactService artifacts,PromotionMappingService mappings,TestAcceptanceService acceptances,WebApi.Infrastructure.Releases.RunningDeploymentReader running,DeliveryGateContextResolver gates,WebApi.Infrastructure.Delivery.Pipelines.PipelineReadService pipelines)
{
 public Task<ReleasePromotion?> ResolvePromotionAsync(Guid releaseId,CancellationToken ct)=>ResolvePromotionWithinAsync(db,releaseId,ct);
 internal static async Task<ReleasePromotion?> ResolvePromotionWithinAsync(WebApiDbContext db,Guid releaseId,CancellationToken ct)
 {
  var release=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==releaseId,ct)??throw InvalidChain();
  var root=(await ResolvePromotionReleasesWithinAsync(db,[release],ct))[releaseId];if(root is null)return null;
  return await db.Set<ReleasePromotion>().SingleOrDefaultAsync(p=>p.Id==root.PromotionId&&p.TargetReleaseId==root.Id&&p.TargetEnvironmentId==root.EnvironmentId&&p.ArtifactId==root.ArtifactId&&p.SourceReleaseId==root.SourceReleaseId,ct)??throw InvalidChain();
 }
 // Load this page and its bounded ancestors in one query. Validation is identical for detail and list,
 // with a per-chain cycle/environment guard and the original 100-release depth bound.
 internal static async Task<IReadOnlyDictionary<Guid,ReleaseRecord?>> ResolvePromotionReleasesWithinAsync(WebApiDbContext db,IReadOnlyList<ReleaseRecord> releases,CancellationToken ct)
 {
  if(releases.Count==0)return new Dictionary<Guid,ReleaseRecord?>();var ids=releases.Select(r=>r.Id).ToArray();
  var rows=await db.Set<ReleaseRecord>().FromSqlInterpolated($"""
   WITH RECURSIVE release_chain AS (
    SELECT r.id,r.promotion_id,r.recovery_of,r.rollback_of,0 AS depth FROM release_records r WHERE r.id=ANY({ids})
    UNION ALL
    SELECT r.id,r.promotion_id,r.recovery_of,r.rollback_of,c.depth+1 FROM release_records r
    JOIN release_chain c ON r.id=COALESCE(c.recovery_of,c.rollback_of) WHERE c.promotion_id IS NULL AND c.depth<100
   ) SELECT r.* FROM release_records r WHERE r.id IN (SELECT id FROM release_chain)
   """).AsNoTracking().ToArrayAsync(ct);
  var records=rows.ToDictionary(r=>r.Id);
  var result=new Dictionary<Guid,ReleaseRecord?>();
  foreach(var original in releases)
  {
   var visited=new HashSet<Guid>();var id=original.Id;var resolved=false;
   for(var depth=0;depth<100;depth++)
   {
    if(!visited.Add(id)||!records.TryGetValue(id,out var release)||release.EnvironmentId!=original.EnvironmentId)throw InvalidChain();
    if(release.PromotionId is not null){if(release.RecoveryOf is not null||release.RollbackOf is not null||release.ReleaseType!="publish")throw InvalidChain();result[original.Id]=release;resolved=true;break;}
    if(release.RecoveryOf is Guid recovery){if(release.ReleaseType!="retry"||release.RollbackOf is not null)throw InvalidChain();id=recovery;continue;}
    if(release.RollbackOf is Guid rollback){if(release.ReleaseType!="rollback")throw InvalidChain();id=rollback;continue;}
    result[original.Id]=null;resolved=true;break;
   }
   if(!resolved)throw InvalidChain();
  }
  return result;
 }
 public async Task<PromotionDto> ReadAsync(Guid id,ActorContext actor,CancellationToken ct)
 {
  var p=await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();foreach(var environment in new[]{p.SourceEnvironmentId,p.TargetEnvironmentId}){var scope=await scopes.EnvironmentAsync(environment,ct);if(scope.OrganizationId!=p.OrganizationId||scope.ProjectId!=p.ProjectId||!await auth.CanAsync(actor,"release.read",new("environment",environment,scope),ct)||!await auth.CanAsync(actor,"environment.read",new("environment",environment,scope),ct))throw ScopeResolver.Missing();}
  var artifact=await artifacts.GetAsync(p.ArtifactId,actor,ct);var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==p.TargetEnvironmentId&&n.Enabled).OrderBy(n=>n.Id).Select(n=>new PromotionNodeState(n.Id,n.NodeName,n.CurrentConfigVersion,n.CurrentDeploymentSequence,n.Status,n.LastHeartbeatAt)).ToArrayAsync(ct);
  var latest=await db.Set<ReleasePromotionEvent>().AsNoTracking().Where(e=>e.PromotionId==id&&e.Phase=="Deployment"&&e.ReleaseId!=null).OrderByDescending(e=>e.OccurredAt).ThenByDescending(e=>e.Id).Select(e=>e.ReleaseId).FirstOrDefaultAsync(ct);
  var targetScope=await scopes.EnvironmentAsync(p.TargetEnvironmentId,ct);var sourceScope=await scopes.EnvironmentAsync(p.SourceEnvironmentId,ct);var create=await auth.CanAsync(actor,"release.create",new("environment",p.TargetEnvironmentId,targetScope),ct);var actualMapping=await mappings.LoadAsync(id,ct);var mappingVisible=await auth.CanAsync(actor,"route.read",new("environment",p.TargetEnvironmentId,targetScope),ct)&&await auth.CanAsync(actor,"cluster.read",new("environment",p.TargetEnvironmentId,targetScope),ct)&&(actualMapping.Applications.Count==0||await auth.CanAsync(actor,"app.read",new("environment",p.TargetEnvironmentId,targetScope),ct))&&(!actualMapping.Routes.Any(r=>r.Policies.Any(p=>p.TargetPolicyId!=null))||await auth.CanAsync(actor,"policy.read",new("environment",p.TargetEnvironmentId,targetScope),ct));var mappingAllowed=mappingVisible&&create&&await auth.CanAsync(actor,"route.write",new("environment",p.TargetEnvironmentId,targetScope),ct);var submitAllowed=mappingAllowed&&p.RequestedBy==actor.UserId&&(p.GateOrigin=="PipelineRunStage"?await auth.CanAsync(actor,"pipeline.run",new("environment",p.TargetEnvironmentId,targetScope),ct):await auth.CanAsync(actor,"release.create",new("environment",p.SourceEnvironmentId,sourceScope),ct));var reasons=new List<string>();if(!mappingVisible)reasons.Add("target_mapping_restricted");var contextCurrent=false;
  try{var policy=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(r=>r.ProjectId==p.ProjectId,ct);var frozen=PromotionMappingService.Read<DeliveryPolicyDto>(p.FrozenPolicyJson);var target=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==p.TargetEnvironmentId,ct);if(p.GateOrigin=="PipelineRunStage"){var gate=await gates.ResolvePromotionAsync(p.Id,actor,ct);await gates.RequireStageWritableAsync(gate,actor,ct);await gates.RequirePassedPredecessorAsync(gate,p.AcceptanceId,ct);await WebApi.Infrastructure.Releases.ReleaseService.RequirePipelineApprovalAsync(db,p.OrganizationId,target,gate.Pipeline!.Approval,ct);contextCurrent=target.AccessAddressRevision==p.TargetAccessAddressRevision&&(target.DesiredConfigVersion??0)==p.BaselineConfigVersion;}else contextCurrent=policy is not null&&policy.Revision==frozen.Revision&&policy.Mode==frozen.Mode&&policy.SourceEnvironmentId==p.SourceEnvironmentId&&policy.TargetEnvironmentId==p.TargetEnvironmentId&&target.AccessAddressRevision==p.TargetAccessAddressRevision&&(target.DesiredConfigVersion??0)==p.BaselineConfigVersion;if(p.AcceptanceId is Guid accepted)await acceptances.RequireAcceptedCurrentAsync(accepted,p.ArtifactId,actor,ct);else contextCurrent=false;}catch(ApiException e)when(e.Status is 403 or 404 or 409 or 422){contextCurrent=false;reasons.Add(e.Code);}
  var precheckState=p.PrecheckJson is null?null:PromotionMappingService.Read<PromotionPrecheckState>(p.PrecheckJson);var precheck=mappingVisible?precheckState?.Result:null;var impact=await auth.CanAsync(actor,"app.read",new("environment",p.TargetEnvironmentId,targetScope),ct)?precheckState?.CredentialImpact:null;var canSubmit=p.Status=="Draft"&&submitAllowed&&contextCurrent&&precheck?.CanSubmit==true&&precheck.CandidateHash==p.CandidateHash&&precheck.MappingRevision==p.MappingRevision;
  Guid? publisher=null;var currentDelivery=false;try{var current=await running.ReadAsync(p.TargetEnvironmentId,null,ct);publisher=current.Release.PublishRequestedBy;currentDelivery=(await ResolvePromotionAsync(current.Release.Id,ct))?.Id==p.Id&&p.TargetReleaseId is Guid formal&&await db.Set<ReleaseRecord>().AnyAsync(r=>r.Id==formal&&r.ToConfigVersion==current.Release.ToConfigVersion,ct);}catch(ApiException e)when(e.Status is 409 or 422){reasons.Add(e.Code);}
  var canVerify=(p.Status is "Verifying" or "VerificationFailed")&&currentDelivery&&publisher is not null&&publisher!=actor.UserId&&p.RequestedBy!=actor.UserId&&await auth.CanAsync(actor,"release.verify",new("environment",p.TargetEnvironmentId,targetScope),ct);
  var pipelineWritable=true;Guid? runCreator=null;if(p.GateOrigin=="PipelineRunStage"){try{var gate=await gates.ResolvePromotionAsync(p.Id,actor,ct);await gates.RequireStageWritableAsync(gate,actor,ct);runCreator=(await db.Set<ReleasePipelineRun>().AsNoTracking().SingleAsync(r=>r.Id==gate.Pipeline!.RunId,ct)).CreatedBy;}catch(ApiException e)when(e.Status is 403 or 404 or 409 or 422){pipelineWritable=false;reasons.Add(e.Code);}}var pipelineAdvance=p.GateOrigin!="PipelineRunStage"||pipelineWritable&&await auth.CanAsync(actor,"pipeline.run",new("environment",p.TargetEnvironmentId,targetScope),ct);canVerify&=pipelineWritable&&actor.UserId!=runCreator;
  var eligibility=new PromotionActionEligibility(p.Status=="Draft"&&mappingAllowed&&pipelineAdvance,canSubmit&&pipelineAdvance,p.Status=="Ready"&&contextCurrent&&pipelineAdvance&&await auth.CanAsync(actor,"release.publish",new("environment",p.TargetEnvironmentId,targetScope),ct),canVerify,p.RequestedBy==actor.UserId&&create&&pipelineAdvance&&(p.Status is "Draft" or "WaitingApproval" or "Ready"),publisher,reasons.Distinct().ToArray());
  return PromotionMappingService.View(p,artifact.ArtifactHash,mappingVisible?actualMapping:null,impact) with{Deployment=new(latest??p.TargetReleaseId,nodes),Eligibility=eligibility,Precheck=precheck,Pipeline=p.PipelineRunStageId is Guid stage?await pipelines.TraceStageAsync(stage,actor,ct):null};
 }
 private static ApiException InvalidChain()=>new(409,"invalid_delivery_recovery_chain","发布恢复追溯存在循环、跨环境或无效正式关联。");
}

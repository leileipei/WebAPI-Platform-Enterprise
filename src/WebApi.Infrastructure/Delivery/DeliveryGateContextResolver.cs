using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Delivery.Pipelines;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;

/// <summary>Resolves server-owned facts. Callers never supply the gate origin or a profile.</summary>
public sealed class DeliveryGateContextResolver(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth)
{
 private static readonly string[] sourceTypes=["InterfaceFunction","Integration","ContractCompatibility"];
 private static readonly string[] productionTypes=["EntryConnectivity","AuthenticationAuthorization","CriticalBusinessCall"];
 private static ApiException Changed()=>new(409,"pipeline_gate_changed","流水线的生效规则、阶段或尝试关联已变化，请刷新当前阶段。");
 public async Task<DeliveryGateContext> ResolvePromotionAsync(Guid promotionId,ActorContext actor,CancellationToken ct)
 {
  var promotion=await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==promotionId,ct)??throw ScopeResolver.Missing();
  await RequireReadsAsync(promotion.ProjectId,[promotion.SourceEnvironmentId,promotion.TargetEnvironmentId],actor,ct);
  return await ResolvePromotionFactsAsync(promotion,ct);
 }
 public Task<DeliveryGateContext> ResolveStageAsync(Guid stageId,ActorContext actor,CancellationToken ct)=>ReadStageAsync(stageId,actor,ct,true);
 internal Task<DeliveryGateContext> ResolveHistoricalStageAsync(Guid stageId,ActorContext actor,CancellationToken ct)=>ReadStageAsync(stageId,actor,ct,false);
 private async Task<DeliveryGateContext> ReadStageAsync(Guid stageId,ActorContext actor,CancellationToken ct,bool requireActivePolicy)
 {
  var stage=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleOrDefaultAsync(s=>s.Id==stageId,ct)??throw ScopeResolver.Missing();
  var environments=new List<Guid>{stage.EnvironmentId};
  if(stage.SourceStageId is Guid sourceId){var source=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleOrDefaultAsync(s=>s.Id==sourceId&&s.RunId==stage.RunId,ct)??throw Changed();environments.Add(source.EnvironmentId);}
  await RequireReadsAsync(stage.ProjectId,environments,actor,ct);
  return await ResolveStageFactsAsync(stageId,null,ct,requireActivePolicy);
 }
 public async Task<DeliveryGateContext> ResolveConnectionAsync(Guid projectId,ActorContext actor,CancellationToken ct)
 {
  var context=await ResolveConnectionFactsAsync(projectId,ct);
  await RequireReadsAsync(projectId,[context.SourceEnvironmentId],actor,ct);return context;
 }
 // Used within an already-authorized business transaction, including Worker execution.
 internal async Task<DeliveryGateContext> ResolveConnectionFactsAsync(Guid projectId,CancellationToken ct)
 {
  var policy=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(p=>p.ProjectId==projectId,ct)??throw PromotionPrecheckService.Stale();
  if(policy.Mode=="PipelineRequired"||policy.ActivePipelineVersionId is not null)throw new ApiException(409,"pipeline_stage_required","该项目使用流水线交付，请从当前阶段办理。");
  return new("ProjectConnection",projectId,policy.SourceEnvironmentId,policy.TargetEnvironmentId,policy.Revision,policy.Mode,new(policy.RequiredTestTypes,policy.VerificationValidityMinutes,null),new(productionTypes,policy.VerificationValidityMinutes,null),PolicyId:policy.Id);
 }
 internal async Task<DeliveryGateContext> ResolveSourceConnectionFactsAsync(Guid projectId,Guid sourceEnvironmentId,CancellationToken ct)
 {
  if(!await db.Set<ProjectDeliveryPolicy>().AsNoTracking().AnyAsync(p=>p.ProjectId==projectId,ct))return new("ProjectConnection",projectId,sourceEnvironmentId,Guid.Empty,0,"Legacy",new(sourceTypes,1440,null),new(productionTypes,1440,null));
  var result=await ResolveConnectionFactsAsync(projectId,ct);if(result.SourceEnvironmentId!=sourceEnvironmentId)throw new ApiException(422,"delivery_source_mismatch","当前制品不是项目连接的来源环境。");return result;
 }
 internal async Task<DeliveryGateContext> ResolveFrozenPromotionFactsAsync(ReleasePromotion promotion,CancellationToken ct)
 {
  if(promotion.GateOrigin=="PipelineRunStage")return await ResolveStageFactsAsync(promotion.PipelineRunStageId??throw Changed(),promotion,ct,false);
  if(promotion.GateOrigin!="ProjectConnection"||promotion.PipelineRunStageId is not null||promotion.StageAttemptId is not null)throw Changed();
  var policy=Read<DeliveryPolicyDto>(promotion.FrozenPolicyJson);
  if(policy.SourceEnvironmentId!=promotion.SourceEnvironmentId||policy.TargetEnvironmentId!=promotion.TargetEnvironmentId)throw Changed();
  return new("ProjectConnection",promotion.ProjectId,promotion.SourceEnvironmentId,promotion.TargetEnvironmentId,policy.Revision,policy.Mode,new(policy.RequiredTestTypes,policy.VerificationValidityMinutes,null),new(productionTypes,policy.VerificationValidityMinutes,null),PolicyId:policy.Id);
 }
 internal static DeliveryPolicyDto ConnectionView(DeliveryGateContext c)=>new(c.PolicyId,c.ProjectId,c.SourceEnvironmentId,c.TargetEnvironmentId,c.Mode,c.SourceEvidence.RequiredTypes,c.SourceEvidence.ValidityMinutes,c.PolicyRevision);
 internal async Task<DeliveryGateContext> ResolvePromotionFactsAsync(ReleasePromotion promotion,CancellationToken ct)
 {
  if(promotion.GateOrigin=="ProjectConnection")
  {
   if(promotion.PipelineRunStageId is not null||promotion.StageAttemptId is not null)throw Changed();
   var context=await ResolveConnectionFactsAsync(promotion.ProjectId,ct);var frozen=Read<DeliveryPolicyDto>(promotion.FrozenPolicyJson);
   if(context.SourceEnvironmentId!=promotion.SourceEnvironmentId||context.TargetEnvironmentId!=promotion.TargetEnvironmentId||context.PolicyRevision!=frozen.Revision||context.Mode!=frozen.Mode)throw PromotionPrecheckService.Stale();return context;
  }
  if(promotion.GateOrigin!="PipelineRunStage"||promotion.PipelineRunStageId is not Guid stageId||promotion.StageAttemptId is null)throw Changed();
  var stageContext=await ResolveStageFactsAsync(stageId,promotion,ct);
  var stage=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleAsync(s=>s.Id==stageId,ct);
  if(stage.SourceStageId is not Guid predecessorId)throw Changed();
  var predecessor=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleOrDefaultAsync(s=>s.Id==predecessorId&&s.RunId==stage.RunId&&s.ProjectId==stage.ProjectId,ct)??throw Changed();
  var profile=await ReadProfileAsync(predecessor,ct);
  if(predecessor.StageOrder!=stage.StageOrder-1||profile.IsProduction||predecessor.EnvironmentId!=promotion.SourceEnvironmentId||stage.EnvironmentId!=promotion.TargetEnvironmentId||predecessor.StageArtifactId!=promotion.ArtifactId)throw Changed();
  var artifact=await db.Set<ReleaseArtifact>().AsNoTracking().SingleOrDefaultAsync(a=>a.Id==promotion.ArtifactId&&a.ProjectId==promotion.ProjectId&&a.OrganizationId==promotion.OrganizationId,ct)??throw Changed();
  if(artifact.SourceEnvironmentId!=predecessor.EnvironmentId||artifact.SourceReleaseId!=promotion.SourceReleaseId||artifact.ArtifactHash!=stageContext.Pipeline!.RootArtifactHash)throw Changed();
  return stageContext with{SourceEnvironmentId=predecessor.EnvironmentId,SourceEvidence=new(profile.RequiredTypes,profile.EvidenceValidityMinutes,predecessor.ProfileHash)};
 }
 internal async Task<DeliveryGateContext> ResolveStageFactsAsync(Guid stageId,ReleasePromotion? promotion,CancellationToken ct,bool requireActivePolicy=true)
 {
  var stage=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleOrDefaultAsync(s=>s.Id==stageId,ct)??throw ScopeResolver.Missing();
  var run=await db.Set<ReleasePipelineRun>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==stage.RunId&&r.ProjectId==stage.ProjectId,ct)??throw Changed();
  var policy=await db.Set<ProjectDeliveryPolicy>().AsNoTracking().SingleOrDefaultAsync(p=>p.ProjectId==run.ProjectId&&p.OrganizationId==run.OrganizationId,ct);
  if(requireActivePolicy&&(policy is null||policy.Mode!="PipelineRequired"||policy.ActivePipelineVersionId!=run.PipelineVersionId||policy.Revision!=run.PolicyRevision))throw Changed();
  var profile=await ReadProfileAsync(stage,ct);ReleasePipelineStageAttempt? current=null;
  if(stage.CurrentAttemptId is Guid currentId){current=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().SingleOrDefaultAsync(a=>a.Id==currentId&&a.RunStageId==stage.Id&&a.RunId==run.Id&&a.ProjectId==run.ProjectId&&a.EnvironmentId==stage.EnvironmentId,ct)??throw Changed();}
  Guid? formalId=null;
  if(promotion is not null)
  {
   if(promotion.ProjectId!=run.ProjectId||promotion.OrganizationId!=run.OrganizationId||promotion.TargetEnvironmentId!=stage.EnvironmentId||current is null)throw Changed();
   var formal=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().SingleOrDefaultAsync(a=>a.Id==promotion.StageAttemptId&&a.RunStageId==stage.Id&&a.RunId==run.Id&&a.PromotionId==promotion.Id,ct)??throw Changed();formalId=formal.Id;
   var origin=current;
   while(origin.Id!=formal.Id){if(origin.OriginAttemptId is not Guid originId)throw Changed();var previous=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().SingleOrDefaultAsync(a=>a.Id==originId&&a.RunStageId==stage.Id&&a.RunId==run.Id,ct)??throw Changed();if(previous.AttemptNo>=origin.AttemptNo)throw Changed();origin=previous;}
  }
  var evidence=new DeliveryEvidenceProfile(profile.RequiredTypes,profile.EvidenceValidityMinutes,stage.ProfileHash);
  return new("PipelineRunStage",run.ProjectId,stage.EnvironmentId,stage.EnvironmentId,run.PolicyRevision,"PipelineRequired",evidence,evidence,new(run.Id,stage.Id,formalId,current?.Id,run.DefinitionHash,run.RootArtifactHash,run.CreatedBy,profile.Approval));
 }
 private async Task<PipelineStageProfile> ReadProfileAsync(ReleasePipelineRunStage stage,CancellationToken ct)
 {
  var run=await db.Set<ReleasePipelineRun>().AsNoTracking().SingleAsync(r=>r.Id==stage.RunId,ct);var version=await db.Set<ReleasePipelineVersion>().AsNoTracking().SingleOrDefaultAsync(v=>v.Id==run.PipelineVersionId&&v.ProjectId==run.ProjectId,ct)??throw Changed();
  var content=Read<PipelineVersionContent>(version.ContentJson);var profile=Read<PipelineStageProfile>(stage.ProfileJson);if(content.Profiles is null||content.Definition is null||profile.RequiredTypes is null)throw Changed();var matches=content.Profiles.Where(p=>p.Order==stage.StageOrder).ToArray();if(matches.Length!=1)throw Changed();var frozen=matches[0];
  if(Hash(content)!=version.DefinitionHash||run.DefinitionHash!=version.DefinitionHash||frozen is null||Hash(frozen)!=stage.ProfileHash||Hash(profile)!=stage.ProfileHash||profile.Order!=stage.StageOrder||profile.EnvironmentId!=stage.EnvironmentId)throw Changed();return profile;
 }
 public async Task RequireStageWritableAsync(DeliveryGateContext context,ActorContext actor,CancellationToken ct)
 {
  if(context.Pipeline is not{} binding)throw Changed();var actual=await ResolveStageAsync(binding.StageId,actor,ct);
  if(actual.Origin!=context.Origin||actual.ProjectId!=context.ProjectId||actual.TargetEnvironmentId!=context.TargetEnvironmentId||actual.PolicyRevision!=context.PolicyRevision||actual.Pipeline!.RunId!=binding.RunId||actual.Pipeline.DefinitionHash!=binding.DefinitionHash||actual.Pipeline.RootArtifactHash!=binding.RootArtifactHash||actual.Pipeline.RunCreatedBy!=binding.RunCreatedBy||actual.Pipeline.CurrentAttemptId!=binding.CurrentAttemptId||actual.TargetEvidence.ProfileHash!=context.TargetEvidence.ProfileHash)throw Changed();
  var run=await db.Set<ReleasePipelineRun>().AsNoTracking().SingleAsync(r=>r.Id==binding.RunId,ct);var stage=await db.Set<ReleasePipelineRunStage>().AsNoTracking().SingleAsync(s=>s.Id==binding.StageId,ct);
  if(run.Status!="Active"||run.CurrentStageOrder!=stage.StageOrder||stage.CurrentAttemptId is not Guid attemptId)throw new ApiException(409,"pipeline_stage_not_writable","当前运行或阶段不允许新操作。");
  var attempt=await db.Set<ReleasePipelineStageAttempt>().AsNoTracking().SingleAsync(a=>a.Id==attemptId,ct);if(attempt.Status!="Active"||!PipelineStateRules.CanWrite(run.Status,stage.Status,attempt.DeadlineAt,DateTimeOffset.UtcNow))throw new ApiException(409,"pipeline_attempt_not_writable","当前阶段尝试已结束或超时，请重新办理。");
  var scope=await scopes.EnvironmentAsync(stage.EnvironmentId,ct);
  if(!await db.Set<UserProjectScope>().AsNoTracking().AnyAsync(g=>g.UserId==actor.UserId&&g.OrganizationId==scope.OrganizationId&&(g.ProjectId==null||g.ProjectId==scope.ProjectId)&&(g.EnvironmentId==null||g.EnvironmentId==scope.EnvironmentId)&&g.AccessMode=="read_write",ct))throw new ApiException(403,"scope_denied","当前阶段需要该环境的写入范围。");
  // The command owns its functional permission: pipeline.run for advancement, or the
  // existing test/approval/verification permission for those independent duties.
  var environment=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==stage.EnvironmentId,ct);var profile=await ReadProfileAsync(stage,ct);if(environment.IsProduction!=profile.IsProduction)throw Changed();
 }
 private async Task RequireReadsAsync(Guid projectId,IReadOnlyCollection<Guid> environmentIds,ActorContext actor,CancellationToken ct)
 {
  foreach(var id in environmentIds.Distinct()){var scope=await scopes.EnvironmentAsync(id,ct);if(scope.ProjectId!=projectId)throw ScopeResolver.Missing();foreach(var permission in new[]{"environment.read","release.read"})if(!await auth.CanAsync(actor,permission,new("environment",id,scope),ct))throw ScopeResolver.Missing();}
 }
 private static T Read<T>(string json){try{return JsonSerializer.Deserialize<T>(json,CanonicalJson.Options)??throw Changed();}catch(JsonException){throw Changed();}}
 private static string Hash<T>(T value)=>Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(value)));
}

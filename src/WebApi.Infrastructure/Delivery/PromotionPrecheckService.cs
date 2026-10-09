using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Comparisons;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
namespace WebApi.Infrastructure.Delivery;
internal sealed record PromotionPrecheckState(PromotionPrecheckDto Result,PromotionPrecheckRequest Input,Guid ActorId,DateTimeOffset CreatedAt,long AcceptanceRevision,Guid ApprovalFlowId,long ApprovalFlowRevision,string ApprovalRulesHash,string? ReportHash,IReadOnlyList<SharedCredentialImpact>? CredentialImpact=null);
public sealed class PromotionPrecheckService(WebApiDbContext db,ScopeResolver scopes,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,DeliveryLockCoordinator locks,PromotionMappingService mapping,PromotionCandidateBuilder candidates,RunningDeploymentReader running,VerificationReportStore reports,VersionComparisonService comparisons,VersionRiskReviewService reviews,PublishSettings settings,DeliveryGateContextResolver gates)
{
 public Task<PromotionPrecheckDto> PrecheckAsync(Guid id,ActorContext actor,CancellationToken ct)=>PrecheckAsync(id,new(),actor,ct);
 public async Task<PromotionPrecheckDto> PrecheckAsync(Guid id,PromotionPrecheckRequest input,ActorContext actor,CancellationToken ct)
 {
  var initial=await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(initial.TargetEnvironmentId,ct);
  return await commands.ExecuteAsync(actor,scope,"promotion.precheck",async(_,token)=>{
   await locks.LockAsync(initial.SourceEnvironmentId,initial.TargetEnvironmentId,token);var p=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==id,token);await mapping.RequireAsync(p,actor,token);if(p.Status!="Draft")throw PromotionMappingService.NotDraft();
   Validate(input);return await idempotency.ExecuteAsync(new(actor.UserId,scope,"promotion.precheck",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,input}),async inner=>{
    await db.Database.CurrentTransaction!.CreateSavepointAsync("promotion_precheck",inner);
    try{var evaluated=await EvaluateAsync(p,input,actor,inner);p.Revision++;var value=evaluated.State.Result with{Revision=p.Revision};p.PrecheckJson=PromotionMappingService.Json(evaluated.State with{Result=value});p.CandidateHash=value.CandidateHash;return value;}
    catch(ApiException e)when(e.Status is 409 or 412 or 422){await db.Database.CurrentTransaction!.RollbackToSavepointAsync("promotion_precheck",inner);db.ChangeTracker.Clear();p=await db.Set<ReleasePromotion>().SingleAsync(row=>row.Id==id,inner);p.Revision++;var value=new PromotionPrecheckDto(p.Id,"Blocked",false,p.Revision,p.MappingRevision,0,p.TargetAccessAddressRevision,p.BaselineConfigVersion,null,[new("candidate","Failed",true,e.Code),new("upstream_health","Unknown",true,"candidate_not_confirmed")],[]);p.PrecheckJson=PromotionMappingService.Json(new PromotionPrecheckState(value,input,actor.UserId,DateTimeOffset.UtcNow,0,Guid.Empty,0,"",null));p.CandidateHash=null;return value;}
   },token);
  },ct);
 }
 internal static void Validate(PromotionPrecheckRequest input)
 {if(input.RiskReviewIds is{Count:>100}||input.RiskReviewIds is{} ids&&ids.Distinct().Count()!=ids.Count||input.UpstreamHealthComment.Length>2000||input.ConfirmUpstreamHealth&&input.UpstreamHealthComment.Trim().EnumerateRunes().Count()<10||!input.ConfirmUpstreamHealth&&(input.UpstreamHealthComment.Length>0||input.UpstreamHealthReportId is not null))throw new ApiException(422,"invalid_promotion_precheck","人工健康确认需至少10字说明，评审引用与报告必须有效。");}
 internal async Task<(FrozenReleaseCandidate Candidate,PromotionPrecheckState State)> EvaluateAsync(ReleasePromotion p,PromotionPrecheckRequest input,ActorContext actor,CancellationToken ct)
 {
  Validate(input);var policy=await RequireContextAsync(p,ct);var prepared=await candidates.PrepareAsync(p,actor,ct);var candidate=prepared.Candidate;var checks=new List<PromotionCheckDto>{new("artifact_and_acceptance","Passed",false,"current_accepted_source"),new("mapping","Passed",false,"target_resources_prepared"),new("upstream_address_policy","Passed",false,"snapshot_compiler_validated")};
  var target=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==p.TargetEnvironmentId,ct);checks.Add(new("public_entry",string.IsNullOrEmpty(target.GatewayPublicUrl)?"Failed":"Passed",string.IsNullOrEmpty(target.GatewayPublicUrl),string.IsNullOrEmpty(target.GatewayPublicUrl)?"public_entry_required":"public_entry_configured"));
  var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==target.Id&&n.Enabled).ToArrayAsync(ct);var online=nodes.Length>=settings.MinimumNodes&&nodes.All(n=>n.IdentityHash.Length==64&&Guid.TryParse(n.InstanceId,out _)&&n.LastHeartbeatAt>=DateTimeOffset.UtcNow.AddSeconds(-settings.HeartbeatGraceSeconds));checks.Add(new("gateway_cohort",online?"Passed":"Failed",!online,online?"configured_cohort_online":"gateway_cohort_unavailable"));
  WebApi.Contracts.Runtime.RuntimeSnapshot? baseline=null;
  if(p.BaselineConfigVersion==0)checks.Add(policy.Pipeline is not null&&!target.IsProduction?new("rollback_snapshot","Passed",false,"nonproduction_initialization"):new("rollback_snapshot","Unknown",true,"production_baseline_required"));
  else{var current=await running.ReadAsync(target.Id,null,ct);baseline=current.Snapshot.Snapshot;checks.Add(new("rollback_snapshot","Passed",false,"current_production_snapshot_verified"));}
  var scope=await scopes.EnvironmentAsync(target.Id,ct);var references=await reviews.ResolveReferencesAsync(scope,candidate.VersionIds,input.RiskReviewIds,actor,ct);candidate=candidate with{RiskReviewReferences=references};
  foreach(var version in candidate.Versions){var from=baseline?.Routes.Where(r=>r.ApiId==version.Api.Id).Select(r=>r.ApiVersionId).Distinct().SingleOrDefault()??Guid.Empty;
   if(from==Guid.Empty||from==version.Version.Id){checks.Add(new("contract:"+version.Api.Id,"Passed",false,from==Guid.Empty?"new_api_no_baseline_contract":"same_sealed_contract"));continue;}
   var row=await db.Set<ApiVersionComparison>().AsNoTracking().Where(c=>c.ApiId==version.Api.Id&&c.FromVersionId==from&&c.ToVersionId==version.Version.Id).OrderByDescending(c=>c.CreatedAt).ThenByDescending(c=>c.Id).FirstOrDefaultAsync(ct);
   if(row is null){checks.Add(new("contract:"+version.Api.Id,"Unknown",true,"baseline_comparison_required"));continue;}
   var comparison=await comparisons.GetAsync(row.Id,actor,ct);var report=comparison.Report;var accepted=references.SingleOrDefault(r=>r.ApiId==version.Api.Id&&r.ComparisonId==row.Id&&r.Summary.FromVersionId==from&&r.Summary.Decision=="AcceptedRisk");
   var uncertain=report.Coverage!="Complete"||report.Counts.Unknown>0||report.Counts.Breaking>0;var invalid=comparison.Freshness!="Current"||report.Coverage=="Invalid";checks.Add(new("contract:"+version.Api.Id,invalid?"Failed":uncertain?"Unknown":"Passed",invalid||uncertain&&accepted is null,invalid?"comparison_stale_or_invalid":uncertain?accepted is null?"manual_risk_acceptance_required":"risk_manually_accepted":"comparison_complete"));
  }
  string? reportHash=null;if(input.UpstreamHealthReportId is Guid reportId){var report=await reports.GetMetadataAsync(reportId,actor,ct);if(report.PromotionId!=p.Id||report.EnvironmentId!=p.TargetEnvironmentId||report.ArtifactId is not null)throw ScopeResolver.Missing();reportHash=report.Sha256;}
  checks.Add(new("upstream_health",input.ConfirmUpstreamHealth?"Passed":"Unknown",!input.ConfirmUpstreamHealth,input.ConfirmUpstreamHealth?"manually_confirmed_not_automatically_executed":"manual_target_health_evidence_required"));
  ApprovalFlow? flow;ApprovalRule[] rules;
  if(policy.Pipeline is{} binding){rules=(await ReleaseService.RequirePipelineApprovalAsync(db,p.OrganizationId,target,binding.Approval,ct)).ToArray();flow=binding.Approval is null?null:await db.Set<ApprovalFlow>().AsNoTracking().SingleAsync(f=>f.Id==binding.Approval.FlowId,ct);checks.Add(new(target.IsProduction?"production_approval":"stage_approval","Passed",false,flow is null?"nonproduction_no_deployment_approval":"two_level_independent_approval"));}
  else{
   flow=target.ReleasePolicyId is Guid flowId?await db.Set<ApprovalFlow>().AsNoTracking().SingleOrDefaultAsync(f=>f.Id==flowId&&f.OrganizationId==p.OrganizationId&&f.Enabled,ct):null;
   rules=flow is null?[]:await db.Set<ApprovalStep>().AsNoTracking().Where(s=>s.FlowId==flow.Id).OrderBy(s=>s.StepOrder).Select(s=>new ApprovalRule(s.StepOrder,s.RoleCode,s.RequiredCount)).ToArrayAsync(ct);var validFlow=flow is not null&&rules.Length==2&&rules[0].StepOrder==1&&rules[1].StepOrder==2&&rules.All(r=>r.RequiredCount is >=1 and <=5&&!string.IsNullOrWhiteSpace(r.RoleCode));checks.Add(new("production_approval",validFlow?"Passed":"Failed",!validFlow,validFlow?"two_level_independent_approval":"approval_policy_required"));
  }
  var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(candidate)));var acceptance=await db.Set<ReleaseTestAcceptance>().AsNoTracking().SingleAsync(a=>a.Id==p.AcceptanceId,ct);var canSubmit=checks.All(c=>!c.Blocking);var value=new PromotionPrecheckDto(p.Id,canSubmit?checks.Any(c=>c.Status=="Unknown")?"PassedWithRisk":"Passed":"Blocked",canSubmit,p.Revision,p.MappingRevision,policy.PolicyRevision,target.AccessAddressRevision,p.BaselineConfigVersion,hash,checks,candidate.ResourceRevisions);
  return new(candidate,new(value,input,actor.UserId,DateTimeOffset.UtcNow,acceptance.Revision,flow?.Id??Guid.Empty,flow?.Revision??0,HashRules(rules),reportHash,candidate.SharedCredentialImpact));
 }
 internal async Task<DeliveryGateContext> RequireContextAsync(ReleasePromotion p,CancellationToken ct)
 {
  var context=await gates.ResolvePromotionFactsAsync(p,ct);var target=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==p.TargetEnvironmentId,ct);
  if(target.AccessAddressRevision!=p.TargetAccessAddressRevision||(target.DesiredConfigVersion??0)!=p.BaselineConfigVersion)throw Stale();return context;
 }
 internal static string HashRules(IReadOnlyList<ApprovalRule> rules)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(rules)));
 internal static ApiException Stale()=>new(409,"promotion_preconditions_changed","晋级连接、来源验收、目标基线、映射或入口条件已变化，请建立新申请并重新核对。");
}

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Delivery;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
internal sealed record ProductionContext(Guid ReleaseId,long ConfigVersion,long DeploymentSequence,string SnapshotHash,long AccessAddressRevision,string PublicOrigin,string BasePath,long PolicyRevision,Guid PublisherId);
public sealed class ProductionVerificationService(WebApiDbContext db,PromotionReadService reads,DeliveryLockCoordinator locks,RunningDeploymentReader running,HistoricalSnapshotService history,ReleaseArtifactService artifacts,VerificationReportStore reports,ScopeResolver scopes,AuthorizationService auth,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext)
{
 public async Task<ProductionVerificationContextDto> ContextAsync(Guid id,ActorContext actor,CancellationToken ct)
 {await reads.ReadAsync(id,actor,ct);var p=await db.Set<ReleasePromotion>().AsNoTracking().SingleAsync(p=>p.Id==id,ct);var context=await RuntimeContextAsync(p,ct);return View(context);}
 public async Task<IReadOnlyList<ReleaseVerificationDto>> ListAsync(Guid id,ActorContext actor,CancellationToken ct)
 {await reads.ReadAsync(id,actor,ct);return(await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>v.PromotionId==id&&v.Phase=="Production").OrderByDescending(v=>v.CreatedAt).ThenByDescending(v=>v.Id).Take(100).ToArrayAsync(ct)).Select(ReleaseVerificationService.View).ToArray();}
 public async Task<PromotionDto> RecordAsync(Guid id,RecordVerificationRequest request,ActorContext actor,CancellationToken ct)
 {
  await reads.ReadAsync(id,actor,ct);var initial=await db.Set<ReleasePromotion>().AsNoTracking().SingleAsync(p=>p.Id==id,ct);var scope=await scopes.EnvironmentAsync(initial.TargetEnvironmentId,ct);
  return await commands.ExecuteAsync(actor,scope,"release.verification.production",async(_,token)=>{
   await locks.LockAsync(initial.SourceEnvironmentId,initial.TargetEnvironmentId,token);await reads.ReadAsync(id,actor,token);await auth.RequireAsync(actor,"release.verify",new("environment",initial.TargetEnvironmentId,scope),token);ReleaseVerificationService.Validate(request,PromotionCompletionRules.RequiredTypes);
   if(request.ExpectedContextHash is null||request.ExpectedContextHash.Length!=64||request.ExpectedContextHash.Any(c=>c is not(>= '0' and <= '9') and not(>= 'a' and <= 'f')))throw new ApiException(422,"production_verification_context_required","请先查看本次生产验证的实际版本、序列与入口上下文。");
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"release.verification.production",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,request}),async inner=>{
    var p=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==id,inner);if(p.Status is not("Verifying" or "VerificationFailed"))throw new ApiException(409,"promotion_not_verifying","晋级尚未完成节点确认或已结束，不能追加生产验证。");var context=await RuntimeContextAsync(p,inner);if(Hash(context)!=request.ExpectedContextHash)throw Changed();
    if(actor.UserId==p.RequestedBy||actor.UserId==context.PublisherId)throw new ApiException(403,"independent_production_verifier_required","生产验证人与晋级申请人、实际发布执行人必须不同，管理员同样遵守。");
    var deployed=await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==context.ReleaseId,inner);if(deployed.CompletedAt is null||request.FinishedAt<deployed.CompletedAt)throw new ApiException(422,"verification_predates_deployment","生产验证完成时间不能早于本次全节点确认。");
    VerificationReport? report=null;if(request.ReportId is Guid reportId){report=await reports.GetMetadataAsync(reportId,actor,inner);if(report.PromotionId!=p.Id||report.ArtifactId is not null||report.EnvironmentId!=p.TargetEnvironmentId)throw ScopeResolver.Missing();}
    var serialized=PromotionMappingService.Json(context);var prior=p.VerificationContextJson is null?null:PromotionMappingService.Read<ProductionContext>(p.VerificationContextJson);if(prior!=context){var first=prior is null;db.Add(Event(p,"Verifying",first?"production_context_bound":"production_context_changed",actor.UserId,context.ReleaseId));p.VerificationContextJson=serialized;p.Revision++;p.Status="Verifying";p.CompletedAt=null;}
    var policy=PromotionMappingService.Read<DeliveryPolicyDto>(p.FrozenPolicyJson);var row=new ReleaseVerification{OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,ArtifactId=p.ArtifactId,PromotionId=p.Id,ReleaseId=context.ReleaseId,EnvironmentId=p.TargetEnvironmentId,ConfigVersion=context.ConfigVersion,DeploymentSequence=context.DeploymentSequence,SnapshotHash=context.SnapshotHash,AccessAddressRevision=context.AccessAddressRevision,AccessContextJson=serialized,PolicyRevision=context.PolicyRevision,Phase="Production",Type=request.Type,Result=request.Result,IsManual=true,ReportId=report?.Id,ReportHash=report?.Sha256,Comment=request.Comment??"",StartedAt=request.StartedAt.ToUniversalTime(),FinishedAt=request.FinishedAt.ToUniversalTime(),ExpiresAt=request.FinishedAt.ToUniversalTime().AddMinutes(policy.VerificationValidityMinutes),CreatedBy=actor.UserId};db.Add(row);
    db.Add(new ReleasePromotionEvent{OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,EnvironmentId=p.TargetEnvironmentId,PromotionId=p.Id,Phase="Verification",FromStatus=p.Status,ToStatus=p.Status,ReasonCode="production_"+request.Result.ToLowerInvariant(),ActorId=actor.UserId,ReleaseId=context.ReleaseId,VerificationId=row.Id});await db.SaveChangesAsync(inner);await TryCompleteAsync(p.Id,inner);await db.SaveChangesAsync(inner);return await reads.ReadAsync(id,actor,inner);
   },token);
  },ct);
 }
 public async Task<bool> TryCompleteAsync(Guid id,CancellationToken ct)
 {
  if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Delivery completion requires an owning governance transaction.");var p=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==id,ct);await locks.LockAsync(p.SourceEnvironmentId,p.TargetEnvironmentId,ct);if(p.Status=="Completed")return true;if(p.Status is not("Verifying" or "VerificationFailed"))return false;
  var context=await RuntimeContextAsync(p,ct);var serialized=PromotionMappingService.Json(context);var facts=new List<ProductionEvidenceFact>();var authority=new Dictionary<Guid,bool>();
  foreach(var type in PromotionCompletionRules.RequiredTypes){var row=await db.Set<ReleaseVerification>().AsNoTracking().Where(v=>v.PromotionId==id&&v.Phase=="Production"&&v.Type==type&&v.ReleaseId==context.ReleaseId&&v.AccessContextJson==serialized).OrderByDescending(v=>v.CreatedAt).ThenByDescending(v=>v.Id).FirstOrDefaultAsync(ct);if(row is null)continue;
   if(!authority.TryGetValue(row.CreatedBy,out var qualified)){qualified=row.CreatedBy!=p.RequestedBy&&row.CreatedBy!=context.PublisherId;try{if(qualified){var actor=new ActorContext(row.CreatedBy,"production-evidence-authority");await auth.RequireAsync(actor,"release.verify",new("environment",p.TargetEnvironmentId,await scopes.EnvironmentAsync(p.TargetEnvironmentId,ct)),ct);await artifacts.GetAsync(p.ArtifactId,actor,ct);}}catch(ApiException e)when(e.Status is 401 or 403 or 404 or 409){qualified=false;}authority[row.CreatedBy]=qualified;}
   var current=row.IsManual&&row.EnvironmentId==p.TargetEnvironmentId&&row.ArtifactId==p.ArtifactId&&row.ConfigVersion==context.ConfigVersion&&row.DeploymentSequence==context.DeploymentSequence&&row.SnapshotHash==context.SnapshotHash&&row.AccessAddressRevision==context.AccessAddressRevision&&row.PolicyRevision==context.PolicyRevision;
   if(row.ReportId is Guid reportId){var report=await db.Set<VerificationReport>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==reportId,ct);current&=report is not null&&report.PromotionId==id&&report.ArtifactId==null&&report.EnvironmentId==p.TargetEnvironmentId&&report.Sha256==row.ReportHash;}
   facts.Add(new(type,row.Result,qualified,current,row.ExpiresAt));
  }
  var state=PromotionCompletionRules.Evaluate(facts,DateTimeOffset.UtcNow);if(state!=p.Status){db.Add(Event(p,state,"production_"+state.ToLowerInvariant(),null,context.ReleaseId));p.Status=state;p.Revision++;p.CompletedAt=state=="Completed"?DateTimeOffset.UtcNow:null;}return state=="Completed";
 }
 private async Task<ProductionContext> RuntimeContextAsync(ReleasePromotion p,CancellationToken ct)
 {
  if(p.TargetReleaseId is not Guid targetId)throw Changed();var current=await running.ReadAsync(p.TargetEnvironmentId,null,ct);var owner=await reads.ResolvePromotionAsync(current.Release.Id,ct);var formal=await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==targetId&&r.EnvironmentId==p.TargetEnvironmentId,ct);
  if(owner?.Id!=p.Id||formal.ToConfigVersion!=current.Release.ToConfigVersion||current.Release.PublishRequestedBy is not Guid publisher||(await history.ReadAsync(p.TargetEnvironmentId,formal.ToConfigVersion,ct)).Hash!=current.Snapshot.Hash)throw Changed();
  if(string.IsNullOrWhiteSpace(current.Environment.GatewayPublicUrl))throw new ApiException(422,"production_entry_required","生产验证需要当前公开入口。");var policy=PromotionMappingService.Read<DeliveryPolicyDto>(p.FrozenPolicyJson);
  return new(current.Release.Id,current.Release.ToConfigVersion,current.Release.DeploymentSequence!.Value,current.Snapshot.Hash,current.Environment.AccessAddressRevision,current.Environment.GatewayPublicUrl,current.Environment.BasePath,policy.Revision,publisher);
 }
 private static string Hash(ProductionContext context)=>Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(context)));
 private static ProductionVerificationContextDto View(ProductionContext c)=>new(Hash(c),c.ReleaseId,c.ConfigVersion,c.DeploymentSequence,c.SnapshotHash,c.AccessAddressRevision,c.PublicOrigin,c.BasePath,c.PolicyRevision,c.PublisherId);
 private static ApiException Changed()=>new(409,"production_verification_context_changed","目标实际版本、部署序列或入口已变化，请刷新上下文并重新验证；其他部署不能完成原晋级。");
 private static ReleasePromotionEvent Event(ReleasePromotion p,string next,string reason,Guid? actor,Guid release)=>new(){OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,EnvironmentId=p.TargetEnvironmentId,PromotionId=p.Id,Phase="Verification",FromStatus=p.Status,ToStatus=next,ReasonCode=reason,ActorId=actor,ReleaseId=release};
}

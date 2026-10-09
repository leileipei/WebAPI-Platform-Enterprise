using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Domain.Delivery;
using WebApi.Domain.Routing;
using WebApi.Infrastructure.Commands;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
internal sealed record PromotionRouteState(PromotionRouteMapping Mapping,Guid? PreparedRouteId=null,IReadOnlyList<Guid>? PrivatePolicyIds=null,long PreparedRevision=0);
public sealed class PromotionMappingService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,ReleaseArtifactService artifacts,PolicyAccess policyAccess,PromotionCredentialSelection credentials,AuditedCommandExecutor commands,IdempotentCommandExecutor idempotency,CommandRequestContext requestContext,DeliveryLockCoordinator locks,DeliveryGateContextResolver gates)
{
 internal static ApiException Invalid()=>new(422,"invalid_promotion_mapping","映射须完整且引用本项目目标环境的有效资源与制品策略槽位。");
 internal static ApiException NotDraft()=>new(409,"promotion_not_draft","只有草稿晋级可编辑或准备映射。");
 internal static T Read<T>(string json)=>JsonSerializer.Deserialize<T>(json,CanonicalJson.Options)??throw Invalid();
 public async Task<CommandResult<PromotionDto>> SaveAsync(Guid id,PromotionMappingRequest request,string? etag,ActorContext actor,CancellationToken ct)
 {
  var initial=await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.EnvironmentAsync(initial.TargetEnvironmentId,ct);
  return await commands.ExecuteAsync(actor,scope,"promotion.mapping.save",async(_,token)=>{
   await LockAsync(initial,token);var p=await db.Set<ReleasePromotion>().SingleAsync(p=>p.Id==id,token);var artifact=await RequireAsync(p,actor,token);
   return await idempotency.ExecuteAsync(new(actor.UserId,scope,"promotion.mapping.save",requestContext.IdempotencyKey),CanonicalJson.Serialize(new{id,request,etag}),async inner=>{
    RevisionTag.Require(etag,p.Revision);if(p.Status!="Draft")throw NotDraft();await ValidateAsync(p,artifact.Content,request,actor,inner);
    var previous=await db.Set<ReleasePromotionMapping>().Where(m=>m.PromotionId==id).ToArrayAsync(inner);var next=new List<ReleasePromotionMapping>();
    foreach(var route in request.Routes){var old=previous.SingleOrDefault(m=>m.Kind=="Route"&&m.ResourceKey==route.ArtifactRouteKey);var state=old is null?null:Read<PromotionRouteState>(old.ParametersJson);var row=old??New(p,"Route",route.ArtifactRouteKey);row.ClusterId=route.ClusterId;row.TargetRouteId=route.TargetRouteId??state?.PreparedRouteId;row.ParametersJson=Json(new PromotionRouteState(route,state?.PreparedRouteId));row.Revision=p.MappingRevision+1;next.Add(row);if(old is null)db.Add(row);}
    foreach(var app in request.Applications){var key=app.ApplicationId.ToString("N");var old=previous.SingleOrDefault(m=>m.Kind=="Application"&&m.ResourceKey==key);var row=old??New(p,"Application",key);row.ApplicationId=app.ApplicationId;row.ParametersJson=Json(app);row.Revision=p.MappingRevision+1;next.Add(row);if(old is null)db.Add(row);}
    db.RemoveRange(previous.Where(m=>!next.Contains(m)));p.MappingRevision++;p.Revision++;p.CandidateHash=null;p.PrecheckJson=null;p.ResourceRevisionsJson="[]";
    return new CommandResult<PromotionDto>(View(p,artifact.ArtifactHash,request),RevisionTag.Format(p.Revision));
   },token);
  },ct);
 }
 internal async Task<ReleaseArtifactDto> RequireAsync(ReleasePromotion p,ActorContext actor,CancellationToken ct)
 {
  var artifact=await artifacts.GetAsync(p.ArtifactId,actor,ct);var target=await scopes.EnvironmentAsync(p.TargetEnvironmentId,ct);
  if(p.GateOrigin=="PipelineRunStage"){var gate=await gates.ResolvePromotionAsync(p.Id,actor,ct);await gates.RequireStageWritableAsync(gate,actor,ct);await gates.RequirePassedPredecessorAsync(gate,p.AcceptanceId,ct);await auth.RequireAsync(actor,"pipeline.run",new("environment",p.TargetEnvironmentId,target),ct);}
  if(artifact.OrganizationId!=p.OrganizationId||artifact.ProjectId!=p.ProjectId||artifact.SourceEnvironmentId!=p.SourceEnvironmentId||artifact.SourceReleaseId!=p.SourceReleaseId||target.OrganizationId!=p.OrganizationId||target.ProjectId!=p.ProjectId)throw ScopeResolver.Missing();
  await auth.RequireAsync(actor,"release.create",new("environment",p.TargetEnvironmentId,target),ct);await auth.RequireAsync(actor,"route.write",new("environment",p.TargetEnvironmentId,target),ct);return artifact;
 }
 internal async Task ValidateAsync(ReleasePromotion p,ArtifactContent content,PromotionMappingRequest request,ActorContext actor,CancellationToken ct)
 {
  if(request.Routes.Count!=content.Routes.Count||request.Routes.Count>1000||request.Routes.Select(r=>r.ArtifactRouteKey).Distinct().Count()!=request.Routes.Count||request.Applications.Count>1000||request.Applications.Select(a=>a.ApplicationId).Distinct().Count()!=request.Applications.Count)throw Invalid();
  var scope=await scopes.EnvironmentAsync(p.TargetEnvironmentId,ct);
  foreach(var route in request.Routes){var source=content.Routes.SingleOrDefault(r=>r.Key==route.ArtifactRouteKey)??throw Invalid();if(route.TimeoutMs is <1 or >300000||!await db.Set<UpstreamCluster>().AnyAsync(c=>c.Id==route.ClusterId&&c.EnvironmentId==p.TargetEnvironmentId&&c.ProjectId==p.ProjectId&&c.Status=="Active",ct))throw Invalid();await auth.RequireAsync(actor,"cluster.read",new("cluster",route.ClusterId,scope),ct);
   if(route.TargetRouteId is Guid id)await RequireMatchingRouteAsync(id,p,source,actor,ct);
   if(route.Policies.Count!=source.Policies.Count||route.Policies.Select(x=>(x.Type,x.Priority)).Distinct().Count()!=route.Policies.Count)throw Invalid();
   if(source.Policies.Count>0)await auth.RequireAsync(actor,"policy.write",new("environment",p.TargetEnvironmentId,scope),ct);
   foreach(var template in source.Policies){var mapping=route.Policies.SingleOrDefault(x=>x.Type==template.Type&&x.Priority==template.Priority)??throw Invalid();await ResolvePolicyAsync(p,template,mapping,actor,ct);}
  }
  await credentials.ValidateAsync(p,content,request.Applications,actor,ct);
 }
 internal async Task<ApiRoute> RequireMatchingRouteAsync(Guid id,ReleasePromotion p,ArtifactRoute source,ActorContext actor,CancellationToken ct)
 {
  var row=await db.Set<ApiRoute>().SingleOrDefaultAsync(r=>r.Id==id&&r.EnvironmentId==p.TargetEnvironmentId,ct)??throw Invalid();var version=await db.Set<ApiVersion>().SingleAsync(v=>v.Id==row.ApiVersionId,ct);
  if(version.ApiId!=source.ApiId||RouteNormalizer.Normalize(row.Path)!=RouteNormalizer.Normalize(source.Path)||!row.Methods.Order(StringComparer.Ordinal).SequenceEqual(source.Methods.Order(StringComparer.Ordinal)))throw Invalid();
  await auth.RequireAsync(actor,"route.write",new("route",id,await scopes.EnvironmentAsync(p.TargetEnvironmentId,ct)),ct);return row;
 }
 internal async Task<string> ResolvePolicyAsync(ReleasePromotion p,ArtifactPolicyTemplate template,PromotionPolicyMapping mapping,ActorContext actor,CancellationToken ct)
 {
  if(template.EnvironmentFields.Count==0){if(mapping.TargetPolicyId is not null||mapping.TargetPolicyRevision is not null)throw Invalid();return ArtifactPolicyTemplates.Resolve(template,JsonSerializer.SerializeToElement(new{}));}
  var policy=await db.Set<Policy>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==mapping.TargetPolicyId,ct)??throw Invalid();if(policy.OrganizationId!=p.OrganizationId||policy.ProjectId is Guid project&&project!=p.ProjectId||policy.Type!=template.Type||!policy.Enabled||mapping.TargetPolicyRevision!=policy.VersionNo)throw Invalid();
  if(!await policyAccess.CanReadAsync(new(policy.OrganizationId,policy.ProjectId),actor,ct))throw ScopeResolver.Missing();using var doc=JsonDocument.Parse(policy.Config);var selected=doc.RootElement.EnumerateObject().Where(x=>template.EnvironmentFields.Contains(x.Name)).ToDictionary(x=>x.Name,x=>x.Value.Clone());return ArtifactPolicyTemplates.Resolve(template,JsonSerializer.SerializeToElement(selected));
 }
 internal Task LockAsync(ReleasePromotion p,CancellationToken ct)=>locks.LockAsync(p.SourceEnvironmentId,p.TargetEnvironmentId,ct);
 internal static ReleasePromotionMapping New(ReleasePromotion p,string kind,string key)=>new(){OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,PromotionId=p.Id,TargetEnvironmentId=p.TargetEnvironmentId,Kind=kind,ResourceKey=key};
 internal static string Json<T>(T value)=>System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(value));
 internal static PromotionDto View(ReleasePromotion p,string hash,PromotionMappingRequest? mapping=null,IReadOnlyList<SharedCredentialImpact>? impact=null)=>new(p.Id,p.OrganizationId,p.ProjectId,p.ArtifactId,hash,p.SourceEnvironmentId,p.TargetEnvironmentId,p.SourceReleaseId,p.TargetReleaseId,p.AcceptanceId,p.Status,p.BaselineConfigVersion,p.MappingRevision,p.CandidateHash,p.Revision,p.RequestedBy,p.CreatedAt,p.CompletedAt,mapping,impact);
 internal async Task<PromotionMappingRequest> LoadAsync(Guid id,CancellationToken ct){var rows=await db.Set<ReleasePromotionMapping>().Where(m=>m.PromotionId==id).OrderBy(m=>m.ResourceKey).ToArrayAsync(ct);return new(rows.Where(m=>m.Kind=="Route").Select(m=>Read<PromotionRouteState>(m.ParametersJson).Mapping).ToArray(),rows.Where(m=>m.Kind=="Application").Select(m=>Read<PromotionApplicationMapping>(m.ParametersJson)).ToArray());}
}

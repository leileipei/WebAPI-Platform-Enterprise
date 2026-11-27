using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Delivery;
using WebApi.Domain.Policies;
using WebApi.Domain.Routing;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Routing;
namespace WebApi.Infrastructure.Delivery;
public sealed record PreparedPromotionAccess(long Revision,string? PublicOrigin,string? InternalOrigin,string BasePath);
public sealed record PreparedPromotionCandidate(FrozenReleaseCandidate Candidate,IReadOnlyList<ResourceRevision> ResourceRevisions,string CandidateHash,long MappingRevision,long BaselineConfigVersion,PreparedPromotionAccess Access,IReadOnlyList<SharedCredentialImpact> CredentialImpact,string SnapshotSchemaVersion);
public sealed class PromotionCandidateBuilder(WebApiDbContext db,PromotionMappingService mapping,ReleaseArtifactService artifacts,TestAcceptanceService acceptances,PromotionCredentialSelection credentials,ReleaseCandidateBuilder builder,SnapshotCompiler compiler,HistoricalSnapshotService history)
{
 public async Task<PreparedPromotionCandidate> PrepareAsync(ReleasePromotion promotion,ActorContext actor,CancellationToken ct)
 {
  if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Promotion preparation requires an owning governance and environment-lock transaction.");
  if(promotion.Status!="Draft")throw PromotionMappingService.NotDraft();var artifact=await mapping.RequireAsync(promotion,actor,ct);if(promotion.AcceptanceId is not Guid accepted)throw new ApiException(409,"test_acceptance_required","晋级需有效独立测试验收。");await acceptances.RequireAcceptedCurrentAsync(accepted,promotion.ArtifactId,actor,ct);
  var source=await artifacts.RequireSourceAsync(promotion.SourceReleaseId,actor,ct);if(ReleaseArtifactCanonicalizer.Hash(source.Content)!=artifact.ArtifactHash)throw new ApiException(409,"artifact_source_changed","来源制品已与实际部署内容不符。");
  var env=await db.Set<EnvironmentRecord>().SingleAsync(e=>e.Id==promotion.TargetEnvironmentId,ct);if(env.ProjectId!=promotion.ProjectId||(!env.IsProduction&&promotion.GateOrigin!="PipelineRunStage")||env.Status!="Active"||(env.DesiredConfigVersion??0)!=promotion.BaselineConfigVersion)throw new ApiException(409,"stale_baseline","目标生产环境或基线已变化。");
  var request=await mapping.LoadAsync(promotion.Id,ct);await mapping.ValidateAsync(promotion,artifact.Content,request,actor,ct);
  var rows=await db.Set<ReleasePromotionMapping>().Where(m=>m.PromotionId==promotion.Id&&m.Kind=="Route").ToArrayAsync(ct);var routeIds=new List<Guid>();var sourcePolicyRevisions=new List<ResourceRevision>();
  foreach(var row in rows){var state=PromotionMappingService.Read<PromotionRouteState>(row.ParametersJson);var target=state.Mapping;var frozen=artifact.Content.Routes.Single(r=>r.Key==target.ArtifactRouteKey);var id=target.TargetRouteId??state.PreparedRouteId;
   var route=id is Guid existing?await mapping.RequireMatchingRouteAsync(existing,promotion,frozen,actor,ct):new ApiRoute{EnvironmentId=env.Id};var normalized=RouteNormalizer.Normalize(frozen.Path);RouteNormalizer.MatchOrder(frozen.Path,frozen.Priority);
   if(frozen.Enabled&&await db.Set<RouteMethod>().AnyAsync(m=>m.EnvironmentId==env.Id&&m.NormalizedPath==normalized&&frozen.Methods.Contains(m.Method)&&m.RouteId!=route.Id,ct))throw new ApiException(409,"route_conflict","目标存在同形方法路径的启用路由。");
   var methods=frozen.Methods.Order(StringComparer.Ordinal).ToArray();
   var (privateIds,bindingsChanged)=await PreparePoliciesAsync(promotion,row,state,route.Id,frozen,sourcePolicyRevisions,actor,ct);
   var changed=bindingsChanged||route.NormalizedPath!=normalized||!route.Methods.SequenceEqual(methods)||route.ApiVersionId!=frozen.VersionId||route.Path!=frozen.Path||route.Priority!=frozen.Priority||route.ClusterId!=target.ClusterId||route.TimeoutMs!=target.TimeoutMs||route.Enabled!=frozen.Enabled;
   if(id is null){route.RouteName="Promotion-"+frozen.Key[..12];db.Add(route);}else if(changed)route.Revision++;
   route.ApiVersionId=frozen.VersionId;route.Path=frozen.Path;route.NormalizedPath=normalized;route.Methods=methods;route.ClusterId=target.ClusterId;route.Priority=frozen.Priority;route.TimeoutMs=target.TimeoutMs;route.Enabled=frozen.Enabled;if(changed)route.UpdatedAt=DateTimeOffset.UtcNow;
   await RouteService.ReplaceMethodsAsync(db,route,ct);
   // The deferred governance trigger reconciles route authentication_mode with
   // the effective binding mode at commit; apply the same rule inside the
   // transaction so the precheck candidate matches the committed row and the
   // deferred trigger becomes a no-op instead of re-stamping the revision.
   await db.SaveChangesAsync(ct);var healMode=await db.Database.SqlQueryRaw<string>("SELECT route_effective_auth_mode({0}::uuid) AS \"Value\"",route.Id).SingleAsync(ct);
   if(route.AuthenticationMode!=healMode){route.AuthenticationMode=healMode;route.Revision++;}
   row.TargetRouteId=route.Id;row.ParametersJson=PromotionMappingService.Json(new PromotionRouteState(target,route.Id,privateIds,promotion.MappingRevision));routeIds.Add(route.Id);
  }
  await db.SaveChangesAsync(ct);
  var selection=await credentials.SelectAsync(promotion,artifact.Content,request.Applications,routeIds,actor,ct);
  var candidate=await builder.BuildPromotionAsync(env.Id,new(promotion.BaselineConfigVersion,artifact.Content.Apis.Select(a=>a.VersionId).ToArray()),routeIds,selection,ct);
  if(candidate.Versions.Any(v=>v.Api.LifecycleStatus=="Retired"||v.Version.Status=="Retired"))throw new ApiException(422,"retired_artifact_api","制品引用的API或版本已停用，不能从晋级目标中隐含删除。");
  foreach(var route in candidate.Routes){var frozen=artifact.Content.Routes.Single(r=>r.VersionId==route.ApiVersionId&&r.Key==rows.Single(row=>row.TargetRouteId==route.Id).ResourceKey);var mode=route.EffectiveAuthenticationMode??(route.RequireApiKey?"ApiKey":"Anonymous");if(mode!=frozen.AuthenticationMode||route.Path!=frozen.Path||!route.Methods.SequenceEqual(frozen.Methods.Order(StringComparer.Ordinal)))throw PromotionMappingService.Invalid();}
  var revisions=candidate.ResourceRevisions.Concat(sourcePolicyRevisions).DistinctBy(r=>(r.Type,r.Id)).OrderBy(r=>r.Type,StringComparer.Ordinal).ThenBy(r=>r.Id).ToArray();candidate=candidate with{ResourceRevisions=revisions};
  var baseline=promotion.BaselineConfigVersion==0?new RuntimeSnapshot("2.0",env.Id,0,DateTimeOffset.UtcNow,[],[],[],[]):(await history.ReadAsync(env.Id,promotion.BaselineConfigVersion,ct)).Snapshot;
  var compiled=compiler.Compile(candidate,baseline,promotion.BaselineConfigVersion+1,DateTimeOffset.UtcNow);
  using var payload=JsonDocument.Parse(compiled.Payload);
  var snapshotSchemaVersion=payload.RootElement.GetProperty("schemaVersion").GetString()!;
  var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(candidate)));promotion.CandidateHash=hash;promotion.ResourceRevisionsJson=PromotionMappingService.Json(revisions);promotion.TargetAccessAddressRevision=env.AccessAddressRevision;
  db.Add(new ReleasePromotionEvent{OrganizationId=promotion.OrganizationId,ProjectId=promotion.ProjectId,EnvironmentId=env.Id,PromotionId=promotion.Id,Phase="Mapping",FromStatus=promotion.Status,ToStatus=promotion.Status,ReasonCode="target_candidate_prepared",ActorId=actor.UserId});
  return new(candidate,revisions,hash,promotion.MappingRevision,promotion.BaselineConfigVersion,new(env.AccessAddressRevision,env.GatewayPublicUrl,env.GatewayInternalUrl,env.BasePath),selection.Impact,snapshotSchemaVersion);
 }

 private async Task<(List<Guid> PolicyIds,bool Changed)> PreparePoliciesAsync(ReleasePromotion promotion,ReleasePromotionMapping row,
  PromotionRouteState state,Guid routeId,ArtifactRoute frozen,List<ResourceRevision> sourceRevisions,ActorContext actor,CancellationToken ct)
 {
  var bound=await (from binding in db.Set<RoutePolicyBinding>() join policy in db.Set<Policy>() on binding.PolicyId equals policy.Id
   where binding.RouteId==routeId select new {Binding=binding,Policy=policy}).ToArrayAsync(ct);
  var boundIds=bound.Select(b=>b.Policy.Id).ToArray();
  var shared=await db.Set<RoutePolicyBinding>().Where(b=>b.RouteId!=routeId&&boundIds.Contains(b.PolicyId)).Select(b=>b.PolicyId).ToHashSetAsync(ct);
  var recorded=state.PrivatePolicyIds;
  var prepared=recorded is not null && state.PreparedRevision>=row.Revision && state.PreparedRevision<=promotion.MappingRevision;
  if(prepared && (state.PreparedRouteId!=routeId || recorded!.Count!=frozen.Policies.Count || bound.Length!=recorded.Count))
   throw new ApiException(409,"prepared_policy_binding_changed","晋级独立策略绑定已改变，需重新编辑映射。");

  var privateIds=new List<Guid>();
  var prefix=$"Promotion-{promotion.Id:N}-";
  var selectedIds=state.Mapping.Policies.Where(p=>p.TargetPolicyId.HasValue).Select(p=>p.TargetPolicyId!.Value).ToHashSet();
  var changed=false;
  for(var i=0;i<frozen.Policies.Count;i++)
  {
   var template=frozen.Policies[i];
   var selected=state.Mapping.Policies.Single(p=>p.Type==template.Type && p.Priority==template.Priority);
   var config=await mapping.ResolvePolicyAsync(promotion,template,selected,actor,ct);
   if(selected.TargetPolicyId is Guid sourceId) sourceRevisions.Add(new("policy",sourceId,selected.TargetPolicyRevision!.Value));
   var slot=bound.Where(b=>b.Binding.Priority==template.Priority);
   if(recorded is not null)
    slot=slot.Where(b=>state.PreparedRouteId==routeId && i<recorded.Count && b.Policy.Id==recorded[i]);
   else
    // Older saves retained only the prepared route. Reuse a matching copy only on its original binding.
    slot=slot.Where(b=>state.PreparedRouteId==routeId && b.Policy.Type==template.Type && b.Policy.Name.StartsWith(prefix,StringComparison.Ordinal) &&
     Guid.TryParseExact(b.Policy.Name.AsSpan(prefix.Length),"N",out _) && b.Policy.VersionNo==1);
   var matches=slot.ToArray();
   if(prepared && matches.Length!=1) throw new ApiException(409,"prepared_policy_binding_changed","晋级独立策略绑定已改变，需重新编辑映射。");
   var existing=matches.Length==1?matches[0].Policy:null;
   if(existing is not null && existing.OrganizationId==promotion.OrganizationId && existing.ProjectId==promotion.ProjectId &&
    existing.Enabled && existing.Type==template.Type && !shared.Contains(existing.Id) && !selectedIds.Contains(existing.Id) &&
    PolicyConfigurationValidator.Normalize(existing.Type,existing.Config)==config)
   {
    privateIds.Add(existing.Id);
    continue;
   }
   if(prepared) throw new ApiException(409,"prepared_policy_changed","晋级独立策略已被更改或共享，需重新编辑映射。");
   var copy=new Policy{OrganizationId=promotion.OrganizationId,ProjectId=promotion.ProjectId,Name=prefix+Guid.NewGuid().ToString("N"),Type=template.Type,Config=config};
   db.Add(copy);
   db.Add(new RoutePolicyBinding{RouteId=routeId,PolicyId=copy.Id,Priority=template.Priority});
   privateIds.Add(copy.Id);
   changed=true;
  }
  var keep=privateIds.ToHashSet();
  var removed=bound.Where(b=>!keep.Contains(b.Policy.Id)).Select(b=>b.Binding).ToArray();
  db.RemoveRange(removed);
  return (privateIds,changed||removed.Length>0);
 }

}

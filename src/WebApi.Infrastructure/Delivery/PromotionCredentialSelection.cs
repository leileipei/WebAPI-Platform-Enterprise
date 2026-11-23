using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Releases;
namespace WebApi.Infrastructure.Delivery;
public sealed record PromotionCredentialResult(IReadOnlyList<FrozenApplication> Applications,IReadOnlyList<ResourceRevision> ResourceRevisions,IReadOnlyList<SharedCredentialImpact> Impact);
public sealed partial class PromotionCredentialSelection(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,
 HistoricalSnapshotService history,PublishSettings settings)
{
 public async Task ValidateAsync(ReleasePromotion p,ArtifactContent content,IReadOnlyList<PromotionApplicationMapping> selections,ActorContext actor,CancellationToken ct)
 {
  var selectedApis=content.Apis.Select(a=>a.ApiId).ToHashSet();var covered=new HashSet<Guid>();var apiKeyCovered=new HashSet<Guid>();var scope=await scopes.EnvironmentAsync(p.TargetEnvironmentId,ct);var now=DateTimeOffset.UtcNow;
  var governed=settings.EnablesGovernanceSnapshot24(p.TargetEnvironmentId);
  foreach(var selection in selections){if(selection.CredentialIds.Count>1000||selection.AuthorizationIds.Count is <1 or >1000||selection.CredentialIds.Distinct().Count()!=selection.CredentialIds.Count||selection.AuthorizationIds.Distinct().Count()!=selection.AuthorizationIds.Count)throw PromotionMappingService.Invalid();
   var app=await db.Set<ApplicationRecord>().AsNoTracking().SingleOrDefaultAsync(a=>a.Id==selection.ApplicationId,ct)??throw PromotionMappingService.Invalid();if(app.OrganizationId!=p.OrganizationId||app.ProjectId is Guid project&&project!=p.ProjectId||app.Status!="Active")throw PromotionMappingService.Invalid();await auth.RequireAsync(actor,"app.read",new("application",app.Id,scope),ct);
   var credentials=await db.Set<ApplicationCredential>().AsNoTracking().Where(c=>selection.CredentialIds.Contains(c.Id)&&c.ApplicationId==app.Id).ToArrayAsync(ct);if(credentials.Length!=selection.CredentialIds.Count||credentials.Any(c=>c.Status!="Active"||c.ValidFrom>now||c.ExpiresAt<=now))throw PromotionMappingService.Invalid();
   if(governed&&(credentials.Any(c=>c.BindingMode!="EnvironmentBound"||c.EnvironmentId!=p.TargetEnvironmentId||c.OrganizationId!=p.OrganizationId||c.ProjectId!=p.ProjectId)||
      await db.Set<CredentialRevocation>().AnyAsync(r=>r.EnvironmentId==p.TargetEnvironmentId&&selection.CredentialIds.Contains(r.CredentialId),ct)))throw PromotionMappingService.Invalid();
   var grants=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(g=>selection.AuthorizationIds.Contains(g.Id)&&g.ApplicationId==app.Id&&g.EnvironmentId==p.TargetEnvironmentId).ToArrayAsync(ct);if(grants.Length!=selection.AuthorizationIds.Count||grants.Any(g=>!selectedApis.Contains(g.ApiId)||g.ValidFrom>now||g.ExpiresAt<=now||governed&&g.Mode!="RouteMethod"))throw PromotionMappingService.Invalid();foreach(var grant in grants){covered.Add(grant.ApiId);if(credentials.Length>0)apiKeyCovered.Add(grant.ApiId);}
  }
  if(content.Routes.Any(r=>r.AuthenticationMode=="ApiKey"?!apiKeyCovered.Contains(r.ApiId):r.AuthenticationMode!="Anonymous"&&!covered.Contains(r.ApiId)))throw PromotionMappingService.Invalid();
 }
 public async Task<PromotionCredentialResult> SelectAsync(ReleasePromotion p,ArtifactContent content,IReadOnlyList<PromotionApplicationMapping> selections,IReadOnlyList<Guid> routeIds,ActorContext actor,CancellationToken ct)
 {
  await ValidateAsync(p,content,selections,actor,ct);
  var baseline = p.BaselineConfigVersion > 0 ? (await history.ReadAsync(p.TargetEnvironmentId,p.BaselineConfigVersion,ct)).Snapshot : null;
  var mappedRoutes = routeIds.ToHashSet();
  var retainedRoutes = baseline?.Routes.Where(route => !mappedRoutes.Contains(route.Id)).ToArray() ?? [];
  var access = baseline is null ? null : new RetainedRouteAccess(baseline, retainedRoutes,content.Apis.Select(api => api.ApiId).ToHashSet());
  var governed = settings.EnablesGovernanceSnapshot24(p.TargetEnvironmentId);
  var result = governed
      ? await SelectGovernedAsync(p,selections,actor,baseline,retainedRoutes,access,ct)
      : await SelectLegacyAsync(p,selections,actor,baseline,access,ct);

  // A baseline grant shared with a protected artifact API must not implicitly authorize its mapped routes.
  var protectedApis = content.Routes.Where(route => route.AuthenticationMode != "Anonymous").Select(route => route.ApiId).ToHashSet();
  var explicitGrants = selections.SelectMany(selection => selection.AuthorizationIds).ToHashSet();
  var evaluatedAt = DateTimeOffset.UtcNow;
  // Expired legacy grants stay in the retained snapshot. Only windows that can still open need the overlap guard.
  var implicitGrants = result.Applications.SelectMany(app => app.Permissions)
      .Where(grant => protectedApis.Contains(grant.ApiId) && !explicitGrants.Contains(grant.Id) &&
          (governed || grant.ExpiresAt is null || grant.ExpiresAt > evaluatedAt)).ToArray();
  var implicitIds = implicitGrants.Select(grant => grant.Id).ToArray();
  if (implicitIds.Length > 0 && (!governed ||
      await db.Set<ApplicationApiPermission>().AnyAsync(grant => implicitIds.Contains(grant.Id) && grant.Mode != "RouteMethod",ct) ||
      await db.Set<ApplicationRouteMethodGrant>().AnyAsync(grant => implicitIds.Contains(grant.PermissionId) && routeIds.Contains(grant.RouteId),ct)))
      throw RetainedRouteAccess.Changed();
  return result;
 }

 private async Task<PromotionCredentialResult> SelectLegacyAsync(ReleasePromotion p,IReadOnlyList<PromotionApplicationMapping> selections,ActorContext actor,
     RuntimeSnapshot? baseline,RetainedRouteAccess? access,CancellationToken ct)
 {
  var scope=await scopes.EnvironmentAsync(p.TargetEnvironmentId,ct);var retained=new Dictionary<Guid,FrozenApplication>();var affectedApis=new Dictionary<Guid,Guid[]>();
  if(baseline is not null){var historical=await history.CandidateAsync(p.TargetEnvironmentId,p.BaselineConfigVersion,p.BaselineConfigVersion,ct);
   foreach(var runtime in baseline.Applications){var affected=access!.ApiIdsFor(runtime);if(affected.Length==0)continue;affectedApis.Add(runtime.Id,affected);var permissions=access.PermissionsFor(runtime).ToArray();var frozen=historical.Applications.SingleOrDefault(a=>a.Application.Id==runtime.Id)??throw BaselineChanged();
    var keepKeys=access.NeedsCredentials(runtime);
    if(frozen.Application.Status!=runtime.Status||keepKeys&&(frozen.Credentials.Count!=runtime.Credentials.Count||runtime.Credentials.Any(c=>!frozen.Credentials.Any(f=>Same(f,c)))))throw BaselineChanged();var grants=frozen.Permissions.Where(g=>permissions.Any(r=>r.ApiId==g.ApiId&&r.ValidFrom==g.ValidFrom&&r.ExpiresAt==g.ExpiresAt)).ToArray();if(grants.Length!=permissions.Length)throw BaselineChanged();
    await auth.RequireAsync(actor,"app.read",new("application",runtime.Id,scope),ct);retained.Add(runtime.Id,frozen with{Credentials=keepKeys?frozen.Credentials:[],Permissions=grants});
   }
  }
  var applications=new List<FrozenApplication>();var revisions=new List<ResourceRevision>();var impact=new List<SharedCredentialImpact>();
  foreach(var selection in selections){var app=await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync(a=>a.Id==selection.ApplicationId,ct);var creds=await db.Set<ApplicationCredential>().AsNoTracking().Where(c=>selection.CredentialIds.Contains(c.Id)).OrderBy(c=>c.Id).ToArrayAsync(ct);var grants=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(g=>selection.AuthorizationIds.Contains(g.Id)).OrderBy(g=>g.Id).ToArrayAsync(ct);var frozen=new FrozenApplication(ApplicationService.Dto(app),creds.Select(c=>new FrozenCredential(c.Id,c.AccessKey,c.SecretHash,c.SecretLast4,c.Status,c.ValidFrom,c.ExpiresAt,c.Revision)).ToArray(),grants.Select(ApplicationService.Dto).ToArray());
   if(retained.Remove(app.Id,out var old)){var required=old.Credentials.Select(c=>c.Id).Order().ToArray();if(required.Except(selection.CredentialIds).Any()||old.Application.Status!=frozen.Application.Status||old.Credentials.Any(c=>!frozen.Credentials.Any(n=>Same(c,n))))throw BaselineChanged();if(!selection.ConfirmSharedCredentialImpact)throw new ApiException(409,"shared_credential_confirmation_required","共享应用的凭证影响须明确确认，并随目标候选提交生产审批。");impact.Add(new(app.Id,affectedApis[app.Id],required,true));frozen=frozen with{Permissions=frozen.Permissions.Concat(old.Permissions).DistinctBy(g=>g.Id).OrderBy(g=>g.Id).ToArray()};}
   var shared=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(g=>g.ApplicationId==app.Id&&g.EnvironmentId!=p.TargetEnvironmentId).ToArrayAsync(ct);
   if(shared.Length>0){if(!selection.ConfirmSharedCredentialImpact)throw new ApiException(409,"shared_credential_confirmation_required","该应用跨环境共用，须确认凭证影响并提交目标审批。");var existing=impact.FindIndex(x=>x.ApplicationId==app.Id);if(existing>=0)impact[existing]=impact[existing] with{SharedAcrossEnvironments=true};else impact.Add(new(app.Id,[],[],true,true));revisions.AddRange(shared.Select(g=>new ResourceRevision("authorization",g.Id,g.Revision)));}
   applications.Add(frozen);revisions.Add(new("application",app.Id,app.Revision));revisions.AddRange(creds.Select(c=>new ResourceRevision("credential",c.Id,c.Revision)));revisions.AddRange(grants.Select(g=>new ResourceRevision("authorization",g.Id,g.Revision)));
  }
  applications.AddRange(retained.Values);foreach(var app in applications){if(!revisions.Any(r=>r.Type=="application"&&r.Id==app.Application.Id)){var current=await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync(a=>a.Id==app.Application.Id,ct);revisions.Add(new("application",current.Id,current.Revision));}foreach(var c in app.Credentials)if(!revisions.Any(r=>r.Type=="credential"&&r.Id==c.Id)){var revision=await db.Set<ApplicationCredential>().AsNoTracking().Where(k=>k.Id==c.Id).Select(k=>k.Revision).SingleAsync(ct);revisions.Add(new("credential",c.Id,revision));}foreach(var g in app.Permissions)if(!revisions.Any(r=>r.Type=="authorization"&&r.Id==g.Id)){var revision=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(k=>k.Id==g.Id).Select(k=>k.Revision).SingleAsync(ct);revisions.Add(new("authorization",g.Id,revision));}}
  return new(applications.OrderBy(a=>a.Application.Id).ToArray(),revisions,impact);
 }
 private static bool Same(FrozenCredential f,RuntimeCredential c)=>f.Id==c.Id&&f.AccessKey==c.AccessKey&&f.SecretHash==c.Hash&&f.Status==c.Status&&f.ValidFrom==c.ValidFrom&&f.ExpiresAt==c.ExpiresAt;
 private static bool Same(FrozenCredential f,FrozenCredential c)=>f.Id==c.Id&&f.AccessKey==c.AccessKey&&f.SecretHash==c.SecretHash&&f.Status==c.Status&&f.ValidFrom==c.ValidFrom&&f.ExpiresAt==c.ExpiresAt;
 private static ApiException BaselineChanged()=>new(409,"shared_credentials_changed","保留业务的现有运行凭证不可在本次晋级中隐含删除或改变，请单独评审该变更。");
}

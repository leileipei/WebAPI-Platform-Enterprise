using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
public sealed class PromotionOptionsService(WebApiDbContext db,PromotionReadService reads,ReleaseArtifactService artifacts,ScopeResolver scopes,AuthorizationService auth,HistoricalSnapshotService history)
{
 public async Task<PromotionMappingOptionsDto> ReadAsync(Guid id,ActorContext actor,CancellationToken ct)
 {
  using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(10));var token=budget.Token;
  try{
   var promotion=await reads.ReadAsync(id,actor,token);var artifact=await artifacts.GetAsync(promotion.ArtifactId,actor,token);var scope=await scopes.EnvironmentAsync(promotion.TargetEnvironmentId,token);var apiIds=artifact.Content.Apis.Select(a=>a.ApiId).ToArray();var now=DateTimeOffset.UtcNow;var truncated=false;
   PromotionClusterChoice[]? clusters=null;PromotionRouteChoice[]? routes=null;PromotionPolicyChoice[]? policies=null;PromotionApplicationChoice[]? applications=null;
   if(await auth.CanAsync(actor,"cluster.read",new("environment",promotion.TargetEnvironmentId,scope),token)){var rows=await db.Set<UpstreamCluster>().AsNoTracking().Where(c=>c.EnvironmentId==promotion.TargetEnvironmentId&&c.Status=="Active").OrderBy(c=>c.Name).ThenBy(c=>c.Id).Take(1001).Select(c=>new PromotionClusterChoice(c.Id,c.Name)).ToArrayAsync(token);truncated|=rows.Length>1000;clusters=rows.Take(1000).ToArray();}
   if(await auth.CanAsync(actor,"route.read",new("environment",promotion.TargetEnvironmentId,scope),token)){var rows=await (from r in db.Set<ApiRoute>() join v in db.Set<ApiVersion>() on r.ApiVersionId equals v.Id where r.EnvironmentId==promotion.TargetEnvironmentId&&apiIds.Contains(v.ApiId) orderby r.Path,r.Id select new PromotionRouteChoice(r.Id,v.ApiId,r.Path,r.Methods,r.Revision)).Take(1001).ToArrayAsync(token);truncated|=rows.Length>1000;routes=rows.Take(1000).ToArray();}
   if(await auth.CanAsync(actor,"policy.read",new("environment",promotion.TargetEnvironmentId,scope),token)){var rows=await db.Set<Policy>().AsNoTracking().Where(p=>p.OrganizationId==scope.OrganizationId&&(p.ProjectId==null||p.ProjectId==scope.ProjectId)&&p.Enabled).OrderBy(p=>p.Name).ThenBy(p=>p.Id).Take(1001).Select(p=>new PromotionPolicyChoice(p.Id,p.Name,p.Type,p.VersionNo)).ToArrayAsync(token);truncated|=rows.Length>1000;policies=rows.Take(1000).ToArray();}
   if(await auth.CanAsync(actor,"app.read",new("environment",promotion.TargetEnvironmentId,scope),token)){
    var rows=await db.Set<ApplicationRecord>().AsNoTracking().Where(a=>a.OrganizationId==scope.OrganizationId&&(a.ProjectId==null||a.ProjectId==scope.ProjectId)&&a.Status=="Active"&&db.Set<ApplicationApiPermission>().Any(g=>g.ApplicationId==a.Id&&g.EnvironmentId==promotion.TargetEnvironmentId&&apiIds.Contains(g.ApiId)&&g.ValidFrom<=now&&(g.ExpiresAt==null||g.ExpiresAt>now))).OrderBy(a=>a.Name).ThenBy(a=>a.Id).Take(1001).ToArrayAsync(token);truncated|=rows.Length>1000;var ids=rows.Take(1000).Select(a=>a.Id).ToArray();
    var keys=await db.Set<ApplicationCredential>().AsNoTracking().Where(k=>ids.Contains(k.ApplicationId)&&k.Status=="Active"&&k.ValidFrom<=now&&k.ExpiresAt>now).OrderBy(k=>k.ApplicationId).ThenBy(k=>k.Id).Select(k=>new{k.ApplicationId,Value=new PromotionCredentialChoice(k.Id,k.SecretLast4,k.ExpiresAt)}).Take(1001).ToArrayAsync(token);truncated|=keys.Length>1000;keys=keys.Take(1000).ToArray();
    var grants=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(g=>ids.Contains(g.ApplicationId)&&g.EnvironmentId==promotion.TargetEnvironmentId&&apiIds.Contains(g.ApiId)&&g.ValidFrom<=now&&(g.ExpiresAt==null||g.ExpiresAt>now)).OrderBy(g=>g.ApplicationId).ThenBy(g=>g.Id).Select(g=>new{g.ApplicationId,Value=new PromotionAuthorizationChoice(g.Id,g.ApiId)}).Take(1001).ToArrayAsync(token);truncated|=grants.Length>1000;grants=grants.Take(1000).ToArray();
    // Only a sharing boolean crosses this boundary. Other environment IDs and grants stay private.
    var shared=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(g=>ids.Contains(g.ApplicationId)&&g.EnvironmentId!=promotion.TargetEnvironmentId).Select(g=>g.ApplicationId).Distinct().ToArrayAsync(token);
    WebApi.Contracts.Runtime.RuntimeSnapshot? baseline=promotion.BaselineConfigVersion>0?(await history.ReadAsync(promotion.TargetEnvironmentId,promotion.BaselineConfigVersion,token)).Snapshot:null;var retainedApiIds=baseline?.Routes.Where(r=>!apiIds.Contains(r.ApiId)).Select(r=>r.ApiId).ToHashSet()??[];
    applications=rows.Take(1000).Select(a=>{var old=baseline?.Applications.SingleOrDefault(r=>r.Id==a.Id);var retained=old?.Permissions.Where(g=>retainedApiIds.Contains(g.ApiId)).Select(g=>g.ApiId).Distinct().ToArray()??[];var jwtRetained=baseline?.Policies.Any(p=>p.Type=="authentication"&&baseline.Routes.Any(r=>retainedApiIds.Contains(r.ApiId)&&(r.PolicyBindings??[]).Any(b=>b.PolicyId==p.Id))&&(WebApi.Domain.Policies.PolicyConfigurationValidator.ParseAuthentication(p.Config).Jwt?.ApplicationMappings??[]).Any(m=>m.ApplicationId==a.Id))==true;return new PromotionApplicationChoice(a.Id,a.Name,keys.Where(k=>k.ApplicationId==a.Id).Select(k=>k.Value).OrderBy(k=>k.Id).ToArray(),grants.Where(g=>g.ApplicationId==a.Id).Select(g=>g.Value).OrderBy(g=>g.Id).ToArray(),shared.Contains(a.Id),retained.Length>0||jwtRetained?old?.Credentials.Select(k=>k.Id).Order().ToArray()??[]:[],retained);}).ToArray();
   }
   return new(promotion.TargetEnvironmentId,clusters,routes,policies,applications,truncated);
  }catch(OperationCanceledException)when(!ct.IsCancellationRequested&&budget.IsCancellationRequested){throw new ApiException(503,"delivery_query_timeout","映射选项查询超时，请稍后重试。");}
 }
}

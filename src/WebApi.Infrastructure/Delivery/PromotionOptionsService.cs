using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.Routing;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
public sealed class PromotionOptionsService(WebApiDbContext db,PromotionReadService reads,ReleaseArtifactService artifacts,ScopeResolver scopes,AuthorizationService auth,HistoricalSnapshotService history,PublishSettings settings)
{
 public async Task<PromotionMappingOptionsDto> ReadAsync(Guid id,ActorContext actor,CancellationToken ct,int page=1,int pageSize=1000,string? search=null)
 {
    if(page is <1 or >1000000 || pageSize is <1 or >1000 || search?.Length>200)
        throw new ApiException(422,"invalid_directory_query","页码、每页数量或搜索条件超出范围。");
    var term=search?.Trim().ToLowerInvariant()??string.Empty;
    var byId=Guid.TryParse(term,out var parsed)?parsed:(Guid?)null;
    using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);
    budget.CancelAfter(TimeSpan.FromSeconds(10));
    var token=budget.Token;
    try{
        var promotion=await reads.ReadAsync(id,actor,token);
        var artifact=await artifacts.GetAsync(promotion.ArtifactId,actor,token);
        var env=promotion.TargetEnvironmentId;
        var scope=await scopes.EnvironmentAsync(env,token);
        var apiIds=artifact.Content.Apis.Select(a=>a.ApiId).ToArray();
        var now=DateTimeOffset.UtcNow;
        var governed=settings.EnablesGovernanceSnapshot24(env);
        var savedRoutes=promotion.Mapping?.Routes??[];
        var savedApps=promotion.Mapping?.Applications??[];
        var truncated=false;

        // Page each authorized resource independently. Keep saved selections available when reopening a later page.
        async Task<T[]> Rows<T>(IQueryable<T> ordered,IQueryable<T>? pinned,Func<T,Guid> key)
        {
            var rows=await ordered.Skip((page-1)*pageSize).Take(pageSize+1).ToArrayAsync(token);
            truncated|=rows.Length>pageSize;
            var selected=pinned is null?Array.Empty<T>():await pinned.ToArrayAsync(token);
            return rows.Take(pageSize).Concat(selected).DistinctBy(key).ToArray();
        }

        PromotionClusterChoice[]? clusters=null;
        PromotionRouteChoice[]? routes=null;
        PromotionPolicyChoice[]? policies=null;
        PromotionApplicationChoice[]? applications=null;
        if(await auth.CanAsync(actor,"cluster.read",new("environment",env,scope),token)){
            var available=db.Set<UpstreamCluster>().AsNoTracking().Where(c=>c.EnvironmentId==env&&c.Status=="Active");
            var pinned=savedRoutes.Select(r=>r.ClusterId).Distinct().ToArray();
            var matches=available.Where(c=>term==""||c.Name.ToLower().Contains(term)||c.Id==byId);
            clusters=await Rows(matches.OrderBy(c=>c.Name).ThenBy(c=>c.Id).Select(c=>new PromotionClusterChoice(c.Id,c.Name)),
                pinned.Length==0?null:available.Where(c=>pinned.Contains(c.Id)).Select(c=>new PromotionClusterChoice(c.Id,c.Name)),c=>c.Id);
        }
        if(await auth.CanAsync(actor,"route.read",new("environment",env,scope),token)){
            var available=from r in db.Set<ApiRoute>().AsNoTracking() join v in db.Set<ApiVersion>() on r.ApiVersionId equals v.Id
                where r.EnvironmentId==env&&apiIds.Contains(v.ApiId) select new {r.Id,v.ApiId,r.Path,r.Methods,r.Revision,r.ApiVersionId,r.RouteName};
            var pinned=savedRoutes.Where(r=>r.TargetRouteId.HasValue).Select(r=>r.TargetRouteId!.Value).ToArray();
            var matches=available.Where(r=>term==""||r.Path.ToLower().Contains(term)||r.RouteName.ToLower().Contains(term)||r.Id==byId||r.ApiId==byId);
            routes=await Rows(matches.OrderBy(r=>r.Path).ThenBy(r=>r.Id).Select(r=>new PromotionRouteChoice(r.Id,r.ApiId,r.Path,r.Methods,r.Revision,governed?r.ApiVersionId:null)),
                pinned.Length==0?null:available.Where(r=>pinned.Contains(r.Id)).Select(r=>new PromotionRouteChoice(r.Id,r.ApiId,r.Path,r.Methods,r.Revision,governed?r.ApiVersionId:null)),r=>r.Id);
        }
        if(await auth.CanAsync(actor,"policy.read",new("environment",env,scope),token)){
            var available=db.Set<Policy>().AsNoTracking().Where(p=>p.OrganizationId==scope.OrganizationId&&(p.ProjectId==null||p.ProjectId==scope.ProjectId)&&p.Enabled);
            var pinned=savedRoutes.SelectMany(r=>r.Policies).Where(p=>p.TargetPolicyId.HasValue).Select(p=>p.TargetPolicyId!.Value).ToArray();
            var matches=available.Where(p=>term==""||p.Name.ToLower().Contains(term)||p.Type.ToLower().Contains(term)||p.Id==byId);
            policies=await Rows(matches.OrderBy(p=>p.Name).ThenBy(p=>p.Id).Select(p=>new PromotionPolicyChoice(p.Id,p.Name,p.Type,p.VersionNo)),
                pinned.Length==0?null:available.Where(p=>pinned.Contains(p.Id)).Select(p=>new PromotionPolicyChoice(p.Id,p.Name,p.Type,p.VersionNo)),p=>p.Id);
        }
        if(await auth.CanAsync(actor,"app.read",new("environment",env,scope),token)){
            var eligibleGrants=db.Set<ApplicationApiPermission>().AsNoTracking().Where(g=>g.EnvironmentId==env&&apiIds.Contains(g.ApiId)&&
                g.ValidFrom<=now&&(g.ExpiresAt==null||g.ExpiresAt>now));
            var eligible=db.Set<ApplicationRecord>().AsNoTracking().Where(a=>a.OrganizationId==scope.OrganizationId&&(a.ProjectId==null||a.ProjectId==scope.ProjectId)&&
                a.Status=="Active"&&eligibleGrants.Any(g=>g.ApplicationId==a.Id));
            var appMatches=eligible.Where(a=>term==""||a.Id==byId||a.Name.ToLower().Contains(term)||a.Code.ToLower().Contains(term));
            var eligibleKeys=from a in eligible join k in db.Set<ApplicationCredential>() on a.Id equals k.ApplicationId
                where k.Status=="Active"&&k.ValidFrom<=now&&k.ExpiresAt>now&&(!governed||k.BindingMode=="EnvironmentBound"&&k.EnvironmentId==env&&
                    k.ProjectId==scope.ProjectId&&k.OrganizationId==scope.OrganizationId&&!db.Set<CredentialRevocation>().Any(r=>r.EnvironmentId==env&&r.CredentialId==k.Id))
                select new{k.Id,k.ApplicationId,k.SecretLast4,k.ExpiresAt};
            var keyIds=savedApps.SelectMany(a=>a.CredentialIds).Distinct().ToArray();
            var keyMatches=eligibleKeys.Where(k=>term==""||k.Id==byId||k.SecretLast4.ToLower().Contains(term)||appMatches.Any(a=>a.Id==k.ApplicationId));
            var keys=await Rows(keyMatches.OrderBy(k=>k.ApplicationId).ThenBy(k=>k.Id),keyIds.Length==0?null:eligibleKeys.Where(k=>keyIds.Contains(k.Id)),k=>k.Id);
            var grantsQuery=from a in eligible join g in eligibleGrants on a.Id equals g.ApplicationId where !governed||g.Mode=="RouteMethod" select new{g.Id,g.ApplicationId,g.ApiId};
            var grantIds=savedApps.SelectMany(a=>a.AuthorizationIds).Distinct().ToArray();
            var grantMatches=grantsQuery.Where(g=>term==""||g.Id==byId||g.ApiId==byId||appMatches.Any(a=>a.Id==g.ApplicationId));
            var grants=await Rows(grantMatches.OrderBy(g=>g.ApplicationId).ThenBy(g=>g.Id),grantIds.Length==0?null:grantsQuery.Where(g=>grantIds.Contains(g.Id)),g=>g.Id);
            var parents=keys.Select(k=>k.ApplicationId).Concat(grants.Select(g=>g.ApplicationId)).Concat(savedApps.Select(a=>a.ApplicationId)).Distinct().ToArray();
            var rows=await Rows(appMatches.OrderBy(a=>a.Name).ThenBy(a=>a.Id).Select(a=>new{a.Id,a.Name}),
                parents.Length==0?null:eligible.Where(a=>parents.Contains(a.Id)).Select(a=>new{a.Id,a.Name}),a=>a.Id);
            var ids=rows.Select(a=>a.Id).ToArray();
            var shared=(await db.Set<ApplicationApiPermission>().AsNoTracking().Where(g=>ids.Contains(g.ApplicationId)&&g.EnvironmentId!=env)
                .Select(g=>g.ApplicationId).Distinct().ToArrayAsync(token)).ToHashSet();
            var baseline=promotion.BaselineConfigVersion>0?(await history.ReadAsync(env,promotion.BaselineConfigVersion,token)).Snapshot:null;
            var retainedAccess=baseline is null?null:await ReadRetainedAccessAsync(promotion.Id,artifact.Content,baseline,token);
            var oldApps=baseline?.Applications.ToDictionary(a=>a.Id);
            var keysByApp=keys.ToLookup(k=>k.ApplicationId,k=>new PromotionCredentialChoice(k.Id,k.SecretLast4,k.ExpiresAt));
            var grantsByApp=grants.ToLookup(g=>g.ApplicationId,g=>new PromotionAuthorizationChoice(g.Id,g.ApiId));
            applications=rows.Select(a=>{
                var old=oldApps?.GetValueOrDefault(a.Id);
                return new PromotionApplicationChoice(a.Id,a.Name,keysByApp[a.Id].OrderBy(k=>k.Id).ToArray(),grantsByApp[a.Id].OrderBy(g=>g.Id).ToArray(),shared.Contains(a.Id),
                    old is not null&&retainedAccess!.NeedsCredentials(old)?old.Credentials.Select(k=>k.Id).Order().ToArray():[],old is null?[]:retainedAccess!.ApiIdsFor(old));
            }).ToArray();
        }
        return new(env,clusters,routes,policies,applications,truncated,governed);
    }catch(OperationCanceledException)when(!ct.IsCancellationRequested&&budget.IsCancellationRequested){throw new ApiException(503,"delivery_query_timeout","映射选项查询超时，请稍后重试。");}
 }

 private async Task<RetainedRouteAccess> ReadRetainedAccessAsync(Guid promotionId,ArtifactContent content,RuntimeSnapshot baseline,CancellationToken ct)
 {
    var saved=await db.Set<ReleasePromotionMapping>().AsNoTracking().Where(row=>row.PromotionId==promotionId&&row.Kind=="Route")
        .Select(row=>new{row.ResourceKey,row.TargetRouteId}).ToArrayAsync(ct);
    var mappedIds=saved.Where(row=>row.TargetRouteId.HasValue).Select(row=>row.TargetRouteId!.Value).ToHashSet();
    var savedKeys=saved.Select(row=>row.ResourceKey).ToHashSet();
    // Before a mapping is saved, identify the existing logical route offered by the wizard.
    var unsaved=content.Routes.Where(route=>!savedKeys.Contains(route.Key))
        .Select(route=>Identity(route.ApiId,route.Path,route.Methods)).ToHashSet();
    var retained=baseline.Routes.Where(route=>!mappedIds.Contains(route.Id)&&!unsaved.Contains(Identity(route.ApiId,route.Path,route.Methods))).ToArray();
    return new(baseline,retained,content.Apis.Select(api=>api.ApiId).ToHashSet());
 }

 private static (Guid Api,string Path,string Methods) Identity(Guid apiId,string path,IReadOnlyList<string> methods) =>
     (apiId,RouteNormalizer.Normalize(path),string.Join(",",methods.Order(StringComparer.Ordinal)));
}

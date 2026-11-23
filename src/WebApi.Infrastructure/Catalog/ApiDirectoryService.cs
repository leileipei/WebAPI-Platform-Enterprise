using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Catalog;

public sealed class DirectoryFilter
{
    public bool? Directory {get;set;}
    public Guid? EnvironmentId {get;set;}
    public string? Q {get;set;}
    public string? Method {get;set;}
    public string? Version {get;set;}
    public string? Health {get;set;}
    public string? Owner {get;set;}
    public string? Tag {get;set;}
    public string? Lifecycle {get;set;}
    public int? Page {get;set;}
    public int? PageSize {get;set;}
    public bool Advanced=>Directory==true||EnvironmentId.HasValue||Q!=null||Method!=null||Version!=null||Health!=null||Owner!=null||Tag!=null||Lifecycle!=null;
}

public sealed record ApiDirectoryRow(Guid Id,Guid OrganizationId,Guid ProjectId,Guid? GroupId,string Code,string Name,string? Description,
    string LifecycleStatus,Guid OwnerUserId,long WorkingRevision,string OwnerName,string[]? Methods,string[] Versions,string[]? Tags,string? Health);
public sealed record DirectoryExport(Guid[] Ids);
internal sealed record OperationTag(Guid ApiId,string Tag);

public sealed class ApiDirectoryService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,ApiRuntimeOverview overview)
{
    public async Task<IReadOnlyList<ApiDirectoryRow>> ExportAsync(Guid projectId,DirectoryFilter filter,DirectoryExport request,ActorContext actor,CancellationToken ct)
    {
        if(request.Ids is not {Length:>0 and <=1000}||request.Ids.Distinct().Count()!=request.Ids.Length)
            throw new ApiException(422,"invalid_export_selection","请选择 1 至 1000 个不同的 API。");
        var page=await ListAsync(projectId,filter,actor,ct,request.Ids);
        if(page.Items.Count!=request.Ids.Length)throw new ApiException(409,"directory_selection_changed","部分选择已不可用或不再符合筛选条件，请刷新目录。");
        return page.Items;
    }

    // Query only the operation-level tags in the version's selected contract. Other operations in the source bundle stay private.
    private IQueryable<OperationTag> OperationTags(Guid projectId)=>db.Database.SqlQuery<OperationTag>($$"""
        SELECT DISTINCT a.id AS "ApiId", tag.value #>> '{}' AS "Tag"
        FROM apis a JOIN api_versions v ON v.api_id=a.id
        CROSS JOIN LATERAL jsonb_path_query(v.openapi_document,
            'strict $.paths.*.keyvalue() ? (@.key == "get" || @.key == "post" || @.key == "put" || @.key == "patch" || @.key == "delete" || @.key == "head" || @.key == "options" || @.key == "trace").value.tags[*] ? (@.type() == "string")',
            '{}',true) tag(value)
        WHERE a.project_id={{projectId}}
        """);

    public async Task<PageResult<ApiDirectoryRow>> ListAsync(Guid projectId,DirectoryFilter filter,ActorContext actor,CancellationToken ct,Guid[]? selection=null)
    {
        var scope=await scopes.ProjectAsync(projectId,ct);
        var project=new ResourceRef("project",projectId,scope);
        if(!await auth.CanAsync(actor,"api.read",project,ct))throw ScopeResolver.Missing();
        var environment=filter.EnvironmentId is Guid env?await overview.EnvironmentAsync(projectId,env,actor,ct):null;
        var envScope=scope with{EnvironmentId=environment?.Id};
        Task<bool> CanEnv(string code)=>environment is null?Task.FromResult(false):auth.CanAsync(actor,code,new("environment",environment.Id,envScope),ct);
        var routeRead=await CanEnv("route.read");
        var healthRead=routeRead&&await CanEnv("metrics.read")&&await CanEnv("cluster.read");
        var tagRead=await auth.CanAsync(actor,"api.version.read",project,ct)||await auth.CanAsync(actor,"api.schema.read",project,ct);
        if((filter.Method!=null&&!routeRead)||(filter.Health!=null&&!healthRead)||(filter.Tag!=null&&!tagRead))throw ScopeResolver.Missing();
        var query=db.Set<Api>().AsNoTracking().Where(a=>a.ProjectId==projectId&&a.OrganizationId==scope.OrganizationId);
        if(selection is not null)query=query.Where(a=>selection.Contains(a.Id));
        var versions=db.Set<ApiVersion>().AsNoTracking();
        var routes=from r in db.Set<ApiRoute>().AsNoTracking() join v in versions on r.ApiVersionId equals v.Id
            where r.EnvironmentId==filter.EnvironmentId select new{v.ApiId,r.Methods};
        var tags=OperationTags(projectId);
        if(!string.IsNullOrWhiteSpace(filter.Q)){
            var text=filter.Q.Trim().ToLower();query=query.Where(a=>a.Code.ToLower().Contains(text)||a.Name.ToLower().Contains(text));
        }
        if(!string.IsNullOrWhiteSpace(filter.Method)){
            var method=filter.Method.Trim().ToUpperInvariant();query=query.Where(a=>routes.Any(r=>r.ApiId==a.Id&&r.Methods.Contains(method)));
        }
        if(!string.IsNullOrWhiteSpace(filter.Version)){
            var version=filter.Version.Trim().ToLower();query=query.Where(a=>versions.Any(v=>v.ApiId==a.Id&&v.Version.ToLower().Contains(version)));
        }
        if(!string.IsNullOrWhiteSpace(filter.Lifecycle))query=query.Where(a=>a.LifecycleStatus==filter.Lifecycle);
        if(!string.IsNullOrWhiteSpace(filter.Owner)){
            var owner=filter.Owner.Trim().ToLower();Guid.TryParse(owner,out var id);
            query=query.Where(a=>a.OwnerUserId==id||db.Set<UserRecord>().Any(u=>u.Id==a.OwnerUserId&&(u.DisplayName.ToLower().Contains(owner)||u.Username.ToLower().Contains(owner))));
        }
        if(!string.IsNullOrWhiteSpace(filter.Tag)){
            var tag=filter.Tag.Trim().ToLower();query=query.Where(a=>tags.Any(t=>t.ApiId==a.Id&&t.Tag.ToLower().Contains(tag)));
        }
        var runtime=healthRead?await overview.RuntimeAsync(environment!,true,ct):null;
        var healthByApi=runtime?.Snapshot?.Routes.GroupBy(r=>r.ApiId).ToDictionary(g=>g.Key,g=>ApiRuntimeState.Aggregate(g.Select(r=>runtime.Health.GetValueOrDefault(r.ClusterId,"Unknown"))))??[];
        var fallback=runtime?.Snapshot is null?"Unknown":"NotRunning";
        if(filter.Health is { } health){
            if(health is not ("Unknown" or "Disabled" or "Healthy" or "Unhealthy" or "NotRunning"))throw new ApiException(422,"invalid_health_filter","健康状态筛选值无效。");
            var include=health!=fallback;
            var ids=healthByApi.Where(p=>include?p.Value==health:p.Value!=health).Select(p=>p.Key).ToArray();
            query=include?query.Where(a=>ids.Contains(a.Id)):query.Where(a=>!ids.Contains(a.Id));
        }
        var page=selection is null?Math.Clamp(filter.Page??1,1,1000000):1;var size=selection?.Length??Math.Clamp(filter.PageSize??50,1,100);
        var total=await query.CountAsync(ct);
        var rows=await (from a in query join u in db.Set<UserRecord>() on a.OwnerUserId equals u.Id
            orderby a.Code,a.Id select new{Api=a,Owner=u.DisplayName}).Skip((page-1)*size).Take(size).ToArrayAsync(ct);
        var keys=rows.Select(r=>r.Api.Id).ToArray();
        var versionRows=await versions.Where(v=>keys.Contains(v.ApiId)).OrderByDescending(v=>v.CreatedAt).ThenBy(v=>v.Id).Select(v=>new {v.ApiId,v.Version}).ToArrayAsync(ct);
        var routeRows=routeRead?await routes.Where(r=>keys.Contains(r.ApiId)).ToArrayAsync(ct):[];
        var tagRows=tagRead?await tags.Where(t=>keys.Contains(t.ApiId)).ToArrayAsync(ct):[];
        if(!await auth.CanAsync(actor,"api.read",project,ct)||environment!=null&&!await CanEnv("api.read")||
            routeRead&&!await CanEnv("route.read")||healthRead&&(!await CanEnv("metrics.read")||!await CanEnv("cluster.read"))||
            tagRead&&!await auth.CanAsync(actor,"api.version.read",project,ct)&&!await auth.CanAsync(actor,"api.schema.read",project,ct))throw ScopeResolver.Missing();
        var versionList=versionRows.ToLookup(v=>v.ApiId,v=>v.Version);var methodList=routeRows.ToLookup(r=>r.ApiId);var tagList=tagRows.ToLookup(t=>t.ApiId,t=>t.Tag);
        return new(rows.Select(row=>{
            var a=row.Api;
            return new ApiDirectoryRow(a.Id,a.OrganizationId,a.ProjectId,a.GroupId,a.Code,a.Name,a.Description,a.LifecycleStatus,a.OwnerUserId,a.VersionNo,row.Owner,
                routeRead?methodList[a.Id].SelectMany(r=>r.Methods).Distinct().Order().ToArray():null,versionList[a.Id].ToArray(),
                tagRead?tagList[a.Id].Distinct().Order().ToArray():null,healthRead?healthByApi.GetValueOrDefault(a.Id,fallback):null);
        }).ToArray(),total,page,size);
    }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Catalog;
public sealed class CatalogService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,AuditedCommandExecutor commands,VersionContractSourceService sources,SchemaValidationService validation)
{
    public static ApiDto Dto(Api a)=>new(a.Id,a.OrganizationId,a.ProjectId,a.GroupId,a.Code,a.Name,a.Description,a.LifecycleStatus,a.OwnerUserId,a.VersionNo);
    public static VersionDto Dto(ApiVersion v)=>new(v.Id,v.ApiId,v.Version,v.Status,v.ChangeType,v.OpenapiDocument,v.OpenapiSource,v.SourceFormat,v.SchemaHash,v.CreatedBy,v.CreatedAt,v.SealedAt,v.Revision);
    private static GroupDto Dto(ApiGroup g)=>new(g.Id,g.ProjectId,g.Name,g.ParentId,g.SortOrder,g.Revision);
    public static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public async Task<ScopeRef> ReadAsync(Guid apiId,string permission,ActorContext actor,CancellationToken ct)
    {var scope=await scopes.ApiAsync(apiId,ct);if(!await auth.CanAsync(actor,permission,new("api",apiId,scope),ct)) throw ScopeResolver.Missing();return scope;}
    public async Task<PageResult<ApiDto>> ListAsync(Guid projectId,ActorContext actor,int page,int size,CancellationToken ct=default)
    {var scope=await scopes.ProjectAsync(projectId,ct);if(!await auth.CanAsync(actor,"api.read",new("project",projectId,scope),ct)) throw ScopeResolver.Missing();var all=await db.Set<Api>().AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Code).Select(x=>Dto(x)).ToArrayAsync(ct);return Pagination.Slice(all,page,size);}
    public async Task<ApiDetailDto> DetailAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {
        var scope=await ReadAsync(id,"api.read",actor,ct);var api=await db.Set<Api>().AsNoTracking().SingleAsync(x=>x.Id==id,ct);
        var visible=new Dictionary<Guid,long?>();var envs=await db.Set<EnvironmentRecord>().AsNoTracking().Where(e=>e.ProjectId==scope.ProjectId).ToArrayAsync(ct);
        foreach(var e in envs) if(await auth.CanAsync(actor,"api.read",new("environment",e.Id,scope with {EnvironmentId=e.Id}),ct))
        {var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==e.Id&&n.Enabled).ToArrayAsync(ct);visible[e.Id]=nodes.Length>0&&nodes.All(n=>n.LastHeartbeatAt>DateTimeOffset.UtcNow.AddSeconds(-30))&&nodes.Select(n=>n.CurrentConfigVersion).Distinct().Count()==1?nodes[0].CurrentConfigVersion:null;}
        var envIds=visible.Keys.ToArray();var pending=await db.Set<ReleaseRecord>().Where(r=>envIds.Contains(r.EnvironmentId)&&r.Status!="Succeeded"&&r.Status!="Failed"&&r.Status!="Cancelled"&&r.Status!="Rejected"&&r.Status!="RolledBack").OrderByDescending(r=>r.CreatedAt).Select(r=>(Guid?)r.Id).FirstOrDefaultAsync(ct);
        var versions=await db.Set<ApiVersion>().AsNoTracking().Where(v=>v.ApiId==id).OrderByDescending(v=>v.CreatedAt).Select(v=>Dto(v)).ToArrayAsync(ct);return new(Dto(api),api.VersionNo,visible,pending,versions);
    }
    private async Task ValidateApiAsync(SaveApiRequest request,ScopeRef scope,Guid owner,CancellationToken ct)
    {
        GovernanceService.Validate(request.Code,request.Name);if(request.LifecycleStatus is not ("Draft" or "Active" or "Deprecated" or "Retired")||request.Description?.Length>10000) throw new ApiException(422,"invalid_api","API生命周期或描述不合法。");
        if(request.GroupId is Guid group&&!await db.Set<ApiGroup>().AnyAsync(g=>g.Id==group&&g.ProjectId==scope.ProjectId,ct)) throw new ApiException(422,"foreign_group","API分组不属于项目。");
        if(!await db.Set<UserRecord>().AnyAsync(u=>u.Id==owner&&u.Status=="Active",ct)||!await db.Set<UserProjectScope>().AnyAsync(s=>s.UserId==owner&&s.OrganizationId==scope.OrganizationId&&(s.ProjectId==null||s.ProjectId==scope.ProjectId),ct)) throw new ApiException(422,"invalid_owner","Owner必须具有项目数据范围。");
    }
    public async Task<CommandResult<ApiDto>> CreateAsync(Guid projectId,SaveApiRequest request,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.ProjectAsync(projectId,ct);return await commands.ExecuteAsync(actor,scope,"api.create",async(_,token)=>{
        await auth.RequireAsync(actor,"api.create",new("project",projectId,scope),token);var owner=request.OwnerUserId??actor.UserId;await ValidateApiAsync(request,scope,owner,token);
        var api=new Api {OrganizationId=scope.OrganizationId,ProjectId=projectId,Code=request.Code,Name=request.Name,Description=request.Description,GroupId=request.GroupId,OwnerUserId=owner,LifecycleStatus=request.LifecycleStatus};db.Add(api);return new CommandResult<ApiDto>(Dto(api),RevisionTag.Format(api.VersionNo));},ct);}
    public async Task<CommandResult<ApiDto>> SaveAsync(Guid id,SaveApiRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.ApiAsync(id,ct);return await commands.ExecuteAsync(actor,scope,"api.edit",async(_,token)=>{
        await auth.RequireAsync(actor,"api.edit",new("api",id,scope),token);var api=await db.Set<Api>().SingleAsync(x=>x.Id==id,token);RevisionTag.Require(tag,api.VersionNo);var owner=request.OwnerUserId??api.OwnerUserId;await ValidateApiAsync(request,scope,owner,token);
        api.Code=request.Code;api.Name=request.Name;api.Description=request.Description;api.GroupId=request.GroupId;api.OwnerUserId=owner;api.LifecycleStatus=request.LifecycleStatus;api.VersionNo++;api.UpdatedAt=DateTimeOffset.UtcNow;return new CommandResult<ApiDto>(Dto(api),RevisionTag.Format(api.VersionNo));},ct);}
    public async Task DeleteAsync(Guid id,string? tag,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.ApiAsync(id,ct);await commands.ExecuteAsync(actor,scope,"api.delete",async(_,token)=>{await auth.RequireAsync(actor,"api.edit",new("api",id,scope),token);var api=await db.Set<Api>().SingleAsync(x=>x.Id==id,token);RevisionTag.Require(tag,api.VersionNo);if(await db.Set<ApiVersion>().AnyAsync(v=>v.ApiId==id,token)||await db.Set<ApplicationApiPermission>().AnyAsync(p=>p.ApiId==id,token)) throw new ApiException(409,"api_in_use","API已有版本或授权，请使用生命周期停用。");db.Remove(api);return true;},ct);}
    public static void ValidateVersion(CreateVersionRequest request)
    {
        if(string.IsNullOrWhiteSpace(request.Version)||request.Version.Length>32||request.ChangeType is not ("compatible" or "breaking" or "unknown")) throw new ApiException(422,"invalid_version","版本号或变更类型不合法。");
        if(request.SourceFormat is not (null or "json" or "yaml")) throw new ApiException(422,"unsupported_format","来源格式必须为JSON或YAML。");JsonFields.Validate(request.OpenapiDocument);
    }
    public async Task<CommandResult<VersionDto>> CreateVersionAsync(Guid apiId,CreateVersionRequest request,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.ApiAsync(apiId,ct);return await commands.ExecuteAsync(actor,scope,"api.version.create",async(_,token)=>{
        await auth.RequireAsync(actor,"api.version.write",new("api",apiId,scope),token);ValidateVersion(request);var v=new ApiVersion {ApiId=apiId,Version=request.Version,ChangeType=request.ChangeType,OpenapiDocument=request.OpenapiDocument,OpenapiSource=request.OpenapiSource,SourceFormat=request.SourceFormat,SchemaHash=request.OpenapiDocument is null?null:Hash(request.OpenapiDocument),CreatedBy=actor.UserId,Status="Draft"};var bundle=VersionContractSourceService.Prepare(v.Id,request,token,out var document,out var source,out var format);v.OpenapiDocument=document;v.OpenapiSource=source;v.SourceFormat=format;v.SchemaHash=document is null?null:Hash(document);db.Add(v);await sources.SaveDraftAsync(v.Id,bundle,token);return new CommandResult<VersionDto>(Dto(v) with{Dialect=VersionContractSourceService.DialectName(bundle.Documents[0].Dialect)},RevisionTag.Format(v.Revision));},ct);}
    public static void RequireDraft(ApiVersion version) {if(version.Status!="Draft"||version.SealedAt is not null) throw new ApiException(409,"version_immutable","已发布或冻结的版本不能覆盖修改，请创建新版本。");}
    public async Task<VersionDto> VersionAsync(Guid id,ActorContext actor,CancellationToken ct=default)
    {var v=await db.Set<ApiVersion>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();await ReadAsync(v.ApiId,"api.version.read",actor,ct);var dialect=await db.Set<ApiVersionContractSources>().AsNoTracking().Where(x=>x.ApiVersionId==id).Select(x=>x.Dialect).SingleOrDefaultAsync(ct);return Dto(v) with{Dialect=dialect=="Oas30"?"oas-3.0":dialect=="Oas31"?"oas-3.1":null};}
    public async Task<CommandResult<VersionDto>> SaveVersionAsync(Guid id,CreateVersionRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.VersionAsync(id,ct);return await commands.ExecuteAsync(actor,scope,"api.version.update",async(_,token)=>{
        await auth.RequireAsync(actor,"api.version.write",new("version",id,scope),token);var v=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==id,token);RevisionTag.Require(tag,v.Revision);RequireDraft(v);ValidateVersion(request);
        var replacement = request.OpenapiDocument is not null && request.OpenapiDocument != v.OpenapiDocument
            || request.OpenapiSource is not null && request.OpenapiSource != v.OpenapiSource
            || request.SourceFormat is not null && request.SourceFormat != v.SourceFormat;
        string? dialect;
        if (replacement) {
            var bundle=VersionContractSourceService.Prepare(v.Id,request,token,out var document,out var source,out var format);
            v.OpenapiDocument=document;v.OpenapiSource=source;v.SourceFormat=format;v.SchemaHash=document is null?null:Hash(document);
            await sources.SaveDraftAsync(v.Id,bundle,token);dialect=VersionContractSourceService.DialectName(bundle.Documents[0].Dialect);
        } else {
            var savedDialect=await db.Set<ApiVersionContractSources>().Where(x=>x.ApiVersionId==id).Select(x=>x.Dialect).SingleOrDefaultAsync(token);
            dialect=savedDialect=="Oas30"?"oas-3.0":savedDialect=="Oas31"?"oas-3.1":null;
            if(request.Dialect is not null && request.Dialect!=dialect)throw new ApiException(422,"contract_dialect_mismatch","修改方言需显式替换契约来源。");
        }
        v.Version=request.Version;v.ChangeType=request.ChangeType;v.Revision++;
        return new CommandResult<VersionDto>(Dto(v) with{Dialect=dialect},RevisionTag.Format(v.Revision));},ct);}
    public async Task DeleteVersionAsync(Guid id,string? tag,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.VersionAsync(id,ct);await commands.ExecuteAsync(actor,scope,"api.version.delete",async(_,token)=>{await auth.RequireAsync(actor,"api.version.write",new("version",id,scope),token);var v=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==id,token);RevisionTag.Require(tag,v.Revision);RequireDraft(v);if(await db.Set<ApiRoute>().AnyAsync(r=>r.ApiVersionId==id,token)) throw new ApiException(409,"version_in_use","版本已被路由引用。");db.RemoveRange(await db.Set<ApiParameter>().Where(p=>p.ApiVersionId==id).ToArrayAsync(token));db.RemoveRange(await db.Set<ApiSchema>().Where(s=>s.ApiVersionId==id).ToArrayAsync(token));db.Remove(v);return true;},ct);}
    public async Task<IReadOnlyList<GroupDto>> GroupsAsync(Guid projectId,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.ProjectAsync(projectId,ct);if(!await auth.CanAsync(actor,"api.read",new("project",projectId,scope),ct)) throw ScopeResolver.Missing();return await db.Set<ApiGroup>().AsNoTracking().Where(g=>g.ProjectId==projectId).OrderBy(g=>g.SortOrder).Select(g=>Dto(g)).ToArrayAsync(ct);}
    public async Task<CommandResult<GroupDto>> SaveGroupAsync(Guid projectId,Guid? id,SaveGroupRequest request,string? tag,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.ProjectAsync(projectId,ct);return await commands.ExecuteAsync(actor,scope,"api.group.save",async(_,token)=>{
        await auth.RequireAsync(actor,"api.edit",new("project",projectId,scope),token);if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>128) throw new ApiException(422,"invalid_group","分组名称不合法。");
        if(request.ParentId is Guid parent) {var seen=new HashSet<Guid>();Guid? cursor=parent;while(cursor is Guid current) {if(current==id||!seen.Add(current)) throw new ApiException(422,"group_cycle","分组不能形成循环。");var p=await db.Set<ApiGroup>().SingleOrDefaultAsync(g=>g.Id==current&&g.ProjectId==projectId,token)??throw new ApiException(422,"foreign_group","父分组不属于项目。");cursor=p.ParentId;}}
        var group=id is Guid existing?await db.Set<ApiGroup>().SingleOrDefaultAsync(g=>g.Id==existing&&g.ProjectId==projectId,token)??throw ScopeResolver.Missing():new ApiGroup {ProjectId=projectId};if(id is not null) {RevisionTag.Require(tag,group.Revision);group.Revision++;}else db.Add(group);group.Name=request.Name;group.ParentId=request.ParentId;group.SortOrder=request.SortOrder;return new CommandResult<GroupDto>(Dto(group),RevisionTag.Format(group.Revision));},ct);}
    public async Task DeleteGroupAsync(Guid id,string? tag,ActorContext actor,CancellationToken ct=default)
    {var group=await db.Set<ApiGroup>().AsNoTracking().SingleOrDefaultAsync(g=>g.Id==id,ct)??throw ScopeResolver.Missing();var scope=await scopes.ProjectAsync(group.ProjectId,ct);await commands.ExecuteAsync(actor,scope,"api.group.delete",async(_,token)=>{await auth.RequireAsync(actor,"api.edit",new("group",id,scope),token);var g=await db.Set<ApiGroup>().SingleAsync(x=>x.Id==id,token);RevisionTag.Require(tag,g.Revision);if(await db.Set<ApiGroup>().AnyAsync(x=>x.ParentId==id,token)||await db.Set<Api>().AnyAsync(x=>x.GroupId==id,token)) throw new ApiException(409,"group_in_use","分组包含子分组或API。");db.Remove(g);return true;},ct);}
    public async Task<IReadOnlyList<ParameterDto>> ParametersAsync(Guid id,ActorContext actor,CancellationToken ct=default,bool includeExamples=false)
    {var scope=await scopes.VersionAsync(id,ct);if(!await auth.CanAsync(actor,"api.schema.read",new("version",id,scope),ct)) throw ScopeResolver.Missing();var rows=await db.Set<ApiParameter>().AsNoTracking().Where(p=>p.ApiVersionId==id).Select(p=>new ParameterDto(p.Id,p.ApiVersionId,p.Location,p.Name,p.DataType,p.Required,p.Schema,p.Description,p.ExampleJson)).ToArrayAsync(ct);if(!includeExamples)return rows;var examples=await ExampleProjectionAsync(id,ct);return rows.Select(row=>row with{Examples=examples.Read(row.Id,"parameter",row.Schema)}).ToArray();}
    public async Task<IReadOnlyList<SchemaDto>> SchemasAsync(Guid id,ActorContext actor,CancellationToken ct=default,bool includeExamples=false)
    {var scope=await scopes.VersionAsync(id,ct);if(!await auth.CanAsync(actor,"api.schema.read",new("version",id,scope),ct)) throw ScopeResolver.Missing();var rows=await db.Set<ApiSchema>().AsNoTracking().Where(s=>s.ApiVersionId==id).Select(s=>new SchemaDto(s.Id,s.ApiVersionId,s.SchemaType,s.Name,s.StatusCode,s.ContentType,s.SchemaJson,s.SchemaHash,s.ExampleJson)).ToArrayAsync(ct);if(!includeExamples)return rows;var examples=await ExampleProjectionAsync(id,ct);return rows.Select(row=>row with{Examples=examples.Read(row.Id,"schema",row.SchemaJson)}).ToArray();}
    private async Task<ContractExampleProjection> ExampleProjectionAsync(Guid id,CancellationToken ct)
    {var source=await db.Set<ApiVersionContractSources>().AsNoTracking().AnyAsync(x=>x.ApiVersionId==id,ct)?await sources.LoadAsync(await db.Set<ApiVersion>().AsNoTracking().SingleAsync(x=>x.Id==id,ct),ct):null;return new(source,ct);}
    public async Task<CommandResult<IReadOnlyList<ParameterDto>>> SaveParametersAsync(Guid id,IReadOnlyList<SaveParameterRequest> request,string? tag,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.VersionAsync(id,ct);return await commands.ExecuteAsync(actor,scope,"api.parameters.save",async(_,token)=>{
        await auth.RequireAsync(actor,"api.schema.write",new("version",id,scope),token);var v=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==id,token);RequireDraft(v);RevisionTag.Require(tag,v.Revision);
        var sourceState=await sources.LoadAsync(v,token);var definitions=await sources.DefinitionsAsync(id,token);var candidates=request.Select(r=>new MaintainedDefinition(r.Id??Guid.NewGuid(),"parameter",r.Name,r.Schema??System.Text.Json.JsonSerializer.Serialize(new{type=r.DataType}),false)).ToArray();var graph=sources.BuildGraph(sourceState,definitions.Where(d=>d.Kind!="parameter").Concat(candidates).ToArray(),token);await validation.ValidateGraphAsync(graph,token);
        var old=await db.Set<ApiParameter>().Where(p=>p.ApiVersionId==id).ToArrayAsync(token);var result=new List<ParameterDto>();var names=new HashSet<string>(StringComparer.Ordinal);var ids=new HashSet<Guid>();
        for(var index=0;index<request.Count;index++)
        {var r=request[index];
            if(r.Location is not ("header" or "query" or "path" or "cookie")||string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>128||string.IsNullOrWhiteSpace(r.DataType)||r.DataType.Length>32||r.Location=="path"&&!r.Required) throw new ApiException(422,"invalid_parameter","参数名称、位置、类型或必填标识不合法。");
            if(!names.Add(r.Location+":"+(r.Location=="header"?r.Name.ToLowerInvariant():r.Name))) throw new ApiException(409,"duplicate_parameter","参数重复。");JsonFields.Validate(r.Schema);JsonFields.Validate(r.ExampleJson);
            var p=r.Id is Guid key?old.SingleOrDefault(p=>p.Id==key)??throw new ApiException(422,"foreign_parameter","参数不属于版本。"):new ApiParameter {Id=candidates[index].Id,ApiVersionId=id};if(!ids.Add(p.Id)) throw new ApiException(422,"duplicate_parameter_id","参数ID重复。");if(r.Id is null) db.Add(p);
            p.Location=r.Location;p.Name=r.Name;p.DataType=r.DataType;p.Required=r.Required;p.Schema=r.Schema;p.Description=r.Description;p.ExampleJson=r.ExampleJson;result.Add(new(p.Id,id,p.Location,p.Name,p.DataType,p.Required,p.Schema,p.Description,p.ExampleJson));
        }
        db.RemoveRange(old.Where(p=>!ids.Contains(p.Id)));v.Revision++;await sources.SaveMaintenanceAsync(v,sourceState,graph,token);return new CommandResult<IReadOnlyList<ParameterDto>>(result,RevisionTag.Format(v.Revision));},ct);}
    public async Task<CommandResult<IReadOnlyList<SchemaDto>>> SaveSchemasAsync(Guid id,IReadOnlyList<SaveSchemaRequest> request,string? tag,ActorContext actor,CancellationToken ct=default)
    {var scope=await scopes.VersionAsync(id,ct);return await commands.ExecuteAsync(actor,scope,"api.schemas.save",async(_,token)=>{
        await auth.RequireAsync(actor,"api.schema.write",new("version",id,scope),token);var v=await db.Set<ApiVersion>().SingleAsync(x=>x.Id==id,token);RequireDraft(v);RevisionTag.Require(tag,v.Revision);var sourceState=await sources.LoadAsync(v,token);var definitions=await sources.DefinitionsAsync(id,token);var candidates=request.Select(r=>new MaintainedDefinition(r.Id??Guid.NewGuid(),"schema",r.Name,r.SchemaJson,r.SchemaType=="component")).ToArray();var all=definitions.Where(d=>d.Kind!="schema").Concat(candidates).ToArray();validation.RequireNoComponentImpact(sourceState,definitions,all,token);var graph=sources.BuildGraph(sourceState,all,token);await validation.ValidateGraphAsync(graph,token);
        var old=await db.Set<ApiSchema>().Where(s=>s.ApiVersionId==id).ToArrayAsync(token);var result=new List<SchemaDto>();var ids=new HashSet<Guid>();var names=new HashSet<string>();
        for(var index=0;index<request.Count;index++)
        {var r=request[index];
            if(r.SchemaType is not ("request" or "response" or "component")||string.IsNullOrWhiteSpace(r.Name)||r.Name.Length>128||string.IsNullOrWhiteSpace(r.ContentType)||r.ContentType.Length>128||r.StatusCode is <100 or >599) throw new ApiException(422,"invalid_schema","Schema字段不合法。");JsonFields.Validate(r.SchemaJson,false);JsonFields.Validate(r.ExampleJson);if(!names.Add($"{r.SchemaType}:{r.Name}:{r.StatusCode}:{r.ContentType}")) throw new ApiException(409,"duplicate_schema","Schema重复。");
            var s=r.Id is Guid key?old.SingleOrDefault(s=>s.Id==key)??throw new ApiException(422,"foreign_schema","Schema不属于版本。"):new ApiSchema {Id=candidates[index].Id,ApiVersionId=id};if(!ids.Add(s.Id)) throw new ApiException(422,"duplicate_schema_id","Schema ID重复。");if(r.Id is null) db.Add(s);
            s.SchemaType=r.SchemaType;s.Name=r.Name;s.StatusCode=r.StatusCode;s.ContentType=r.ContentType;s.SchemaJson=r.SchemaJson;s.SchemaHash=Hash(r.SchemaJson);s.ExampleJson=r.ExampleJson;result.Add(new(s.Id,id,s.SchemaType,s.Name,s.StatusCode,s.ContentType,s.SchemaJson,s.SchemaHash,s.ExampleJson));
        }
        db.RemoveRange(old.Where(s=>!ids.Contains(s.Id)));v.Revision++;await sources.SaveMaintenanceAsync(v,sourceState,graph,token);return new CommandResult<IReadOnlyList<SchemaDto>>(result,RevisionTag.Format(v.Revision));},ct);}
}

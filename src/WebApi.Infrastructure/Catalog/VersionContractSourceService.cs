using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Contracts;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Catalog;
internal sealed record MaintainedDefinition(Guid Id,string Kind,string Name,string Schema,bool Component);
internal sealed record VersionContractState(ContractBundle Bundle,ContractSourceMetadata Metadata,long PolicyRevision);
internal sealed record MaintenanceGraph(ContractBundle Bundle,IReadOnlyList<ContractDefinitionSource> Definitions);
public sealed class VersionContractSourceService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes)
{
    internal static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web){MaxDepth=64};
    internal static Uri RootUri(Guid id)=>new($"https://contracts.invalid/versions/{id:D}/openapi.json");
    internal static string DocumentHash(string? json)=>json is null?CatalogService.Hash("null"):CatalogService.Hash(ContractDocumentReader.Canonicalize(JsonNode.Parse(json,documentOptions:new(){MaxDepth=64})!).ToJsonString(JsonOptions));
    internal static string DialectName(ContractDialect dialect)=>dialect==ContractDialect.Oas30?"oas-3.0":"oas-3.1";
    private static string EmptyRoot(ContractDialect dialect)=>new JsonObject{["openapi"]=dialect==ContractDialect.Oas30?"3.0.3":"3.1.0",["info"]=new JsonObject{["title"]="Maintained contract",["version"]="1"},["paths"]=new JsonObject()}.ToJsonString();
    internal static ContractBundle Prepare(Guid id,CreateVersionRequest request,CancellationToken ct,out string? document,out string? source,out string? format)
    {
        var reader=new ContractDocumentReader();var limits=new ContractLimits();var uri=RootUri(id);
        if(request.Dialect is not(null or "oas-3.0" or "oas-3.1"))throw Invalid("unsupported_schema_dialect","版本方言未登记。");
        if(request.SourceFormat is not(null or "json" or "yaml"))throw Invalid("unsupported_format","来源格式必须为JSON或YAML。");
        ContractDocument? canonical=null,raw=null;
        if(request.OpenapiDocument is not null)canonical=reader.Read(new(uri,request.OpenapiDocument,"json"),limits,ct);
        if(request.OpenapiSource is not null)raw=reader.Read(new(uri,request.OpenapiSource,request.SourceFormat??"auto"),limits,ct);
        if(canonical is not null&&raw is not null&&canonical.CanonicalJson!=raw.CanonicalJson)throw Invalid("contract_source_mismatch","规范化文档与原始来源内容不一致。");
        var root=raw??canonical;
        if(root is not null&&request.Dialect is not null&&DialectName(root.Dialect)!=request.Dialect)throw Invalid("contract_dialect_mismatch","版本方言与根契约不一致。");
        document=root?.CanonicalJson;source=request.OpenapiSource??request.OpenapiDocument;format=root?.Source.Format;
        root??=reader.Read(new(uri,EmptyRoot(request.Dialect=="oas-3.0"?ContractDialect.Oas30:ContractDialect.Oas31),"json"),limits,ct);
        return ContractBundleCodec.Create(uri,[root],limits);
    }
    public async Task<ContractBundle> ReadAsync(Guid versionId,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.VersionAsync(versionId,ct);
        if(!await auth.CanAsync(actor,"api.schema.read",new("version",versionId,scope),ct)&&!await auth.CanAsync(actor,"api.version.read",new("version",versionId,scope),ct))throw ScopeResolver.Missing();
        var version=await db.Set<ApiVersion>().AsNoTracking().SingleAsync(x=>x.Id==versionId,ct);return(await LoadAsync(version,ct)).Bundle;
    }
    internal async Task<VersionContractState> LoadAsync(ApiVersion version,CancellationToken ct)
    {
        var row=await db.Set<ApiVersionContractSources>().AsNoTracking().SingleOrDefaultAsync(x=>x.ApiVersionId==version.Id,ct);
        if(row is not null){
            var bundle=ContractBundleCodec.Decode(row.BundleJson);if(bundle.Hash!=row.BundleHash)throw Invalid("contract_bundle_tampered","固定来源包哈希不匹配。");
            ContractSourceMetadata metadata;try{metadata=JsonSerializer.Deserialize<ContractSourceMetadata>(row.SourcesJson,JsonOptions)??throw new JsonException();}catch(JsonException){throw Invalid("contract_source_stale","来源元数据无法核对，请重新导入。");}
            if(metadata.Definitions is null||metadata.Documents is null||metadata.VersionDocumentHash!=DocumentHash(version.OpenapiDocument))throw Invalid("contract_source_stale","版本文档与来源元数据不一致，请重新导入。");
            if(!Enum.TryParse<ContractDialect>(row.Dialect,out var dialect)||dialect!=bundle.Documents.Single(x=>x.Source.LogicalUri==bundle.RootUri).Dialect)throw Invalid("contract_dialect_mismatch","来源方言不一致。");
            return new(bundle,metadata,row.SourcePolicyRevision);
        }
        var root=version.OpenapiDocument??EmptyRoot(ContractDialect.Oas31);
        var document=new ContractDocumentReader().Read(new(RootUri(version.Id),root,"json"),new(),ct);
        var legacy=ContractBundleCodec.Create(document.Source.LogicalUri,[document]);
        return new(legacy,new(legacy.Documents.Select(d=>new ContractResourceSource(d.Source.LogicalUri,d.Source.Format)).ToArray(),[],DocumentHash(version.OpenapiDocument)),0);
    }
    public async Task SaveDraftAsync(Guid versionId,ContractBundle bundle,CancellationToken ct)
    {
        if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Source writes require the catalog transaction.");
        var version=db.Set<ApiVersion>().Local.SingleOrDefault(x=>x.Id==versionId)??await db.Set<ApiVersion>().SingleAsync(x=>x.Id==versionId,ct);CatalogService.RequireDraft(version);ContractBundleCodec.Verify(bundle);
        var row=await Row(versionId,ct);row.BundleJson=ContractBundleCodec.Encode(bundle);row.BundleHash=bundle.Hash;row.Dialect=bundle.Documents.Single(x=>x.Source.LogicalUri==bundle.RootUri).Dialect.ToString();row.SourcePolicyRevision=0;
        row.SourcesJson=JsonSerializer.Serialize(new ContractSourceMetadata(bundle.Documents.Select(d=>new ContractResourceSource(d.Source.LogicalUri,d.Source.Format)).ToArray(),[],DocumentHash(version.OpenapiDocument)),JsonOptions);
    }
    internal async Task<IReadOnlyList<MaintainedDefinition>> DefinitionsAsync(Guid id,CancellationToken ct)
    {
        var schemas=await db.Set<ApiSchema>().AsNoTracking().Where(x=>x.ApiVersionId==id).ToArrayAsync(ct);var parameters=await db.Set<ApiParameter>().AsNoTracking().Where(x=>x.ApiVersionId==id).ToArrayAsync(ct);
        return schemas.Select(x=>new MaintainedDefinition(x.Id,"schema",x.Name,x.SchemaJson,x.SchemaType=="component")).Concat(parameters.Select(x=>new MaintainedDefinition(x.Id,"parameter",x.Name,x.Schema??JsonSerializer.Serialize(new{type=x.DataType},JsonOptions),false))).ToArray();
    }
    internal MaintenanceGraph BuildGraph(VersionContractState state,IReadOnlyList<MaintainedDefinition> definitions,CancellationToken ct)
        =>BuildMaintenanceGraph(state,definitions,ct);
    internal static MaintenanceGraph BuildMaintenanceGraph(VersionContractState state,IReadOnlyList<MaintainedDefinition> definitions,CancellationToken ct,ContractLimits? representationLimits=null)
    {
        var limits=representationLimits??new ContractLimits();var definitionLimits=representationLimits is null?limits:limits with{MaxDepth=Math.Min(64,limits.MaxDepth-16),MaxDocumentBytes=Math.Min(2*1024*1024,limits.MaxDocumentBytes)};long bytes=definitions.Sum(x=>(long)Encoding.UTF8.GetByteCount(x.Schema))+state.Bundle.Documents.Sum(x=>(long)Encoding.UTF8.GetByteCount(x.CanonicalJson));if(bytes>limits.MaxBundleBytes)throw new ApiException(413,"contract_budget","维护定义与来源包超过预算。");
        if(definitions.Where(x=>x.Component).GroupBy(x=>x.Name,StringComparer.Ordinal).Any(group=>group.Count()>1))throw Invalid("ambiguous_component_definition","组件名称在维护图中必须唯一，不能按媒体类型覆盖另一个组件。");
        var nodes=state.Bundle.Documents.ToDictionary(x=>x.Source.LogicalUri,x=>x.Root.DeepClone());var originals=new ContractReferenceRegistry(state.Bundle,limits);var origins=new List<ContractDefinitionSource>();
        var root=(JsonObject)nodes[state.Bundle.RootUri];root["components"]??=new JsonObject();var components=root["components"] as JsonObject??throw Invalid("invalid_contract","components必须为对象。");components["schemas"]??=new JsonObject();var componentSchemas=components["schemas"] as JsonObject??throw Invalid("invalid_contract","components.schemas必须为对象。");
        foreach(var definition in definitions){
            ct.ThrowIfCancellationRequested();var schema=new ContractDocumentReader().ReadResource(new(new Uri("https://maintenance.invalid/schema"),definition.Schema,"json"),definitionLimits,state.Bundle.Documents.Single(d=>d.Source.LogicalUri==state.Bundle.RootUri).Dialect,ct).Root;
            var old=state.Metadata.Definitions.SingleOrDefault(x=>x.Id==definition.Id&&x.Kind==definition.Kind);Uri documentUri;string pointer;
            if(definition.Component){documentUri=state.Bundle.RootUri;pointer="/components/schemas/"+ContractDocumentReader.Escape(definition.Name);componentSchemas[definition.Name]=schema.DeepClone();}
            else if(old is not null){
                (documentUri,pointer)=Physical(state.Bundle,originals,old);
                if(IsIndependentSource(state.Bundle,old)&&!componentSchemas.ContainsKey(pointer.Split('/')[^1]))componentSchemas[pointer.Split('/')[^1]]=schema.DeepClone();
                else Set(nodes[documentUri],pointer,schema);
            }
            else{documentUri=state.Bundle.RootUri;var name="__maintained_"+definition.Id.ToString("N");if(componentSchemas.ContainsKey(name))throw Invalid("duplicate_schema_id","维护定义资源名称冲突。");pointer="/components/schemas/"+name;componentSchemas[name]=schema.DeepClone();}
            origins.Add(new(definition.Id,definition.Kind,documentUri,pointer,DocumentHash(definition.Schema),documentUri));
        }
        var reader=new ContractDocumentReader();var docs=state.Bundle.Documents.Select(d=>{
            if(JsonNode.DeepEquals(nodes[d.Source.LogicalUri],d.Root))return d;
            var text=nodes[d.Source.LogicalUri].ToJsonString(representationLimits is null?JsonOptions:new(JsonOptions){MaxDepth=128});var source=new ContractSource(d.Source.LogicalUri,text,"json");return d.Source.LogicalUri==state.Bundle.RootUri?reader.Read(source,limits,ct):reader.ReadResource(source,limits,d.Dialect,ct);
        }).ToArray();return new(ContractBundleCodec.Create(state.Bundle.RootUri,docs,limits),origins);
    }
    internal static bool IsIndependentSource(ContractBundle bundle,ContractDefinitionSource source)=>source.ResourceUri==bundle.RootUri&&(source.DocumentUri is null||source.DocumentUri==bundle.RootUri)&&source.Pointer=="/components/schemas/__maintained_"+source.Id.ToString("N");
    internal static (Uri Uri,string Pointer) Physical(ContractBundle bundle,ContractReferenceRegistry graph,ContractDefinitionSource source)
    {
        if(source.DocumentUri is not null){if(!bundle.Documents.Any(d=>d.Source.LogicalUri==source.DocumentUri))throw Invalid("contract_source_stale","定义物理来源不在固定包中。");return(source.DocumentUri,source.Pointer);}
        foreach(var document in bundle.Documents)try{var found=graph.Resolve(document.Source.LogicalUri,"#"+Uri.EscapeDataString(source.Pointer));if(found.ResourceUri==source.ResourceUri)return(document.Source.LogicalUri,source.Pointer);}catch(ApiException){}
        throw Invalid("contract_source_stale","定义来源位置无法核对。");
    }
    internal async Task SaveMaintenanceAsync(ApiVersion version,VersionContractState state,MaintenanceGraph graph,CancellationToken ct)
    {
        if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Maintenance writes require the catalog transaction.");CatalogService.RequireDraft(version);
        var row=await Row(version.Id,ct);row.BundleJson=ContractBundleCodec.Encode(state.Bundle);row.BundleHash=state.Bundle.Hash;row.Dialect=state.Bundle.Documents[0].Dialect.ToString();row.SourcePolicyRevision=state.PolicyRevision;
        row.SourcesJson=JsonSerializer.Serialize(state.Metadata with{Definitions=graph.Definitions,VersionDocumentHash=DocumentHash(version.OpenapiDocument)},JsonOptions);
    }
    private async Task<ApiVersionContractSources> Row(Guid id,CancellationToken ct){var row=db.Set<ApiVersionContractSources>().Local.SingleOrDefault(x=>x.ApiVersionId==id)??await db.Set<ApiVersionContractSources>().SingleOrDefaultAsync(x=>x.ApiVersionId==id,ct);if(row is null){row=new(){ApiVersionId=id};db.Add(row);}return row;}
    internal static void Set(JsonNode root,string pointer,JsonNode replacement)
    {
        var tokens=pointer.Split('/').Skip(1).Select(x=>x.Replace("~1","/",StringComparison.Ordinal).Replace("~0","~",StringComparison.Ordinal)).ToArray();if(tokens.Length==0)throw Invalid("contract_source_stale","维护定义不能替换整份来源根。");JsonNode current=root;
        foreach(var key in tokens[..^1])current=current is JsonObject obj?obj[key]??throw Invalid("contract_source_stale","定义来源路径不存在。"):current is JsonArray array&&int.TryParse(key,out var index)&&index>=0&&index<array.Count?array[index]??throw Invalid("contract_source_stale","定义来源路径不存在。"):throw Invalid("contract_source_stale","定义来源路径不存在。");
        if(current is JsonObject target)target[tokens[^1]]=replacement.DeepClone();else if(current is JsonArray list&&int.TryParse(tokens[^1],out var last)&&last>=0&&last<list.Count)list[last]=replacement.DeepClone();else throw Invalid("contract_source_stale","定义来源路径不存在。");
    }
    internal static ApiException Invalid(string code,string message)=>new(422,code,message);
}

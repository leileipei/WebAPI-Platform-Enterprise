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
public sealed class SchemaValidationService(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,VersionContractSourceService sources,ContractProcessRunner runner)
{
    public async Task<SchemaValidationView> ValidateAsync(Guid versionId,ValidateSchemaRequest request,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.VersionAsync(versionId,ct);if(!await auth.CanAsync(actor,"api.schema.read",new("version",versionId,scope),ct))throw ScopeResolver.Missing();
        // Read a coherent revision and its definitions without writes or the command/audit pipeline.
        await using var transaction=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var version=await db.Set<ApiVersion>().AsNoTracking().SingleAsync(x=>x.Id==versionId,ct);RevisionTag.Require(RevisionTag.Format(request.ExpectedVersionRevision),version.Revision);
        if(request.ExtraFields?.Count>0||request.Direction is not("request" or "response")||request.FormatMode is not("Annotation" or "Strict")||request.SchemaId is not null&&request.ParameterId is not null||request.SchemaId is null&&request.ParameterId is null&&!request.Draft||request.Draft&&request.DraftSchemaJson is null||!request.Draft&&request.DraftSchemaJson is not null)throw Invalid("invalid_schema_selector","定义选择器、草稿或校验选项不合法。");
        var definitions=(await sources.DefinitionsAsync(versionId,ct)).ToList();var id=request.SchemaId??request.ParameterId??Guid.NewGuid();var kind=request.ParameterId is not null?"parameter":"schema";var selected=definitions.SingleOrDefault(x=>x.Id==id&&x.Kind==kind);
        if((request.SchemaId is not null||request.ParameterId is not null)&&selected is null)throw Invalid("foreign_schema_selector","定义不属于当前版本。");
        var limits=new ContractLimits();if(Encoding.UTF8.GetByteCount(request.ExampleJson)>limits.MaxExampleBytes)throw new ApiException(413,"schema_example_budget","示例超过允许字节数。");JsonNode? example;try{example=JsonNode.Parse(request.ExampleJson,documentOptions:new(){MaxDepth=limits.MaxDepth});}catch(JsonException){throw Invalid("invalid_json_example","示例必须为合法JSON。");}
        var schemaJson=request.DraftSchemaJson??selected!.Schema;
        if(request.SchemaId is Guid schemaId){var media=await db.Set<ApiSchema>().Where(x=>x.Id==schemaId).Select(x=>x.ContentType).SingleAsync(ct);if(!IsJsonMedia(media))return new(version.Revision,Incomplete("schema_non_json_media","当前示例验证仅覆盖JSON媒体类型。",request,schemaJson));}
        try{
            var state=await sources.LoadAsync(version,ct);
            foreach(var savedOrigin in state.Metadata.Definitions){var definition=definitions.SingleOrDefault(x=>x.Id==savedOrigin.Id&&x.Kind==savedOrigin.Kind);if(definition is not null&&savedOrigin.SchemaHash!=VersionContractSourceService.DocumentHash(definition.Schema))throw Invalid("contract_definition_stale","维护定义与来源摘要不一致。");}
            if(request.Draft){definitions.RemoveAll(x=>x.Id==id&&x.Kind==kind);definitions.Add(selected is null?new(id,kind,"Draft",schemaJson,false):selected with{Schema=schemaJson});}
            var graph=sources.BuildGraph(state,definitions,ct);var origin=graph.Definitions.Single(x=>x.Id==id&&x.Kind==kind);var parsed=new ContractReferenceRegistry(graph.Bundle,limits).Resolve(origin.DocumentUri??origin.ResourceUri,"#"+Uri.EscapeDataString(origin.Pointer)).Node;
            var response=await runner.RunAsync(ContractProcessProtocol.SchemaRequest(new(graph.Bundle,parsed,example,request.Direction,request.FormatMode,true,origin.DocumentUri??origin.ResourceUri,origin.Pointer)),TimeSpan.FromSeconds(5),ct);
            var result=response.Result?.Deserialize<SchemaValidationResult>(ContractProcessProtocol.JsonOptions)??Incomplete(response.Issues.FirstOrDefault()?.Code??"schema_evaluation_incomplete","Schema校验未完成。",request,schemaJson);
            return new(version.Revision,result);
        }catch(ApiException e){return new(version.Revision,Incomplete(e.Code,e.Message,request,schemaJson));}catch(JsonException){return new(version.Revision,Incomplete("invalid_schema_json","维护定义JSON无法读取。",request,schemaJson));}
    }
    internal async Task ValidateGraphAsync(MaintenanceGraph graph,CancellationToken ct)
    {
        var registry=new ContractReferenceRegistry(graph.Bundle,new());var missing=new List<ContractIssue>();foreach(var reference in registry.References)try{registry.Resolve(reference.ResourceUri,reference.Reference);}catch(ApiException){missing.Add(new("missing_contract_reference",reference.Pointer,"引用目标不存在于当前维护图。"));if(missing.Count==500)break;}
        if(missing.Count>0)throw new ApiException(422,"invalid_schema_reference","Schema包含失效引用。"){Issues=missing};
        if(graph.Definitions.Count==0)return;var origin=graph.Definitions[0];var node=registry.Resolve(origin.DocumentUri??origin.ResourceUri,"#"+Uri.EscapeDataString(origin.Pointer)).Node;
        var response=await runner.RunAsync(ContractProcessProtocol.SchemaRequest(new(graph.Bundle,node,null,"request","Annotation",false,origin.DocumentUri??origin.ResourceUri,origin.Pointer)),TimeSpan.FromSeconds(5),ct);
        if(response.Status!="Valid"){
            var result=response.Result?.Deserialize<SchemaValidationResult>(ContractProcessProtocol.JsonOptions);var issues=result?.Issues.Select(x=>new ContractIssue(x.Code,x.SchemaPointer,x.Message,x.Line,x.Column)).Concat(result.CoverageIssues).ToArray()??response.Issues.ToArray();
            throw new ApiException(422,response.Status=="Invalid"?"invalid_schema":"schema_validation_incomplete","Schema结构或引用未通过校验；示例匹配不作为保存门禁。"){Issues=issues};
        }
    }
    internal void RequireNoComponentImpact(VersionContractState state,IReadOnlyList<MaintainedDefinition> old,IReadOnlyList<MaintainedDefinition> next,CancellationToken ct)
    {
        var changed=old.Where(x=>x.Component&&!next.Any(n=>n.Id==x.Id&&n.Kind==x.Kind&&n.Component&&n.Name==x.Name)).ToArray();if(changed.Length==0)return;
        var graph=sources.BuildGraph(state,next,ct);var registry=new ContractReferenceRegistry(graph.Bundle,new());var affected=new List<ContractIssue>();
        foreach(var reference in registry.References){ct.ThrowIfCancellationRequested();try{var target=registry.Resolve(reference.ResourceUri,reference.Reference);if(changed.Any(x=>target.Pointer=="/components/schemas/"+ContractDocumentReader.Escape(x.Name)||target.Pointer.StartsWith("/components/schemas/"+ContractDocumentReader.Escape(x.Name)+"/",StringComparison.Ordinal)))affected.Add(new("schema_reference_impact",reference.Pointer,"删除或改名的组件仍被引用，请显式处理引用。"));}catch(ApiException){}
            if(affected.Count==500)break;
        }
        if(affected.Count>0)throw new ApiException(422,"schema_reference_impact","组件删除或改名影响现有引用。"){Issues=affected};
    }
    private static bool IsJsonMedia(string media){var type=media.Split(';')[0].Trim();return type.Equals("application/json",StringComparison.OrdinalIgnoreCase)||type.EndsWith("+json",StringComparison.OrdinalIgnoreCase);}
    private static SchemaValidationResult Incomplete(string code,string message,ValidateSchemaRequest request,string schema)=>new("Incomplete","unknown",CatalogService.Hash(schema),CatalogService.Hash(request.ExampleJson),request.FormatMode,[],[new(code,"",message)]);
    private static ApiException Invalid(string code,string message)=>new(422,code,message);
}

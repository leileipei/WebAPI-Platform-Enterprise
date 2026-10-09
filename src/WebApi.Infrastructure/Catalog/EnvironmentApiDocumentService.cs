using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Catalog;
public sealed class EnvironmentApiDocumentService(EnvironmentApiAddressService addresses,ScopeResolver scopes,AuthorizationService auth,VersionContractSourceService sources,WebApiDbContext db)
{
    public async Task<JsonDocument> BuildAsync(Guid envId,Guid apiId,Guid? versionId,AccessView view,ActorContext actor,CancellationToken ct=default)
    {
        var selection=await addresses.SelectAsync(envId,apiId,versionId,view,actor,ct);var scope=await scopes.EnvironmentAsync(envId,ct);
        if(!await auth.CanAsync(actor,"api.schema.read",new("api",apiId,scope),ct))throw ScopeResolver.Missing();
        var e=selection.Environment;if(e.GatewayPublicUrl is null)throw new ApiException(409,"access_address_unconfigured","请先配置环境公开入口。");
        if(selection.Versions.Count!=1||selection.Routes.Count==0)throw Mapping();var version=selection.Versions.Single();
        if(version.Version.OpenapiDocument is null)throw new ApiException(409,"contract_document_unavailable","所选版本尚无可映射的 OpenAPI 契约。");
        var current=await db.Set<ApiVersion>().AsNoTracking().SingleAsync(x=>x.Id==version.Version.Id,ct);
        if(view==AccessView.Running&&current.Revision!=version.Version.Revision)throw new ApiException(422,"historical_contract_changed","运行契约来源修订与冻结版本不一致。");
        var state=await sources.LoadAsync(current,ct);
        var definitions=version.Parameters.Select(x=>new MaintainedDefinition(x.Id,"parameter",x.Name,x.Schema??JsonSerializer.Serialize(new{type=x.DataType}),false)).Concat(version.Schemas.Select(x=>new MaintainedDefinition(x.Id,"schema",x.Name,x.SchemaJson,x.SchemaType=="component"))).ToArray();
        var graph=sources.BuildGraph(state,definitions,ct);var sourceRoot=graph.Bundle.Documents.Single(x=>x.Source.LogicalUri==graph.Bundle.RootUri).Root as JsonObject??throw Mapping();
        var root=JsonNode.Parse(version.Version.OpenapiDocument) as JsonObject??throw Mapping();
        // A fixed source bundle may contain many operations. Retain only the selected version's paths and methods.
        var selectedPaths=root["paths"] as JsonObject??throw Mapping();
        foreach(var(path,node)in selectedPaths.ToArray())
        {
            if(node is not JsonObject item||sourceRoot["paths"]?[path] is not JsonObject maintained)throw Mapping();
            foreach(var method in new[]{"get","post","put","patch","delete","head","options","trace"})if(item.ContainsKey(method))item[method]=maintained[method]?.DeepClone()??throw Mapping();
            if(item.ContainsKey("parameters"))item["parameters"]=maintained["parameters"]?.DeepClone();
        }
        if(sourceRoot["components"] is JsonNode components)root["components"]=components.DeepClone();
        var reader=new ContractDocumentReader();var filtered=reader.Read(new(graph.Bundle.RootUri,root.ToJsonString(),"json"),new(),ct);
        var bundle=ContractBundleCodec.Create(graph.Bundle.RootUri,graph.Bundle.Documents.Select(d=>d.Source.LogicalUri==graph.Bundle.RootUri?filtered:d).ToArray());
        var operations=new OpenApiOperationParser(bundle).Operations();
        if(operations.Count!=selection.Routes.Count||selection.Routes.GroupBy(r=>(r.Method,r.Path)).Any(g=>g.Count()!=1)||operations.Any(o=>selection.Routes.Count(r=>r.Method==o.Method&&r.Path==o.Path)!=1))throw Mapping();
        root["servers"]=new JsonArray(new JsonObject{["url"]=e.GatewayPublicUrl+(e.BasePath=="/"?"":e.BasePath)});
        foreach(var(_,path)in (JsonObject)root["paths"]!)if(path is JsonObject item){item.Remove("servers");foreach(var(_,operation)in item)if(operation is JsonObject op)op.Remove("servers");}
        root["x-environment-view"]=view.ToString().ToLowerInvariant();root["x-access-address-revision"]=e.AccessAddressRevision;
        return JsonDocument.Parse(root.ToJsonString());
    }
    private static ApiException Mapping()=>new(422,"ambiguous_route_contract","契约操作与该视图的实际路由无法一一匹配；请明确版本及方法路径，不生成不完整文档。");
}

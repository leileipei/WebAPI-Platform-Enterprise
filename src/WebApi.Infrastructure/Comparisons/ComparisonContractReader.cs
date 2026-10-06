using System.Text;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using WebApi.Infrastructure.Catalog;
namespace WebApi.Infrastructure.Comparisons;
internal static class ComparisonContractReader
{
    internal static ContractBundle Read(ContractVersionInput input,ContractLimits limits,CancellationToken ct,Action<string,string,string> issue)
    {
        var reader=new ContractDocumentReader();
        if(input.Sources is ComparisonSourceInput sources){
            if(sources.Documents.Count is <1||sources.Documents.Count>limits.MaxResources)throw Invalid();
            var source=sources.Documents.SingleOrDefault(x=>x.LogicalUri==sources.RootUri)??throw Invalid();var root=reader.Read(source,limits,ct);var docs=new List<ContractDocument>{root};
            foreach(var document in sources.Documents.Where(x=>x.LogicalUri!=sources.RootUri))docs.Add(reader.ReadResource(document,limits,root.Dialect,ct));
            var bundle=ContractBundleCodec.Create(sources.RootUri,docs,limits);if(bundle.Hash!=sources.BundleHash)throw Invalid();
            if(sources.Metadata.VersionDocumentHash is string selectedHash){if(selectedHash!=VersionContractSourceService.DocumentHash(input.Version.OpenapiDocument))throw Invalid();}
            else if(input.Version.OpenapiDocument is string original&&Canonical(original)!=Canonical(root.CanonicalJson))throw Invalid();
            return bundle;
        }
        var uri=new Uri($"https://contracts.invalid/versions/{input.Version.Id:D}/openapi.json");var raw=input.Version.OpenapiDocument;
        if(raw is null){
            issue("missing_document","/openapi","缺少 OpenAPI 文档，仅比较维护定义。");
            raw=new JsonObject{["openapi"]=input.Version.Dialect=="oas-3.0"?"3.0.3":"3.1.0",["info"]=new JsonObject{["title"]="Maintained contract",["version"]=input.Version.Version},["paths"]=new JsonObject()}.ToJsonString();
        }
        if(Encoding.UTF8.GetByteCount(raw)>limits.MaxDocumentBytes)throw Invalid();
        // Older persisted contracts omitted annotation-only info. Add it solely in
        // the private comparison graph; input bytes and legacy fingerprints stay intact.
        var parsed=new ComparisonContext(new(MaxDepth:limits.MaxDepth,MaxNodes:limits.MaxNodes),ct);var node=parsed.Parse(raw,"input","/");if(parsed.Invalid||node is not JsonObject obj)throw Invalid();
        if(!obj.ContainsKey("info"))obj["info"]=new JsonObject{["title"]="Legacy contract",["version"]=input.Version.Version};
        var documentRoot=reader.Read(new(uri,obj.ToJsonString(),"json"),limits,ct);return ContractBundleCodec.Create(uri,[documentRoot],limits);
    }
    private static string Canonical(string text)=>ContractNormalizer.Canonical(JsonNode.Parse(text,documentOptions:new(){MaxDepth=64}));
    internal static HashSet<string>? SelectedOperations(ContractVersionInput input)
    {
        if(input.Sources is null||input.Version.OpenapiDocument is null)return null;
        var selected=JsonNode.Parse(input.Version.OpenapiDocument,documentOptions:new(){MaxDepth=64}) as JsonObject??throw Invalid();var keys=new HashSet<string>(StringComparer.Ordinal);
        foreach(var(path,node)in selected["paths"]!.AsObject())if(node is JsonObject item)foreach(var method in new[]{"get","post","put","patch","delete","head","options","trace"})if(item.ContainsKey(method)||item.ContainsKey("$ref"))keys.Add(method.ToUpperInvariant()+" "+path);
        return keys;
    }
    private static ApiException Invalid()=>new(422,"invalid_comparison_source","固定来源内容、根文档或预算无法核对。");
}

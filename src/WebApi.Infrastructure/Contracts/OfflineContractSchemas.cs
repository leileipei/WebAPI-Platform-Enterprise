using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Pointer;
using Json.Schema;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
namespace WebApi.Infrastructure.Contracts;

// Each evaluation owns its graphs, registrations and build cache. No network fallback exists.
internal sealed class OfflineContractSchemas
{
    private readonly ContractReferenceRegistry graph;
    private readonly ContractDialect dialect;
    private readonly string direction;
    private readonly string formatMode;
    private readonly CancellationToken token;
    private readonly BuildOptions options;
    private readonly BuildContext template;
    private readonly Dictionary<string,JsonSchemaNode> cache=new(StringComparer.Ordinal);
    private readonly List<ContractIssue> coverage;
    private static readonly HashSet<Uri> BuiltinMetas=[MetaSchemas.Draft202012Id,MetaSchemas.Applicator202012Id,MetaSchemas.Content202012Id,MetaSchemas.Core202012Id,MetaSchemas.FormatAnnotation202012Id,MetaSchemas.FormatAssertion202012Id,MetaSchemas.Metadata202012Id,MetaSchemas.Unevaluated202012Id,MetaSchemas.Validation202012Id];

    public OfflineContractSchemas(ContractReferenceRegistry graph,ContractDialect dialect,string direction,string formatMode,List<ContractIssue> coverage,CancellationToken token)
    {
        this.graph=graph;this.dialect=dialect;this.direction=direction;this.formatMode=formatMode;this.coverage=coverage;this.token=token;
        var registry=new SchemaRegistry {Fetch=(_,_)=>throw new ApiException(422,"missing_contract_reference","引用不在固定来源包中。")};
        options=new(){SchemaRegistry=registry,VocabularyRegistry=new(),DialectRegistry=new(),Dialect=Dialect.Draft202012};
        foreach(var resource in graph.Resources)registry.Register(new FixedDocument(this,resource.ResourceUri));
        var seed=JsonSchema.FromText("{\"type\":\"object\"}",options,new Uri("https://evaluation.invalid/build-context"));
        template=BuildContext.From(seed.Root.Keywords[0]);
    }
    public JsonSchema Build(ResolvedContractNode selected)
    {
        // Build each schema/resource root once, so anchors are registered before any reference
        // resolution and two pointers to the same anchor never register it twice.
        foreach(var root in graph.SchemaRoots)Find(root);
        var origin=graph.Describe(selected.Node);
        var reference=origin.ResourceUri.AbsoluteUri+"#"+Uri.EscapeDataString(RelativePointer(origin)).Replace("%2F","/",StringComparison.Ordinal);
        return JsonSchema.FromText(new JsonObject{["$ref"]=reference}.ToJsonString(),options,new Uri("https://evaluation.invalid/entry"));
    }
    private string RelativePointer(ResolvedContractNode node)
    {
        var resource=graph.Resources.Single(x=>x.ResourceUri==node.ResourceUri);
        if(!node.Pointer.StartsWith(resource.Pointer,StringComparison.Ordinal))throw new ApiException(422,"invalid_schema_location","Schema位置与资源不一致。");
        return node.Pointer[resource.Pointer.Length..];
    }
    private JsonSchemaNode Find(ResolvedContractNode selected)
    {
        token.ThrowIfCancellationRequested();var origin=graph.Describe(selected.Node);var pointer=RelativePointer(origin);var key=origin.ResourceUri.AbsoluteUri+"#"+pointer;
        if(cache.TryGetValue(key,out var existing))return existing;
        var prepared=DialectAdapter.PrepareSchema(selected.Node,dialect,direction,applyDirection:false);
        coverage.AddRange(prepared.Issues);
        if(prepared.Issues.Count>0)throw new ApiException(422,"unsupported_schema_semantics","Schema包含未登记或不支持的语义。");
        var element=JsonSerializer.SerializeToElement(prepared.Node);
        if(!MetaSchemas.Draft202012.Evaluate(element,new(){OutputFormat=OutputFormat.Flag}).IsValid)throw new InvalidSchemaShapeException();
        ApplyDirection(selected.Node,prepared.Node);
        var lowered=Lower(selected.Node,prepared.Node,true);
        var context=template with {BaseUri=origin.ResourceUri,LocalSchema=JsonSerializer.SerializeToElement(lowered),Dialect=Dialect.Draft202012};
#pragma warning disable CS0618
        context.PathFromResourceRoot=JsonPointer.Parse(pointer);
        var built=JsonSchema.BuildNode(context);
        built.PathFromResourceRoot=JsonPointer.Parse(pointer);
        Cache(built,origin.ResourceUri);
#pragma warning restore CS0618
        return built;
    }
    private JsonNode Lower(JsonNode original,JsonNode prepared,bool root)
    {
        token.ThrowIfCancellationRequested();
        if(original is not JsonObject source||prepared is not JsonObject transformed)return prepared.DeepClone();
        var identity=graph.Describe(original);
        if(!root&&dialect==ContractDialect.Oas31&&source.ContainsKey("$id"))return new JsonObject{["$ref"]=identity.ResourceUri.AbsoluteUri};
        var result=(JsonObject)transformed.DeepClone();result.Remove("$id");
        if(result.ContainsKey("$schema"))result["$schema"]="https://json-schema.org/draft/2020-12/schema";
        if(formatMode=="Strict"&&source["format"] is JsonValue format&&format.TryGetValue<string>(out var formatName)&&!ContractFormats.Registered.Contains(formatName))throw new ApiException(422,"unsupported_schema_format","Strict模式中该格式未登记。");
        foreach(var keyword in new[]{"$ref","$dynamicRef"})if(result[keyword] is JsonValue reference&&reference.TryGetValue<string>(out var text)) {
            var target=new Uri(identity.ResourceUri,text);
            if(!BuiltinMetas.Contains(new Uri(target.AbsoluteUri.Split('#')[0])))graph.Resolve(identity.ResourceUri,text);
            result[keyword]=target.AbsoluteUri;
        }
        foreach(var(key,value)in transformed) {
            if(SchemaNavigation.MapKeywords.Contains(key)&&value is JsonObject map&&source[key] is JsonObject oldMap){var replacement=new JsonObject();foreach(var(name,child)in map)replacement[name]=Lower(oldMap[name]!,child!,false);result[key]=replacement;}
            else if(SchemaNavigation.ArrayKeywords.Contains(key)&&value is JsonArray list&&source[key] is JsonArray oldList){var replacement=new JsonArray();for(var i=0;i<list.Count;i++)replacement.Add(Lower(oldList[i]!,list[i]!,false));result[key]=replacement;}
            else if(SchemaNavigation.SingleKeywords.Contains(key)&&value is JsonNode child&&source[key] is JsonNode oldChild)result[key]=Lower(oldChild,child,false);
        }
        return result;
    }
    private void ApplyDirection(JsonNode original,JsonNode prepared)
    {
        if(original is not JsonObject source||prepared is not JsonObject target)return;
        if(target["required"] is JsonArray required&&source["properties"] is JsonObject properties)for(var i=required.Count-1;i>=0;i--) {
            var name=ContractDocumentReader.Text(required[i]);
            if(properties[name] is JsonNode property&&Annotated(property,direction=="request"?"readOnly":"writeOnly",new(ReferenceEqualityComparer.Instance)))required.RemoveAt(i);
        }
        foreach(var(key,value)in target.ToArray()) {
            if(SchemaNavigation.MapKeywords.Contains(key)&&value is JsonObject map&&source[key] is JsonObject oldMap) {
                foreach(var(name,mapChild)in map)if(mapChild is not null&&oldMap[name] is JsonNode oldMapChild)ApplyDirection(oldMapChild,mapChild);
            } else if(SchemaNavigation.ArrayKeywords.Contains(key)&&value is JsonArray list&&source[key] is JsonArray oldList) {
                for(var i=0;i<list.Count;i++)if(list[i] is JsonNode arrayChild&&oldList[i] is JsonNode oldArrayChild)ApplyDirection(oldArrayChild,arrayChild);
            } else if(SchemaNavigation.SingleKeywords.Contains(key)&&value is JsonNode singleChild&&source[key] is JsonNode oldSingleChild)ApplyDirection(oldSingleChild,singleChild);
        }
    }
    private bool Annotated(JsonNode node,string keyword,HashSet<JsonNode> visited)
    {
        token.ThrowIfCancellationRequested();if(!visited.Add(node)||node is not JsonObject schema)return false;
        if(visited.Count>50000)throw new ApiException(422,"contract_budget","方向注解引用图超过预算。");
        var reference=schema["$ref"];
        if(!(dialect==ContractDialect.Oas30&&reference is not null)&&schema[keyword] is JsonValue flag&&flag.TryGetValue<bool>(out var enabled)&&enabled)return true;
        if(reference is JsonValue text&&text.TryGetValue<string>(out var value))return Annotated(graph.Resolve(graph.Describe(node).ResourceUri,value).Node,keyword,visited);
        return false;
    }
    private void Cache(JsonSchemaNode node,Uri uri)
    {
#pragma warning disable CS0618
        cache.TryAdd(uri.AbsoluteUri+"#"+node.PathFromResourceRoot,node);
#pragma warning restore CS0618
        foreach(var keyword in node.Keywords)foreach(var child in keyword.Subschemas)Cache(child,uri);
    }
    private sealed class FixedDocument(OfflineContractSchemas owner,Uri uri):IBaseDocument
    {
        public Uri BaseUri=>uri;
        public JsonSchemaNode? FindSubschema(JsonPointer pointer,BuildContext context)=>owner.Find(owner.graph.Resolve(uri,"#"+Uri.EscapeDataString(pointer.ToString())));
    }
}
internal sealed class InvalidSchemaShapeException:Exception;

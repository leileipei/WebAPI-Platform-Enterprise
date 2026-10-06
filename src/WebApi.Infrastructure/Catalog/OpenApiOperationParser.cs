using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using WebApi.Domain.Routing;
namespace WebApi.Infrastructure.Catalog;
internal sealed record ParsedDefinitionOrigin(string Kind,int Index,Uri ResourceUri,string Pointer);
internal sealed record ParsedOperation(string Id,string Method,string Path,string Summary,string SuggestedCode,string Document,IReadOnlyList<SaveParameterRequest> Parameters,IReadOnlyList<SaveSchemaRequest> Schemas,IReadOnlyList<string> Warnings,IReadOnlyList<ParsedDefinitionOrigin> Origins);
internal sealed class OpenApiOperationParser
{
    private readonly JsonObject root;
    private readonly ContractReferenceRegistry registry;
    private readonly Uri rootUri;
    private static readonly string[] methods=["get","post","put","patch","delete","head","options","trace"];
    public static ContractBundle Read(string source,ContractLimits? limits=null)
    {
        limits??=new();var uri=new Uri("https://import.invalid/openapi");var doc=new ContractDocumentReader().Read(new(uri,source,"auto"),limits,default);return ContractBundleCodec.Create(uri,[doc],limits);
    }
    public OpenApiOperationParser(string source):this(Read(source)){}
    public OpenApiOperationParser(ContractBundle bundle,ContractLimits? limits=null)
    {
        rootUri=bundle.RootUri;registry=new(bundle,limits??new());root=registry.Resolve(bundle.RootUri,"").Node as JsonObject??throw Invalid("OpenAPI必须是对象。");
    }
    private static string? Text(JsonNode? node)=>node is JsonValue v&&v.TryGetValue<string>(out var s)?s:null;
    private static bool Flag(JsonNode? node)=>node is JsonValue v&&v.TryGetValue<bool>(out var b)&&b;
    private static ApiException Invalid(string message)=>new(422,"invalid_openapi",message);
    public IReadOnlyList<(string Id,string Method,string Path)> Operations()
    {
        var result=new List<(string,string,string)>();var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var (path,item) in (JsonObject)root["paths"]!) {
            var entry=Object(item,"Path Item");
            foreach(var method in methods) if(entry[method] is JsonObject operation) {
                var id=Text(operation["operationId"])??method+"_"+path;if(string.IsNullOrWhiteSpace(id)||id.Length>256||!ids.Add(id))throw Invalid("Operation ID重复或不合法。");result.Add((id,method.ToUpperInvariant(),path));
            }
        }
        if(result.Count is <1 or >1000)throw Invalid("必须选择包含1到1000个可识别Operation的文档。");return result;
    }
    public ParsedOperation Parse(string operationId)
    {
        var key=Operations().SingleOrDefault(x=>x.Id==operationId);if(key.Id is null)throw Invalid("选中的Operation不存在。");RouteNormalizer.Normalize(key.Path);
        var path=Object(root["paths"]![key.Path],"Path Item");var raw=(JsonObject)path[key.Method.ToLowerInvariant()]!;var warnings=new List<string>();
        if(raw["security"] is not null||root["security"] is not null)warnings.Add("OpenAPI security保留原文；路由默认使用API Key，企业授权需另行配置并发布。");
        if(raw["callbacks"] is not null||root["webhooks"] is not null)warnings.Add("callbacks/webhooks保留原文，不自动创建普通路由。");
        var parameters=new Dictionary<string,SaveParameterRequest>(StringComparer.Ordinal);var parameterOrigins=new Dictionary<string,ResolvedContractNode>(StringComparer.Ordinal);var schemaOrigins=new List<ResolvedContractNode>();
        void Parameters(JsonNode? node) {
            if(node is null)return;if(node is not JsonArray list)throw Invalid("parameters必须是数组。");var local=new HashSet<string>();
            foreach(var p in list) {
                var item=Object(p,"Parameter");var location=Text(item["in"]);var name=Text(item["name"]);
                if(location is not("path" or "query" or "header" or "cookie")||string.IsNullOrWhiteSpace(name)||name.Length>128||location=="path"&&!Flag(item["required"]))throw Invalid("参数位置、名称或path必填属性不合法。");
                var code=location+":"+(location=="header"?name.ToLowerInvariant():name);if(!local.Add(code))throw new ApiException(409,"duplicate_parameter","同一Operation重复定义参数。");
                JsonNode schema;JsonNode? example=item["example"];
                if(item.TryGetPropertyValue("content",out var content)) {
                    if(content is not JsonObject media)throw Invalid("parameter.content必须是对象。");
                    if(item["schema"] is not null||media.Count!=1||media.First().Value is not JsonObject definition||definition["schema"] is not JsonNode contentSchema)throw Invalid("parameter.content必须含单个媒体类型及Schema，不能混用schema。");
                    schema=contentSchema;example=definition["example"];warnings.Add($"参数{name}的content媒体类型保留原文，网关不执行媒体序列化转换。");
                } else schema=item["schema"]??new JsonObject{["type"]="string"};
                CheckSchema(schema);parameterOrigins[code]=Origin(schema);var type=schema is JsonObject schemaObject?Text(schemaObject["type"])??"unknown":"unknown";
                if(item["style"] is not null||item["explode"] is not null||item["allowReserved"] is not null)warnings.Add($"参数{name}的序列化选项保留原文，不自动改变Runtime转换。");
                parameters[code]=new(null,location,name,type,Flag(item["required"]),schema.ToJsonString(),Text(item["description"]),example?.ToJsonString());
            }
        }
        Parameters(path["parameters"]);Parameters(raw["parameters"]);var schemas=new List<SaveSchemaRequest>();
        void Content(JsonNode? content,string kind,string name,int? status) {
            if(content is null)return;if(content is not JsonObject media)throw Invalid("content必须是对象。");
            foreach(var(contentType,definition)in media) {
                if(contentType.Length>128||definition is not JsonObject value)throw Invalid("Media Type不合法。");var schema=value["schema"];if(schema is null){warnings.Add($"{name}的{contentType}未定义Schema。");continue;}
                CheckSchema(schema);schemaOrigins.Add(Origin(schema));schemas.Add(new(null,kind,name,status,contentType,schema.ToJsonString(),value["example"]?.ToJsonString()));
                if(value["examples"] is not null)warnings.Add($"{name}的多个examples及引用保留在固定来源，单例字段保留example。");
            }
        }
        if(raw["requestBody"] is JsonNode bodyNode){var body=Object(bodyNode,"Request Body");Content(body["content"],"request","Request",null);}
        var responses=raw["responses"] as JsonObject??throw Invalid("Operation必须定义responses。");if(responses.Count==0)throw Invalid("responses不能为空。");
        foreach(var(status,responseNode)in responses) {
            int? code=int.TryParse(status,out var parsed)&&parsed is >=100 and <=599?parsed:null;if(code is null&&status!="default"&&!Regex.IsMatch(status,"^[1-5][Xx]{2}$"))throw Invalid("响应状态码不合法。");
            if(code is null&&status!="default")warnings.Add($"响应{status}保留范围状态码，不转为单一HTTP状态。");var response=Object(responseNode,"Response");Content(response["content"],"response","Response-"+status,code);
            if(response["headers"] is JsonObject headers)foreach(var(_,headerNode)in headers){var header=Object(headerNode,"Header");if(header["schema"] is JsonNode h)CheckSchema(h);if(header["content"] is JsonObject hc)foreach(var(_,v)in hc)if(v is JsonObject hm&&hm["schema"] is JsonNode hs)CheckSchema(hs);warnings.Add("响应Header定义保留原文，不自动创建请求参数。");}
        }
        if(root["components"]?["schemas"] is JsonObject components)foreach(var(name,schema)in components.OrderBy(p=>p.Key,StringComparer.Ordinal)) {
            if(name.Length>128||schema is null)throw Invalid("组件Schema名称或定义不合法。");schemaOrigins.Add(Origin(schema));schemas.Add(new(null,"component",name,null,"application/json",schema.ToJsonString()));
        }
        var filtered=(JsonObject)root.DeepClone();var selectedPath=new JsonObject{[key.Method.ToLowerInvariant()]=raw.DeepClone()};if(path["parameters"] is JsonNode common)selectedPath["parameters"]=common.DeepClone();filtered["paths"]=new JsonObject{[key.Path]=selectedPath};
        var suggested=Regex.Replace(key.Id,"[^A-Za-z0-9_.-]","_");if(suggested.Length==0||!char.IsAsciiLetterOrDigit(suggested[0]))suggested="API_"+suggested;if(suggested.Length>64)suggested=suggested[..48]+"_"+CatalogService.Hash(key.Id)[..12];
        return new(key.Id,key.Method,key.Path,Text(raw["summary"])??key.Id,suggested,filtered.ToJsonString(),parameters.Values.ToArray(),schemas,warnings.Distinct().ToArray(),parameterOrigins.Values.Select((x,i)=>new ParsedDefinitionOrigin("parameter",i,x.ResourceUri,x.Pointer)).Concat(schemaOrigins.Select((x,i)=>new ParsedDefinitionOrigin("schema",i,x.ResourceUri,x.Pointer))).ToArray());
    }
    private ResolvedContractNode Origin(JsonNode schema)
    {
        try{return registry.Describe(schema);}catch(ApiException){return new(rootUri,"",schema);}
    }
    private JsonObject Object(JsonNode? node,string label)
    {
        var seen=new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);var depth=0;
        while(node is JsonObject o&&o["$ref"] is JsonNode refNode) {
            if(++depth>64||!seen.Add(node))throw new ApiException(422,"unsupported_reference","非Schema引用循环或超过深度预算。");var text=Text(refNode)??throw Invalid("引用必须为字符串。");node=registry.Resolve(registry.Describe(node).ResourceUri,text).Node;
        }
        return node as JsonObject??throw Invalid(label+"必须为对象。");
    }
    private void CheckSchema(JsonNode schema)
    {
        var visited=new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);var count=0;
        void Visit(JsonNode node,int depth) {
            if(!visited.Add(node))return;if(++count>50000||depth>64)throw new ApiException(422,"contract_budget","Schema引用图超过预算。");
            if(node is JsonValue b&&b.TryGetValue<bool>(out _))return;if(node is not JsonObject o)throw Invalid("Schema必须是对象或boolean。");
            foreach(var keyword in new[]{"$ref","$dynamicRef"})if(o[keyword] is JsonNode reference)Visit(registry.Resolve(registry.Describe(node).ResourceUri,Text(reference)??throw Invalid("引用必须为字符串。")).Node,depth+1);
            foreach(var(k,v)in o){if(SchemaNavigation.MapKeywords.Contains(k)&&v is JsonObject map)foreach(var(_,c)in map){if(c is null)throw Invalid("Schema不能为空。");Visit(c,depth+1);}else if(SchemaNavigation.ArrayKeywords.Contains(k)&&v is JsonArray list)foreach(var c in list){if(c is null)throw Invalid("Schema不能为空。");Visit(c,depth+1);}else if(SchemaNavigation.SingleKeywords.Contains(k)&&v is JsonNode c)Visit(c,depth+1);}
        }
        Visit(schema,0);
    }
}

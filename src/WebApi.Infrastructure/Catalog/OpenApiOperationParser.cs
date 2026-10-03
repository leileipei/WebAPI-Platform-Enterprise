using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.Domain.Routing;
namespace WebApi.Infrastructure.Catalog;
internal sealed record ParsedOperation(string Id,string Method,string Path,string Summary,string SuggestedCode,string Document,IReadOnlyList<SaveParameterRequest> Parameters,IReadOnlyList<SaveSchemaRequest> Schemas,IReadOnlyList<string> Warnings);
internal sealed class OpenApiOperationParser
{
    private readonly JsonObject root;
    private static readonly string[] methods=["get","post","put","patch","delete","head","options"];
    public OpenApiOperationParser(string source)
    {
        try {root=JsonNode.Parse(source,new JsonNodeOptions(),new JsonDocumentOptions {MaxDepth=64}) as JsonObject??throw Invalid("OpenAPI必须是JSON对象。");}
        catch(JsonException) {throw Invalid("OpenAPI JSON无法解析。");}
        var version=Text(root["openapi"]);if(version is null||!version.StartsWith("3.",StringComparison.Ordinal)||root["paths"] is not JsonObject) throw Invalid("首期仅接受OpenAPI 3.x JSON及paths对象。");
    }
    private static string? Text(JsonNode? node)=>node is JsonValue v&&v.TryGetValue<string>(out var s)?s:null;
    private static bool Flag(JsonNode? node)=>node is JsonValue v&&v.TryGetValue<bool>(out var b)&&b;
    private static ApiException Invalid(string message)=>new(422,"invalid_openapi",message);
    public IReadOnlyList<(string Id,string Method,string Path)> Operations()
    {
        var result=new List<(string,string,string)>();var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var (path,item) in (JsonObject)root["paths"]!)
        {
            if(item is not JsonObject entry) throw Invalid("Path Item必须是对象。");entry=ResolvePathItem(entry,new LocalResolver(root));
            foreach(var method in methods) if(entry[method] is JsonObject operation)
            {
                var id=Text(operation["operationId"])??method+"_"+path;if(string.IsNullOrWhiteSpace(id)||id.Length>256||!ids.Add(id)) throw Invalid("Operation ID重复或不合法。");result.Add((id,method.ToUpperInvariant(),path));
            }
        }
        if(result.Count is <1 or >1000) throw Invalid("必须选择包含1到1000个可识别Operation的文档。");return result;
    }
    public ParsedOperation Parse(string operationId)
    {
        var key=Operations().SingleOrDefault(x=>x.Id==operationId);if(key.Id is null) throw Invalid("选中的Operation不存在。");RouteNormalizer.Normalize(key.Path);
        var resolver=new LocalResolver(root);var path=ResolvePathItem((JsonObject)root["paths"]![key.Path]!,resolver);var raw=(JsonObject)path[key.Method.ToLowerInvariant()]!;var warnings=new List<string>();
        if(raw["security"] is not null||root["security"] is not null) warnings.Add("OpenAPI security不会自动导入企业授权，路由默认使用API Key，授权需另行配置并发布。");
        if(raw["callbacks"] is not null) throw Invalid("首期不支持callbacks导入。");
        var parameters=new Dictionary<string,SaveParameterRequest>(StringComparer.Ordinal);
        void Parameters(JsonNode? node)
        {
            if(node is null) return;if(node is not JsonArray list) throw Invalid("parameters必须是数组。");var local=new HashSet<string>();
            foreach(var p in list)
            {
                var item=resolver.Resolve(p) as JsonObject??throw Invalid("参数必须是对象。");var location=Text(item["in"]);var name=Text(item["name"]);
                if(location is not ("path" or "query" or "header" or "cookie")||string.IsNullOrWhiteSpace(name)||name.Length>128||location=="path"&&!Flag(item["required"])) throw Invalid("参数位置、名称或path必填属性不合法。");
                var code=location+":"+(location=="header"?name.ToLowerInvariant():name);if(!local.Add(code)) throw new ApiException(409,"duplicate_parameter","同一Operation重复定义参数。");
                if(item["content"] is not null) throw Invalid("首期参数须使用schema，暂不支持parameter.content。");
                var schema=item["schema"]??new JsonObject { ["type"]="string" };if(schema is not JsonObject&&!(schema is JsonValue schemaBoolean&&schemaBoolean.TryGetValue<bool>(out _))) throw Invalid("参数Schema必须是对象或布尔Schema。");var type=schema is JsonObject schemaObject?Text(schemaObject["type"])??"unknown":"unknown";
                if(item["style"] is not null||item["explode"] is not null||item["allowReserved"] is not null) warnings.Add($"参数{name}的序列化选项保留在原文中，首期不执行这些Runtime转换。");
                parameters[code]=new(null,location,name,type,Flag(item["required"]),schema.ToJsonString(),Text(item["description"]),item["example"]?.ToJsonString());
            }
        }
        Parameters(path["parameters"]);Parameters(raw["parameters"]);
        var schemas=new List<SaveSchemaRequest>();
        void Content(JsonNode? content,string kind,string name,int? status)
        {
            if(content is null) return;if(content is not JsonObject media) throw Invalid("content必须是对象。");
            foreach(var (contentType,definition) in media)
            {
                if(contentType.Length>128||definition is not JsonObject value) throw Invalid("Media Type不合法。");
                var schema=value["schema"];if(schema is null) {warnings.Add($"{name}的{contentType}未定义Schema。");continue;}
                var resolved=resolver.Resolve(schema)??throw Invalid("Schema不能为空。");
                if(resolved is not JsonObject&&!(resolved is JsonValue boolean&&boolean.TryGetValue<bool>(out _))) throw Invalid("Schema必须是对象或布尔Schema。");
                schemas.Add(new(null,kind,name,status,contentType,resolved.ToJsonString(),value["example"]?.ToJsonString()));
                if(value["examples"] is not null) warnings.Add($"{name}的多个examples保留原文；首期示例字段展示单个example。");
            }
        }
        if(raw["requestBody"] is JsonNode requestNode) {var body=resolver.Resolve(requestNode) as JsonObject??throw Invalid("requestBody必须是对象。");Content(body["content"],"request","Request",null);}
        var responses=raw["responses"] as JsonObject??throw Invalid("Operation必须定义responses。");if(responses.Count==0) throw Invalid("responses不能为空。");
        foreach(var (status,responseNode) in responses)
        {
            int? code=int.TryParse(status,out var parsed)&&parsed is >=100 and <=599?parsed:null;
            if(code is null&&status!="default"&&!Regex.IsMatch(status,"^[1-5][Xx]{2}$")) throw Invalid("响应状态码不合法。");
            if(code is null&&status!="default") warnings.Add($"响应{status}使用范围状态码，名称保留，状态字段不转为单一HTTP状态。");
            var response=resolver.Resolve(responseNode) as JsonObject??throw Invalid("Response必须是对象。");Content(response["content"],"response","Response-"+status,code);
        }
        foreach(var (name,schema) in resolver.Schemas.OrderBy(p=>p.Key,StringComparer.Ordinal))
        {if(name.Length>128) throw Invalid("Schema名称超过128字符。");schemas.Add(new(null,"component",name,null,"application/json",schema.ToJsonString()));}
        var filtered=(JsonObject)root.DeepClone();var selectedPath=new JsonObject { [key.Method.ToLowerInvariant()]=raw.DeepClone() };if(path["parameters"] is JsonNode common) selectedPath["parameters"]=common.DeepClone();filtered["paths"]=new JsonObject { [key.Path]=selectedPath };
        var suggested=Regex.Replace(key.Id,"[^A-Za-z0-9_.-]","_");if(suggested.Length==0||!char.IsAsciiLetterOrDigit(suggested[0])) suggested="API_"+suggested;if(suggested.Length>64) suggested=suggested[..48]+"_"+CatalogService.Hash(key.Id)[..12];
        return new(key.Id,key.Method,key.Path,Text(raw["summary"])??key.Id,suggested,filtered.ToJsonString(),parameters.Values.ToArray(),schemas,warnings.Distinct().ToArray());
    }
    private static JsonObject ResolvePathItem(JsonObject item,LocalResolver resolver)=>item["$ref"] is null?item:resolver.Resolve(item) as JsonObject??throw Invalid("Path Item引用必须指向对象。");
    private sealed class LocalResolver(JsonObject document)
    {
        public Dictionary<string,JsonNode> Schemas {get;}=new(StringComparer.Ordinal);
        private readonly HashSet<string> stack=new(StringComparer.Ordinal);
        private int visited;
        public JsonNode? Resolve(JsonNode? node,int depth=0)
        {
            if(node is null) return null;if(depth>64||++visited>50000) throw new ApiException(422,"unsupported_reference","引用过深或超过首期解析限制。");
            if(node is JsonObject obj)
            {
                if(obj["$ref"] is JsonNode referenceNode)
                {
                    var reference=Text(referenceNode);if(reference is null||!reference.StartsWith("#/",StringComparison.Ordinal)||!stack.Add(reference)) throw new ApiException(422,"unsupported_reference","首期不支持外部或循环引用。");
                    if(obj.Any(p=>p.Key is not ("$ref" or "description" or "summary"))) throw new ApiException(422,"unsupported_reference","首期不支持有语义合并字段的$ref相邻属性。");
                    try
                    {
                        JsonNode? target=document;foreach(var token in reference[2..].Split('/')) {var name=token.Replace("~1","/").Replace("~0","~");target=target is JsonObject value?value[name]:target is JsonArray array&&int.TryParse(name,out var i)&&i>=0&&i<array.Count?array[i]:null;if(target is null) throw new ApiException(422,"unsupported_reference","本地引用不存在。");}
                        var resolved=Resolve(target,depth+1)??throw Invalid("引用对象为空。");
                        if(reference.StartsWith("#/components/schemas/",StringComparison.Ordinal)) Schemas[reference.Split('/')[^1].Replace("~1","/").Replace("~0","~")]=resolved.DeepClone();
                        return resolved;
                    }
                    finally {stack.Remove(reference);}
                }
                var result=new JsonObject();foreach(var (key,value) in obj) result[key]=key is "example" or "default" or "enum"?value?.DeepClone():Resolve(value,depth+1);return result;
            }
            if(node is JsonArray list) {var result=new JsonArray();foreach(var child in list) result.Add(Resolve(child,depth+1));return result;}return node.DeepClone();
        }
    }
}

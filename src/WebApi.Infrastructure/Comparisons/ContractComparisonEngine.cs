using WebApi.Contracts.Comparisons;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Catalog;
namespace WebApi.Infrastructure.Comparisons;
public sealed class ContractComparisonEngine
{
    public const string EngineVersion="compatibility-v1";
    private static readonly HashSet<string> Methods=["get","post","put","patch","delete","head","options","trace"];
    private sealed record Operation(JsonObject Value,JsonObject Path,JsonObject Root);
    public ComparisonReport Compare(ComparisonInput input,ComparisonLimits limits,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var c=new ComparisonContext(limits,ct);string fingerprint;
        try
        {
            var left=JsonSerializer.SerializeToUtf8Bytes(input.From);var right=JsonSerializer.SerializeToUtf8Bytes(input.To);
            if(left.Length>limits.MaxSideBytes||right.Length>limits.MaxSideBytes||left.Length+right.Length>limits.MaxPairBytes)c.Issue("input_budget","input","/","契约输入超出比较预算。",true);
            if(!c.Invalid)
            {
                var a=Document(c,input.From,"from");var b=Document(c,input.To,"to");var ao=Operations(c,a,"from");var bo=Operations(c,b,"to");
                foreach(var key in ao.Keys.Union(bo.Keys,StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    if(!ao.TryGetValue(key,out var before))c.Finding("openapi",key,"/paths/"+ContractNormalizer.Pointer(key),"Added","Compatible","新增 Operation。",null,bo[key].Value);
                    else if(!bo.TryGetValue(key,out var after))c.Finding("openapi",key,"/paths/"+ContractNormalizer.Pointer(key),"Removed","Breaking","移除旧客户端使用的 Operation。",before.Value);
                    else CompareOperation(c,key,before,after);
                }
                CompareParameters(c,input.From.Parameters,input.To.Parameters,a,b,"parameters",null,"/parameters");
                CompareManagedSchemas(c,input.From.Schemas,input.To.Schemas,a,b);
                var ac=a?["components"]?["schemas"] as JsonObject??new();var bc=b?["components"]?["schemas"] as JsonObject??new();
                foreach(var key in ac.Select(x=>x.Key).Union(bc.Select(x=>x.Key),StringComparer.Ordinal).Order(StringComparer.Ordinal))CompareSchema(c,ac[key],bc[key],"unknown","components",null,"/components/schemas/"+ContractNormalizer.Pointer(key),a,b);
                Conflict(c,input.From,ao,"from");Conflict(c,input.To,bo,"to");
                foreach(var key in new[]{"security","info"})if(!Same(a?[key],b?[key]))c.Finding("openapi",null,"/"+key,"Changed",key=="info"?"Compatible":"Unknown",key=="info"?"文档元数据变化。":"安全要求变化需人工评审。",a?[key],b?[key],key=="info");
            }
            fingerprint=ContractNormalizer.Fingerprint(input,EngineVersion);
        }
        catch(ComparisonBudgetException){c.Issue("comparison_budget","input","/","节点、深度或输出超出比较预算，结果不完整。",true);fingerprint=ContractNormalizer.Hash(JsonSerializer.SerializeToUtf8Bytes(input));}
        catch(JsonException){c.Issue("invalid_json","input","/","契约 JSON 无法完整解析。",true);fingerprint=ContractNormalizer.Hash(JsonSerializer.SerializeToUtf8Bytes(input));}
        var findings=c.Findings.OrderBy(x=>x.Key,StringComparer.Ordinal).ToArray();var issues=c.Issues;
        return new(EngineVersion,fingerprint,c.Invalid?"Invalid":issues.Count>0?"Limited":"Complete",new(findings.Count(x=>x.ChangeKind=="Added"),findings.Count(x=>x.ChangeKind=="Changed"),findings.Count(x=>x.ChangeKind=="Removed"),findings.Count(x=>x.Risk=="Compatible"),findings.Count(x=>x.Risk=="Breaking"),findings.Count(x=>x.Risk=="Unknown")+issues.Count),findings,issues);
    }
    private static void UnsupportedFields(ComparisonContext c,JsonObject value,string source,string pointer,params string[] supported)
    {foreach(var property in value)if(!supported.Contains(property.Key,StringComparer.Ordinal))c.Issue("unsupported_openapi_field",source,pointer+"/"+ContractNormalizer.Pointer(property.Key),"此 OpenAPI 结构未纳入当前自动判断范围。");}
    private static bool Same(JsonNode? a,JsonNode? b)=>ContractNormalizer.Canonical(ContractNormalizer.Normalize(a))==ContractNormalizer.Canonical(ContractNormalizer.Normalize(b));
    private static JsonObject? Document(ComparisonContext c,ContractVersionInput input,string side)
    {
        var raw=input.Version.OpenapiDocument;
        if(raw is null){c.Issue("missing_document",side,"/openapi","缺少可解析的 OpenAPI 文档；仅比较维护定义。");return null;}
        var root=c.Parse(raw,side,"/openapi") as JsonObject;if(root is null){c.Issue("invalid_document",side,"/openapi","OpenAPI 文档必须为对象。",true);return null;}
        if(!SchemaCompatibilityRules.Text(root["openapi"]).StartsWith("3.0.",StringComparison.Ordinal)){c.Issue("unsupported_openapi",side,"/openapi","仅自动支持 OpenAPI 3.0 JSON。");return null;}
        if(root["paths"] is not JsonObject){c.Issue("invalid_paths",side,"/paths","OpenAPI paths 必须为对象。",true);return null;}
        if(root["components"] is not null&&(root["components"] is not JsonObject components||components["schemas"] is not null&&components["schemas"] is not JsonObject)){c.Issue("invalid_components",side,"/components","components 及 schemas 必须为对象。",true);return null;}
        UnsupportedFields(c,root,side,"","openapi","info","paths","components","security","tags","externalDocs");
        if(root["components"] is JsonObject definitions)UnsupportedFields(c,definitions,side,"/components","schemas");
        if(root["security"] is not null)c.Issue("security_assumptions",side,"/security","安全要求不作为自动兼容性保证。");
        if(root["components"]?["schemas"] is JsonObject schemas)foreach(var schema in schemas)c.Resolve(schema.Value,root,side,"/components/schemas/"+ContractNormalizer.Pointer(schema.Key));
        return root;
    }
    private static Dictionary<string,Operation> Operations(ComparisonContext c,JsonObject? root,string side)
    {
        var result=new Dictionary<string,Operation>(StringComparer.Ordinal);if(root?["paths"] is not JsonObject paths)return result;
        foreach(var path in paths)
        {
            if(path.Value is not JsonObject item){c.Issue("invalid_path_item",side,"/paths","Path Item 必须为对象。",true);continue;}
            UnsupportedFields(c,item,side,"/paths/"+ContractNormalizer.Pointer(path.Key),"$ref","summary","description","parameters","get","post","put","patch","delete","head","options","trace");
            if(item["$ref"] is not null){c.Issue("path_reference",side,"/paths/"+ContractNormalizer.Pointer(path.Key),"Path Item 引用需人工评审。");continue;}
            foreach(var method in item.Where(p=>Methods.Contains(p.Key)))
            {
                if(method.Value is not JsonObject op){c.Issue("invalid_operation",side,"/paths","Operation 必须为对象。",true);continue;}
                var key=method.Key.ToUpperInvariant()+" "+path.Key;result.Add(key,new(op,item,root));
                UnsupportedFields(c,op,side,"/paths/"+ContractNormalizer.Pointer(path.Key)+"/"+method.Key,"parameters","requestBody","responses","security","summary","description","operationId","tags","deprecated","externalDocs");
                foreach(var k in new[]{"security","callbacks","servers"})if(op[k] is not null)c.Issue("unsupported_operation",side,"/"+key+"/"+k,"此 Operation 约束需人工评审。");
                _=Parameters(c,new(op,item,root),side,key);
                _=OperationSchemas(c,new(op,item,root),side,key);
            }
        }
        return result;
    }
    private static IReadOnlyList<ParameterDto> Parameters(ComparisonContext c,Operation op,string source,string key)
    {
        var map=new Dictionary<string,ParameterDto>(StringComparer.Ordinal);
        foreach(var collection in new[]{op.Path["parameters"],op.Value["parameters"]})
        {
            if(collection is null)continue;if(collection is not JsonArray list){c.Issue("invalid_parameters",source,key,"参数必须为数组。",true);continue;}
            var local=new HashSet<string>(StringComparer.Ordinal);
            foreach(var node in list)
            {
                if(node is not JsonObject p){c.Issue("invalid_parameter",source,key,"参数必须为对象。",true);continue;}
                if(p["$ref"] is not null){c.Issue("parameter_reference",source,key,"参数引用需人工评审。");continue;}
                var name=SchemaCompatibilityRules.Text(p["name"]);var location=SchemaCompatibilityRules.Text(p["in"]);var identity=ContractNormalizer.ParameterKey(location,name);
                if(name.Length==0||location is not ("path" or "query" or "header" or "cookie")||!local.Add(identity)){c.Issue("invalid_parameter",source,key,"参数标识不完整或重复。",true);continue;}
                foreach(var k in new[]{"style","explode","allowReserved","content"})if(p[k] is not null)c.Issue("parameter_serialization",source,key+"/"+identity,"参数序列化行为需人工评审。");
                UnsupportedFields(c,p,source,key+"/parameters/"+ContractNormalizer.Pointer(identity),"name","in","description","required","schema","example","examples","deprecated","style","explode","allowReserved","content");
                if(p.ContainsKey("required")&&(p["required"] is not JsonValue flag||!flag.TryGetValue<bool>(out _)))c.Issue("invalid_parameter_required",source,key+"/parameters/"+identity,"参数 required 必须为布尔值。",true);
                var schema=c.Resolve(p["schema"],op.Root,source,key+"/"+identity);if(schema is null)c.Issue("missing_parameter_schema",source,key+"/"+identity,"参数缺少 Schema。");
                map[identity]=new(Guid.Empty,Guid.Empty,location,name,SchemaCompatibilityRules.Text(schema is JsonObject shape?shape["type"]:null),SchemaCompatibilityRules.Flag(p["required"]),schema?.ToJsonString(),SchemaCompatibilityRules.Text(p["description"]),p["example"]?.ToJsonString());
            }
        }
        return map.Values.ToArray();
    }
    private sealed record OperationSchema(JsonNode? Schema,string Direction,bool Required);
    private static Dictionary<string,OperationSchema> OperationSchemas(ComparisonContext c,Operation op,string source,string key)
    {
        var result=new Dictionary<string,OperationSchema>(StringComparer.Ordinal);
        void Content(JsonNode? content,string identity,string direction,bool required)
        {
            if(content is null)return;if(content is not JsonObject media){c.Issue("invalid_content",source,key,"content 必须为对象。",true);return;}
            foreach(var p in media){if(p.Value is not JsonObject definition){c.Issue("invalid_media_type",source,key+"/"+identity,"媒体类型定义必须为对象。",true);continue;}UnsupportedFields(c,definition,source,key+"/"+identity+"/"+ContractNormalizer.Pointer(p.Key),"schema","example","examples");var schema=definition["schema"];if(schema is null){c.Issue("missing_schema",source,key+"/"+identity+"/"+p.Key,"媒体类型未定义 Schema。");continue;}result[identity+":"+p.Key]=new(c.Resolve(schema,op.Root,source,key+"/"+identity+"/"+p.Key),direction,required);}
        }
        if(op.Value["requestBody"] is not null&&op.Value["requestBody"] is not JsonObject)c.Issue("invalid_request_body",source,key,"requestBody 必须为对象。",true);
        if(op.Value["requestBody"] is JsonObject request)
        {UnsupportedFields(c,request,source,key+"/requestBody","$ref","description","required","content");if(request.ContainsKey("required")&&(request["required"] is not JsonValue flag||!flag.TryGetValue<bool>(out _)))c.Issue("invalid_body_required",source,key+"/requestBody/required","请求体 required 必须为布尔值。",true);if(request["$ref"] is not null)c.Issue("body_reference",source,key,"请求体引用需人工评审。");else Content(request["content"],"request","request",SchemaCompatibilityRules.Flag(request["required"]));}
        if(op.Value["responses"] is not JsonObject)c.Issue("invalid_responses",source,key,"responses 必须为对象。",true);
        if(op.Value["responses"] is JsonObject responses)foreach(var p in responses)
        {if(p.Value is not JsonObject response){c.Issue("invalid_response",source,key+"/responses/"+p.Key,"响应定义必须为对象。",true);continue;}UnsupportedFields(c,response,source,key+"/responses/"+p.Key,"$ref","description","content");if(response["$ref"] is not null)c.Issue("response_reference",source,key+"/responses/"+p.Key,"响应引用需人工评审。");else Content(response["content"],"response:"+p.Key,"response",false);}
        return result;
    }
    private static void CompareOperation(ComparisonContext c,string key,Operation before,Operation after)
    {
        CompareParameters(c,Parameters(c,before,"openapi",key),Parameters(c,after,"openapi",key),before.Root,after.Root,"openapi",key,"/parameters");
        var a=OperationSchemas(c,before,"openapi",key);var b=OperationSchemas(c,after,"openapi",key);
        foreach(var schema in a.Keys.Union(b.Keys,StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            a.TryGetValue(schema,out var old);b.TryGetValue(schema,out var next);
            if(old is not null&&next is not null&&old.Required!=next.Required)c.Finding("openapi",key,"/"+schema+"/required","Changed",next.Required?"Breaking":"Compatible","请求体必填性变化。",JsonValue.Create(old.Required),JsonValue.Create(next.Required));
            if(old is null||next is null)c.Finding("openapi",key,"/"+ContractNormalizer.Pointer(schema),old is null?"Added":"Removed",next?.Direction=="request"&&!a.Values.Any(x=>x.Direction=="request")?(next.Required?"Breaking":"Compatible"):"Unknown","请求/响应状态或媒体类型定义变化。",old?.Schema,next?.Schema);
            else CompareSchema(c,old.Schema,next.Schema,next.Direction,"openapi",key,"/"+ContractNormalizer.Pointer(schema),before.Root,after.Root,next.Required);
        }
        foreach(var property in before.Value.Select(p=>p.Key).Union(after.Value.Select(p=>p.Key),StringComparer.Ordinal).Where(x=>x is not ("parameters" or "requestBody" or "responses")))
            if(!Same(before.Value[property],after.Value[property]))c.Finding("openapi",key,"/"+property,"Changed",property is "description" or "summary" or "operationId" or "tags"?"Compatible":"Unknown","Operation 属性变化。",before.Value[property],after.Value[property],property is "description" or "summary" or "operationId" or "tags");
        var oldResponses=before.Value["responses"] as JsonObject??new();var newResponses=after.Value["responses"] as JsonObject??new();
        foreach(var status in oldResponses.Select(p=>p.Key).SymmetricExcept(newResponses.Select(p=>p.Key)))c.Finding("openapi",key,"/responses/"+status,oldResponses.ContainsKey(status)?"Removed":"Added","Unknown","响应状态码变化需核对旧客户端。");
    }
    private static void CompareParameters(ComparisonContext c,IReadOnlyList<ParameterDto> before,IReadOnlyList<ParameterDto> after,JsonNode? oldRoot,JsonNode? newRoot,string source,string? operation,string pointer)
    {
        Dictionary<string,ParameterDto> Map(IReadOnlyList<ParameterDto> values)
        {var result=new Dictionary<string,ParameterDto>(StringComparer.Ordinal);foreach(var p in values){var k=ContractNormalizer.ParameterKey(p.Location,p.Name);if(!result.TryAdd(k,p))c.Issue("duplicate_parameter",source,pointer,"存在重复参数标识。",true);}return result;}
        var a=Map(before);var b=Map(after);
        foreach(var key in a.Keys.Union(b.Keys,StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            a.TryGetValue(key,out var old);b.TryGetValue(key,out var next);var path=pointer+"/"+ContractNormalizer.Pointer(key);
            var os=c.Resolve(c.Parse(old?.Schema,source,path),oldRoot,source,path);var ns=c.Resolve(c.Parse(next?.Schema,source,path),newRoot,source,path);
            if(old is null)c.Finding(source,operation,path,"Added",next!.Required?"Breaking":"Compatible","新增请求参数。",null,ns);
            else if(next is null)c.Finding(source,operation,path,"Removed","Unknown","移除请求参数，旧值是否仍被接受需人工判断。",os);
            else
            {
                if(old.Required!=next.Required)c.Finding(source,operation,path+"/required","Changed",next.Required?"Breaking":"Compatible","请求参数必填性变化。",JsonValue.Create(old.Required),JsonValue.Create(next.Required));
                if(old.DataType!=next.DataType)c.Finding(source,operation,path+"/dataType","Changed","Unknown","参数类型变化。",JsonValue.Create(old.DataType),JsonValue.Create(next.DataType));
                SchemaCompatibilityRules.Compare(c,os,ns,"request",source,operation,path+"/schema");
                if(old.Description!=next.Description||old.ExampleJson!=next.ExampleJson)c.Finding(source,operation,path+"/metadata","Changed","Compatible","仅参数元数据变化。",metadata:true);
            }
        }
    }
    private static void CompareManagedSchemas(ComparisonContext c,IReadOnlyList<SchemaDto> before,IReadOnlyList<SchemaDto> after,JsonNode? oldRoot,JsonNode? newRoot)
    {
        Dictionary<string,SchemaDto> Map(IReadOnlyList<SchemaDto> values)
        {var result=new Dictionary<string,SchemaDto>(StringComparer.Ordinal);foreach(var s in values){var key=ContractNormalizer.SchemaKey(s.SchemaType,s.Name,s.StatusCode,s.ContentType);if(!result.TryAdd(key,s))c.Issue("duplicate_schema","schemas","/schemas","存在重复 Schema 标识。",true);}return result;}
        var a=Map(before);var b=Map(after);var consumed=new HashSet<string>(StringComparer.Ordinal);
        foreach(var key in a.Keys.Order(StringComparer.Ordinal).Concat(b.Keys.Except(a.Keys,StringComparer.Ordinal).Order(StringComparer.Ordinal)))
        {
            if(consumed.Contains(key))continue;a.TryGetValue(key,out var old);b.TryGetValue(key,out var next);var path="/schemas/"+ContractNormalizer.Pointer(key);
            if(old is not null&&next is null)
            {
                var replacements=b.Where(p=>!a.ContainsKey(p.Key)&&!consumed.Contains(p.Key)&&p.Value.SchemaType==old.SchemaType&&p.Value.Name==old.Name).ToArray();
                if(replacements.Length==1){next=replacements[0].Value;consumed.Add(replacements[0].Key);c.Finding("schemas",null,path+"/media","Changed","Unknown","状态码或 ContentType 变化需核对旧客户端。");}
            }
            CompareSchema(c,c.Parse(old?.SchemaJson,"schemas",path),c.Parse(next?.SchemaJson,"schemas",path),(next??old)!.SchemaType,"schemas",null,path,oldRoot,newRoot);
            if(old is not null&&next is not null&&old.ExampleJson!=next.ExampleJson)c.Finding("schemas",null,path+"/metadata","Changed","Compatible","仅 Schema 示例元数据变化。",metadata:true);
        }
    }
    private static void CompareSchema(ComparisonContext c,JsonNode? before,JsonNode? after,string direction,string source,string? operation,string pointer,JsonNode? oldRoot,JsonNode? newRoot,bool required=false)
    {
        var a=c.Resolve(before,oldRoot,source,pointer);var b=c.Resolve(after,newRoot,source,pointer);
        if(before is null&&after is not null)c.Finding(source,operation,pointer,"Added",direction=="request"?(required?"Breaking":"Compatible"):"Unknown","新增 Schema 定义。",null,b);
        else if(after is null&&before is not null)c.Finding(source,operation,pointer,"Removed",direction=="response"?"Breaking":"Unknown","移除 Schema 定义。",a);
        else SchemaCompatibilityRules.Compare(c,a,b,direction,source,operation,pointer);
    }
    private static void Conflict(ComparisonContext c,ContractVersionInput input,Dictionary<string,Operation> operations,string side)
    {
        if(operations.Count!=1)return;var op=operations.Single();var parameters=Parameters(c,op.Value,side,op.Key).ToDictionary(x=>ContractNormalizer.ParameterKey(x.Location,x.Name),StringComparer.Ordinal);
        foreach(var p in input.Parameters)if(parameters.TryGetValue(ContractNormalizer.ParameterKey(p.Location,p.Name),out var other)&&(p.DataType!=other.DataType||p.Required!=other.Required||!Same(c.Parse(p.Schema,side,"/parameters"),c.Parse(other.Schema,side,"/parameters"))))c.Issue("source_conflict",side,"/parameters/"+ContractNormalizer.Pointer(p.Name),"OpenAPI 与维护参数定义不一致。");
        var schemas=OperationSchemas(c,op.Value,side,op.Key);
        foreach(var s in input.Schemas)
        {var key=s.SchemaType=="request"?"request:"+s.ContentType:s.SchemaType=="response"?"response:"+(s.StatusCode?.ToString()??"default")+":"+s.ContentType:"";if(schemas.TryGetValue(key,out var other)&&!Same(c.Resolve(c.Parse(s.SchemaJson,side,"/schemas"),op.Value.Root,side,"/schemas"),other.Schema))c.Issue("source_conflict",side,"/schemas/"+ContractNormalizer.Pointer(s.Name),"OpenAPI 与维护 Schema 定义不一致。");}
    }
}
internal static class ComparisonSetExtensions
{
    internal static IEnumerable<string> SymmetricExcept(this IEnumerable<string> left,IEnumerable<string> right)=>left.Except(right,StringComparer.Ordinal).Concat(right.Except(left,StringComparer.Ordinal)).Order(StringComparer.Ordinal);
}

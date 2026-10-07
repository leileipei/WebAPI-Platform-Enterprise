using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.OpenApi;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Contracts;
namespace WebApi.Infrastructure.Comparisons.Rules;

// Models decoded HTTP values. Wire serialization must be unchanged before a
// decoded-value proof can establish compatibility. All references remain offline.
internal sealed class OpenApiContractRules
{
    private static readonly HashSet<string> Methods=["get","post","put","patch","delete","head","options","trace"];
    private readonly ContractBundle bundle;
    private readonly ContractReferenceRegistry graph;
    private readonly ContractLimits limits;
    private readonly CancellationToken ct;
    private readonly string formatMode;
    private readonly Action<string,string,string> issue;
    private readonly ContractVersionInput? maintained;
    private readonly IReadOnlyDictionary<(string Kind,Guid Id),ContractDefinitionSource> origins;
    private readonly Dictionary<string,IReadOnlyList<ContractDocument>> adapted=new(StringComparer.Ordinal);
    internal JsonObject Root {get;}
    internal Dictionary<string,HttpOperation> Operations {get;}=new(StringComparer.Ordinal);
    internal sealed record HttpParameter(string Key,JsonObject Value,JsonNode Schema,string Wire,bool Required,bool WireKnown);
    internal sealed record HttpOperation(string Key,string Pointer,JsonObject Value,JsonObject Path,IReadOnlyDictionary<string,HttpParameter> Parameters,JsonNode? Security,JsonObject Responses);
    internal sealed record Model(ContractBundle Bundle,JsonNode Schema,SchemaProofLocation Location);
    internal OpenApiContractRules(ContractBundle bundle,ContractLimits limits,CancellationToken ct,Action<string,string,string> issue,ContractVersionInput? maintained=null,IReadOnlyList<ContractDefinitionSource>? definitions=null,IReadOnlySet<string>? selectedOperations=null,string formatMode="Annotation")
    {
        this.bundle=bundle;this.limits=limits;this.ct=ct;this.issue=issue;this.formatMode=formatMode;
        this.maintained=maintained;origins=(definitions??[]).ToDictionary(x=>(x.Kind,x.Id));
        graph=new(bundle,limits);Root=(JsonObject)graph.Resolve(bundle.RootUri,"").Node;
        foreach(var schema in graph.SchemaRoots){
            ct.ThrowIfCancellationRequested();var physical=schema.Node;while(physical.Parent is not null)physical=physical.Parent;
            var document=bundle.Documents.First(x=>ReferenceEquals(graph.Resolve(x.Source.LogicalUri,"").Node,physical));
            var prepared=DialectAdapter.PrepareSchema(schema.Node,document.Dialect,"request",applyDirection:false);
            foreach(var diagnostic in prepared.Issues){if(diagnostic.Code=="invalid_schema_dialect")throw Invalid("Schema 不符合所声明的 OpenAPI 方言。");issue(diagnostic.Code,schema.Pointer+diagnostic.Pointer,diagnostic.Message);}
            if(!Json.Schema.MetaSchemas.Draft202012.Evaluate(JsonSerializer.SerializeToElement(prepared.Node,new JsonSerializerOptions{MaxDepth=128}),new(){OutputFormat=Json.Schema.OutputFormat.Flag}).IsValid)throw Invalid("Schema 结构不符合登记方言。");
        }
        var inspected=new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);var active=new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        foreach(var schema in graph.SchemaRoots)InspectSchema(schema.Node,inspected,active);
        Fields(Root,"","openapi","info","paths","components","security","tags","externalDocs","servers","jsonSchemaDialect");
        if(Root["servers"] is not null)issue("server_routing","/servers","服务器路由不在解码值包含证明中。");
        if(Root["jsonSchemaDialect"] is not null&&Text(Root["jsonSchemaDialect"]) is not("https://json-schema.org/draft/2020-12/schema" or "https://spec.openapis.org/oas/3.1/dialect/base"))issue("unsupported_schema_dialect","/jsonSchemaDialect","未登记的 Schema 方言。");
        if(Root["components"] is JsonObject components)Fields(components,"/components","schemas","responses","parameters","examples","requestBodies","headers","securitySchemes","links","callbacks","pathItems");
        var patterns=new HashSet<string>(StringComparer.Ordinal);
        foreach(var(path,node)in Root["paths"]!.AsObject()){
            ct.ThrowIfCancellationRequested();var pointer="/paths/"+Escape(path);
            var pattern=Regex.Replace(path,"\\{[^{}]+\\}","{}",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            if(!patterns.Add(pattern))issue("ambiguous_path_template",pointer,"存在仅模板名称不同的路径，不能自动匹配。");
            var item=ResolveObject(node,"path_item");Fields(item,pointer,"$ref","summary","description","parameters","get","post","put","patch","delete","head","options","trace","servers");
            if(item["servers"] is not null)issue("server_routing",pointer+"/servers","Path Item 路由需单独评审。");
            foreach(var(method,operation)in item.Where(x=>Methods.Contains(x.Key))){
                var key=method.ToUpperInvariant()+" "+path;if(selectedOperations is not null&&!selectedOperations.Contains(key))continue;
                var value=ResolveObject(operation,"operation");var opPointer=pointer+"/"+method;
                Fields(value,opPointer,"tags","summary","description","externalDocs","operationId","parameters","requestBody","responses","deprecated","security","servers");
                if(value["servers"] is not null)issue("server_routing",opPointer+"/servers","Operation 路由需单独评审。");
                var security=value.ContainsKey("security")?value["security"]:ScopeRoot(value)["security"];
                _=SecurityRequirementRules.BuildPredicate(security);
                ValidateSecurityRequirements(security,ScopeRoot(value));
                var responses=value["responses"] as JsonObject??throw Invalid("responses 必须为对象。");
                var parameters=Parameters(item,value,opPointer);
                if(!Operations.TryAdd(key,new(key,opPointer,value,item,parameters,security,responses)))throw Invalid("重复 Operation。");
                if(Operations.Count>limits.MaxOperations)throw Budget();
            }
        }
        if(Operations.Count>1&&maintained is not null&&(maintained.Parameters.Count>0||maintained.Schemas.Any(x=>x.SchemaType is "request" or "response")))issue("ambiguous_definition_operation","/","维护定义没有 Operation 标识，无法分配到多个操作。");
    }
    private void InspectSchema(JsonNode node,HashSet<JsonNode> inspected,HashSet<JsonNode> active)
    {
        ct.ThrowIfCancellationRequested();var origin=graph.Describe(node);
        if(active.Contains(node)){issue("recursive_schema_unproved",origin.Pointer,"递归引用的完整接受域需人工评审。");return;}
        if(!inspected.Add(node)||node is not JsonObject value)return;if(active.Count>=limits.MaxDepth)throw Budget();active.Add(node);
        var physical=node;while(physical.Parent is not null)physical=physical.Parent;
        var dialect=bundle.Documents.Single(x=>ReferenceEquals(graph.Resolve(x.Source.LogicalUri,"").Node,physical)).Dialect;
        if(dialect==ContractDialect.Oas30&&value["$ref"] is JsonValue legacyReference&&legacyReference.TryGetValue<string>(out var legacyText)){InspectSchema(graph.Resolve(origin.ResourceUri,legacyText).Node,inspected,active);active.Remove(node);return;}
        if(formatMode=="Strict"&&value["format"] is JsonNode format&&!ContractFormats.Registered.Contains(Text(format)))issue("unsupported_schema_format",origin.Pointer+"/format","Strict 模式中该格式未登记。");
        foreach(var keyword in new[]{"$dynamicRef","$dynamicAnchor","$vocabulary","discriminator","contentSchema"})if(value.ContainsKey(keyword))issue("schema_behavior_unproved",origin.Pointer+"/"+keyword,"该动态、词汇或内容行为未纳入有限包含证明。");
        if(value["$ref"] is JsonValue reference&&reference.TryGetValue<string>(out var text))InspectSchema(graph.Resolve(origin.ResourceUri,text).Node,inspected,active);
        if(value["$dynamicRef"] is JsonValue dynamicReference&&dynamicReference.TryGetValue<string>(out var dynamicText))_=graph.Resolve(origin.ResourceUri,dynamicText);
        foreach(var(key,child)in value){
            if(SchemaNavigation.MapKeywords.Contains(key)&&child is JsonObject map){foreach(var(_,item)in map)if(item is not null)InspectSchema(item,inspected,active);}
            else if(SchemaNavigation.ArrayKeywords.Contains(key)&&child is JsonArray list){foreach(var item in list)if(item is not null)InspectSchema(item,inspected,active);}
            else if(SchemaNavigation.SingleKeywords.Contains(key)&&child is not null)InspectSchema(child,inspected,active);
        }active.Remove(node);
    }
    private void ValidateSecurityRequirements(JsonNode? requirements,JsonObject scopeRoot)
    {
        if(requirements is not JsonArray list)return;
        foreach(var requirement in list.OfType<JsonObject>())foreach(var(name,scopes)in requirement){
            if(scopeRoot["components"]?["securitySchemes"]?[name] is not JsonNode raw){issue("security_implementation","/security","认证方案不在来源文档中。");continue;}
            var scheme=ResolveObject(raw,"security_scheme");var type=Text(scheme["type"]);
            if(Text(scopeRoot["openapi"]).StartsWith("3.0.",StringComparison.Ordinal)&&type is not("oauth2" or "openIdConnect")&&scopes is JsonArray roles&&roles.Count>0)throw Invalid("OpenAPI 3.0 非 OAuth/OIDC 的认证要求必须为空数组。");
            _=Meaning(name,scopeRoot);
        }
    }
    internal void CheckMaintenanceSources(ContractVersionInput version,ContractSourceMetadata metadata)
    {
        foreach(var source in metadata.Definitions){
            ct.ThrowIfCancellationRequested();var schema=source.Kind=="schema"?version.Schemas.SingleOrDefault(x=>x.Id==source.Id)?.SchemaJson:source.Kind=="parameter"?version.Parameters.SingleOrDefault(x=>x.Id==source.Id)?.Schema:null;
            if(schema is null)continue;var physical=WebApi.Infrastructure.Catalog.VersionContractSourceService.Physical(bundle,graph,source);
            JsonNode original;try{original=graph.Resolve(physical.Uri,"#"+Uri.EscapeDataString(physical.Pointer)).Node;}
            catch(ApiException error)when(error.Code=="missing_contract_reference"&&(WebApi.Infrastructure.Catalog.VersionContractSourceService.IsIndependentSource(bundle,source)||source.Kind=="schema"&&physical.Uri==bundle.RootUri&&version.Schemas.Any(x=>x.Id==source.Id&&x.SchemaType=="component"&&physical.Pointer=="/components/schemas/"+Escape(x.Name)))){continue;}
            if(ContractNormalizer.Canonical(original)!=ContractNormalizer.Canonical(JsonNode.Parse(schema)))issue("source_conflict",physical.Pointer,"固定 OpenAPI 来源与独立维护定义不一致，原始来源仍保留。");
        }
        if(Operations.Count!=1)return;var operation=Operations.Values.Single();
        foreach(var parameter in version.Parameters){
            var key=ContractNormalizer.ParameterKey(parameter.Location,parameter.Name);if(!operation.Parameters.TryGetValue(key,out var declared))continue;
            var schema=JsonNode.Parse(parameter.Schema??new JsonObject{["type"]=parameter.DataType}.ToJsonString());
            if(declared.Required!=parameter.Required||ContractNormalizer.Canonical(ReferencedValue(declared.Schema))!=ContractNormalizer.Canonical(schema))issue("source_conflict",operation.Pointer+"/parameters/"+Escape(key),"固定 OpenAPI 与维护参数定义不一致。");
        }
        foreach(var schema in version.Schemas.Where(x=>x.SchemaType is "request" or "response")){
            var source=metadata.Definitions.SingleOrDefault(x=>x.Kind=="schema"&&x.Id==schema.Id);
            if(schema.SchemaType=="response"&&source?.ResponseSelector is string selector){
                var sameScope=int.TryParse(selector,out var originalStatus)?schema.StatusCode==originalStatus:schema.StatusCode is null;
                if(!sameScope)issue("source_conflict",operation.Pointer+"/responses/"+Escape(selector),"固定 OpenAPI 响应范围与维护 Schema 状态码范围不一致。");
            }
            var parent=schema.SchemaType=="request"?operation.Value["requestBody"] is JsonNode body?ResolveObject(body,"request_body"):null:schema.StatusCode is int status?EffectiveResponse(operation,status):operation.Responses["default"] is JsonNode fallback?ResolveObject(fallback,"response"):null;
            if(parent?["content"]?[schema.ContentType]?["schema"] is JsonNode original&&ContractNormalizer.Canonical(original)!=ContractNormalizer.Canonical(JsonNode.Parse(schema.SchemaJson)))issue("source_conflict",operation.Pointer+"/"+schema.SchemaType+"/"+Escape(schema.ContentType),"固定 OpenAPI 与维护请求/响应 Schema 不一致。");
        }
    }
    internal Model Request(HttpOperation op)
    {
        var parameters=new JsonObject();var required=new JsonArray();
        foreach(var(key,parameter)in op.Parameters){parameters[key]=parameter.Schema.DeepClone();if(parameter.Required)required.Add(key);}
        if(Operations.Count<=1&&maintained is not null)foreach(var parameter in maintained.Parameters){
            var key=ContractNormalizer.ParameterKey(parameter.Location,parameter.Name);
            if(op.Parameters.TryGetValue(key,out var declared)&&(declared.Required!=parameter.Required||ContractNormalizer.Canonical(ReferencedValue(declared.Schema))!=ContractNormalizer.Canonical(ReferencedValue(MaintainedSchema(parameter.Id,"parameter")))))issue("source_conflict",op.Pointer+"/parameters/"+Escape(key),"OpenAPI 与维护参数定义不一致。");
            parameters[key]=MaintainedSchema(parameter.Id,"parameter");for(var i=required.Count-1;i>=0;i--)if(Text(required[i])==key)required.RemoveAt(i);if(parameter.Required)required.Add(key);
        }
        var properties=new JsonObject{["parameters"]=Object(parameters,required,true),["credentials"]=SecurityRequirementRules.BuildPredicate(op.Security)};
        var rootRequired=new JsonArray("parameters","credentials");
        if(op.Value["requestBody"] is JsonNode raw){
            var body=ResolveObject(raw,"request_body");Fields(body,op.Pointer+"/requestBody","$ref","description","content","required");
            properties["body"]=Content(body["content"],op.Pointer+"/requestBody/content");if(Flag(body,"required"))rootRequired.Add("body");
        }
        if(Operations.Count<=1&&maintained is not null&&maintained.Schemas.Any(x=>x.SchemaType=="request"))properties["body"]=MaintainedContent(maintained.Schemas.Where(x=>x.SchemaType=="request"));
        return Derive(Object(properties,rootRequired,true),"request");
    }
    internal Model ManagedRequest()=>Request(new("","/definitions",new(),new(),new Dictionary<string,HttpParameter>(),null,new()));
    internal Model ManagedResponse(int status)
    {
        var matching=ResponseSchemas(status);
        return matching.Length==0?Derive(JsonValue.Create(false)!,"response"):Derive(Object(new(){["body"]=MaintainedContent(matching)},new("body"),true),"response");
    }
    internal bool ManagedWireCompatible(OpenApiContractRules target)
    {
        var keys=target.maintained?.Parameters.Select(x=>ContractNormalizer.ParameterKey(x.Location,x.Name)).ToHashSet(StringComparer.Ordinal)??[];var known=true;
        foreach(var parameter in maintained?.Parameters??[])if(!keys.Contains(ContractNormalizer.ParameterKey(parameter.Location,parameter.Name))){issue("removed_parameter","/definitions/parameters/"+Escape(parameter.Name),"移除参数后是否仍接受旧值需评审。");known=false;}
        return known;
    }
    private JsonNode MaintainedSchema(Guid id,string kind="schema")
    {
        if(!origins.TryGetValue((kind,id),out var source))throw Invalid("维护定义缺少固定来源位置。");return Reference(graph.Resolve(source.DocumentUri??source.ResourceUri,"#"+Uri.EscapeDataString(source.Pointer)).Node);
    }
    private JsonNode ReferencedValue(JsonNode reference)=>reference["$ref"] is JsonValue value&&value.TryGetValue<string>(out var text)?graph.Resolve(bundle.RootUri,text).Node:reference;
    private JsonNode MaintainedContent(IEnumerable<SchemaDto> schemas)
    {
        var branches=new JsonArray();var media=new HashSet<string>(StringComparer.Ordinal);
        foreach(var schema in schemas){if(!media.Add(schema.ContentType))throw Invalid("维护请求/响应媒体类型重复，无法确定父约束。");branches.Add(Object(new(){["mediaType"]=new JsonObject{["const"]=schema.ContentType},["value"]=MaintainedSchema(schema.Id)},new("mediaType","value"),true));}
        return branches.Count==1?branches[0]!.DeepClone():new JsonObject{["anyOf"]=branches};
    }
    internal JsonObject? EffectiveResponse(HttpOperation op,int status)=>ResponseCoverageRules.Effective(op.Responses,status) is JsonNode response?ResolveObject(response,"response"):null;
    internal string ManagedResponseKey(int status)=>string.Join("|",ResponseSchemas(status).Select(x=>x.Id.ToString("N")).Order(StringComparer.Ordinal));
    private SchemaDto[] ResponseSchemas(int status)
    {
        if(Operations.Count>1)return [];var responses=maintained?.Schemas.Where(x=>x.SchemaType=="response").ToArray()??[];
        var exact=responses.Where(x=>x.StatusCode==status).ToArray();if(exact.Length>0)return exact;
        string? Selector(SchemaDto schema)=>origins.TryGetValue(("schema",schema.Id),out var source)?source.ResponseSelector:null;
        var range=responses.Where(x=>x.StatusCode is null&&Selector(x)==(status/100)+"XX").ToArray();
        return range.Length>0?range:responses.Where(x=>x.StatusCode is null&&Selector(x) is null or "DEFAULT").ToArray();
    }
    internal Model Response(HttpOperation op,JsonObject? response,int status)
    {
        var matching=ResponseSchemas(status);
        if(response is null&&matching.Length==0)return Derive(JsonValue.Create(false)!,"response");
        response??=new JsonObject();
        Fields(response,op.Pointer+"/responses","$ref","description","headers","content","links");
        if(response["links"] is not null)issue("response_links",op.Pointer+"/responses/links","响应链接行为未登记。");
        var headerProperties=new JsonObject();var headerRequired=new JsonArray();
        if(response["headers"] is JsonNode rawHeaders){
            if(rawHeaders is not JsonObject headers)throw Invalid("headers 必须为对象。");
            foreach(var(name,node)in headers){
                var key=name.ToLowerInvariant();if(key=="content-type")continue;
                if(headerProperties.ContainsKey(key))throw Invalid("响应头名称忽略大小写后重复。");
                var header=ResolveObject(node,"header");Fields(header,op.Pointer+"/responses/headers/"+Escape(name),"$ref","description","required","deprecated","allowEmptyValue","style","explode","schema","content","example","examples");
                if(Text(header["style"]) is not("" or "simple"))issue("header_serialization",op.Pointer+"/responses/headers/"+Escape(name),"响应头编码形式未登记。");
                headerProperties[key]=ValueSchema(header,op.Pointer+"/responses/headers/"+Escape(name));if(Flag(header,"required"))headerRequired.Add(key);
            }
        }
        var properties=new JsonObject{["headers"]=Object(headerProperties,headerRequired,false)};var required=new JsonArray("headers");
        if(response["content"] is JsonNode content){properties["body"]=Content(content,op.Pointer+"/responses/content");required.Add("body");}
        if(matching.Length>0){properties["body"]=MaintainedContent(matching);if(!required.Any(x=>Text(x)=="body"))required.Add("body");}
        // No declared content is the documented empty-body guarantee.
        return Derive(Object(properties,required,true),"response");
    }
    internal bool RequestWireCompatible(HttpOperation before,OpenApiContractRules target,HttpOperation after)
    {
        var known=true;
        if(!ManagedWireCompatible(target))known=false;
        if(before.Parameters.Values.Concat(after.Parameters.Values).Any(x=>!x.WireKnown))known=false;
        foreach(var(key,old)in before.Parameters){
            if(!after.Parameters.TryGetValue(key,out var next)){issue("removed_parameter",before.Pointer+"/parameters/"+Escape(key),"移除参数后是否仍接受旧值需评审。");known=false;}
            else if(old.Wire!=next.Wire){issue("parameter_serialization",before.Pointer+"/parameters/"+Escape(key),"参数序列化语义发生变化。");known=false;}
        }
        var a=before.Value["requestBody"] is JsonNode ar?ResolveObject(ar,"request_body"):null;
        var b=after.Value["requestBody"] is JsonNode br?target.ResolveObject(br,"request_body"):null;
        if(!EncodingEqual(a?["content"],b?["content"])) {issue("request_encoding",before.Pointer+"/requestBody/content","请求媒体编码发生变化。");known=false;}
        foreach(var name in Schemes(before.Security).Union(Schemes(after.Security),StringComparer.Ordinal)){
            var old=Meaning(name,ScopeRoot(before.Value));var next=target.Meaning(name,target.ScopeRoot(after.Value));
            if(old is null||next is null||old!=next){issue("security_implementation",before.Pointer+"/security","认证实现缺失、未登记或发生变化。");known=false;}
        }
        return known;
    }
    internal bool ResponseWireCompatible(HttpOperation before,JsonObject? a,OpenApiContractRules target,JsonObject? b)
    {
        var known=true;
        if(!EncodingEqual(a?["content"],b?["content"])) {issue("response_encoding",before.Pointer+"/responses/content","响应媒体编码发生变化。");known=false;}
        if(a?["headers"] is JsonObject headers&&b?["headers"] is JsonObject other)foreach(var(name,node)in headers){
            var next=other.FirstOrDefault(x=>string.Equals(x.Key,name,StringComparison.OrdinalIgnoreCase)).Value;
            if(next is not null&&Wire(ResolveObject(node,"header"),"header")!=Wire(target.ResolveObject(next,"header"),"header")){issue("header_serialization",before.Pointer+"/responses/headers/"+Escape(name),"响应头序列化发生变化。");known=false;}
        }
        return known;
    }
    private IReadOnlyDictionary<string,HttpParameter> Parameters(JsonObject path,JsonObject op,string pointer)
    {
        var result=new Dictionary<string,HttpParameter>(StringComparer.Ordinal);
        foreach(var collection in new[]{path["parameters"],op["parameters"]}){
            if(collection is null)continue;if(collection is not JsonArray list)throw Invalid("parameters 必须为数组。");
            var local=new HashSet<string>(StringComparer.Ordinal);
            foreach(var node in list){
                var value=ResolveObject(node,"parameter");var name=Text(value["name"]);var location=Text(value["in"]);
                if(name.Length==0||location is not("query" or "path" or "header" or "cookie"))throw Invalid("参数标识不合法。");
                var key=ContractNormalizer.ParameterKey(location,name);if(!local.Add(key))throw Invalid("同一级重复参数。");
                Fields(value,pointer+"/parameters/"+Escape(key),"$ref","name","in","description","required","deprecated","allowEmptyValue","style","explode","allowReserved","schema","content","example","examples");
                if(location=="header"&&name.ToLowerInvariant() is "accept" or "content-type" or "authorization")continue;
                var required=Flag(value,"required");if(location=="path"&&!required)throw Invalid("path 参数必须 required=true。");
                var style=Text(value["style"]);if(style.Length==0)style=location is "query" or "cookie"?"form":"simple";
                var wireKnown=location switch{"query"=>style=="form","path"=>style is "matrix" or "label" or "simple","header"=>style=="simple","cookie"=>style=="form",_=>false};
                if(!wireKnown)issue("parameter_serialization",pointer+"/parameters/"+Escape(key),"该参数编码形式未登记。");
                result[key]=new(key,value,ValueSchema(value,pointer+"/parameters/"+Escape(key)),Wire(value,location),required,wireKnown);
            }
        }
        return result;
    }
    private JsonNode ValueSchema(JsonObject value,string pointer)
    {
        if(value.ContainsKey("schema")==value.ContainsKey("content"))throw Invalid("schema 和 content 必须且只能选择一项。");
        if(value["schema"] is JsonNode schema)return Reference(schema);
        if(value["content"] is not JsonObject content||content.Count!=1)throw Invalid("参数或头的 content 必须只有一种媒体类型。");
        return Content(content,pointer+"/content");
    }
    private JsonNode Content(JsonNode? raw,string pointer)
    {
        if(raw is not JsonObject content||content.Count==0)throw Invalid("content 必须为非空对象。");
        var branches=new JsonArray();
        foreach(var(media,node)in content){
            if(string.IsNullOrWhiteSpace(media)||node is not JsonObject value)throw Invalid("媒体类型不合法。");
            Fields(value,pointer+"/"+Escape(media),"schema","example","examples","encoding");
            if(value["encoding"] is JsonNode rawEncoding){
                if(rawEncoding is not JsonObject encodings)throw Invalid("encoding 必须为对象。");
                foreach(var(name,encodingNode)in encodings){if(encodingNode is not JsonObject encoding)throw Invalid("encoding 项必须为对象。");Fields(encoding,pointer+"/"+Escape(media)+"/encoding/"+Escape(name),"contentType","headers","style","explode","allowReserved");}
            }
            if(media.Contains('*',StringComparison.Ordinal))issue("media_type_range",pointer+"/"+Escape(media),"媒体范围的优先级尚未纳入有限证明。");
            var schema=value["schema"] is JsonNode shape?Reference(shape):JsonValue.Create(true)!;
            branches.Add(Object(new(){["mediaType"]=new JsonObject{["const"]=media},["value"]=schema},new("mediaType","value"),true));
        }
        return branches.Count==1?branches[0]!.DeepClone():new JsonObject{["anyOf"]=branches};
    }
    private string? Meaning(string name,JsonObject scopeRoot)
    {
        if(scopeRoot["components"]?["securitySchemes"] is not JsonObject schemes||schemes[name] is not JsonNode node)return null;
        var value=ResolveObject(node,"security_scheme");if(!Fields(value,"/components/securitySchemes/"+Escape(name),"$ref","type","description","name","in","scheme","bearerFormat","flows","openIdConnectUrl"))return null;
        var type=Text(value["type"]);JsonObject? meaning=null;
        if(type=="apiKey"&&Text(value["in"]) is "query" or "header" or "cookie"&&Text(value["name"]).Length>0)meaning=new(){["type"]=type,["in"]=value["in"]!.DeepClone(),["name"]=Text(value["in"])=="header"?Text(value["name"]).ToLowerInvariant():Text(value["name"])};
        else if(type=="http"&&Text(value["scheme"]).Length>0)meaning=new(){["type"]=type,["scheme"]=Text(value["scheme"]).ToLowerInvariant(),["bearerFormat"]=value["bearerFormat"]?.DeepClone()};
        else if(type=="oauth2"&&value["flows"] is JsonObject flows){
            var normalized=new JsonObject();foreach(var(flow,raw)in flows){
                if(flow is not("implicit" or "password" or "clientCredentials" or "authorizationCode")||raw is not JsonObject definition)return null;
                if(!Fields(definition,"/components/securitySchemes/"+Escape(name)+"/flows/"+Escape(flow),"authorizationUrl","tokenUrl","refreshUrl","scopes"))return null;
                if(flow is "implicit" or "authorizationCode"&&Text(definition["authorizationUrl"]).Length==0||flow is "password" or "clientCredentials" or "authorizationCode"&&Text(definition["tokenUrl"]).Length==0||definition["scopes"] is not JsonObject availableScopes||availableScopes.Any(x=>x.Value is not JsonValue scope||!scope.TryGetValue<string>(out _)))throw Invalid("OAuth flow 缺少必需 URL 或 scopes 定义。");
                normalized[flow]=new JsonObject{["authorizationUrl"]=definition["authorizationUrl"]?.DeepClone(),["tokenUrl"]=definition["tokenUrl"]?.DeepClone(),["refreshUrl"]=definition["refreshUrl"]?.DeepClone()};
            }meaning=new(){["type"]=type,["flows"]=normalized};
        }else if(type=="openIdConnect"&&Text(value["openIdConnectUrl"]).Length>0)meaning=new(){["type"]=type,["openIdConnectUrl"]=value["openIdConnectUrl"]!.DeepClone()};
        return meaning is null?null:ContractNormalizer.Canonical(meaning);
    }
    private Model Derive(JsonNode model,string direction)
    {
        ct.ThrowIfCancellationRequested();
        if(!adapted.TryGetValue(direction,out var documents)){
            var reader=new ContractDocumentReader();var nodes=new List<ContractDocument>();
            foreach(var document in bundle.Documents){
                var root=Adapt(document.Root,document.Dialect,!HasOpenApi(document.Root),"",direction);
                if(HasOpenApi(root))root["openapi"]="3.1.0";
                var adaptedSource=new ContractSource(document.Source.LogicalUri,root.ToJsonString(new JsonSerializerOptions{MaxDepth=128}),"json");
                nodes.Add(document.Source.LogicalUri==bundle.RootUri?reader.Read(adaptedSource,limits,ct):reader.ReadResource(adaptedSource,limits,ContractDialect.Oas31,ct));
            }adapted[direction]=documents=nodes;
        }
        var result=documents.ToList();var index=result.FindIndex(x=>x.Source.LogicalUri==bundle.RootUri);var documentRoot=(JsonObject)result[index].Root.DeepClone();
        documentRoot["components"]??=new JsonObject();documentRoot["components"]!["schemas"]??=new JsonObject();var schemas=documentRoot["components"]!["schemas"]!.AsObject();
        var name="__webapi_http_contract_v2";for(var i=0;schemas.ContainsKey(name);i++){if(i>=1000)throw Budget();name="__webapi_http_contract_v2_"+i;}
        schemas[name]=model.DeepClone();var source=new ContractSource(bundle.RootUri,documentRoot.ToJsonString(new JsonSerializerOptions{MaxDepth=128}),"json");result[index]=new ContractDocumentReader().Read(source,limits,ct);
        var derived=ContractBundleCodec.Create(bundle.RootUri,result,limits);var selected=derived.Documents.Single(x=>x.Source.LogicalUri==bundle.RootUri).Root["components"]!["schemas"]![name]!;
        return new(derived,selected,new(bundle.RootUri,"/components/schemas/"+Escape(name)));
    }
    private JsonNode Adapt(JsonNode node,ContractDialect dialect,bool schema,string pointer,string direction)
    {
        ct.ThrowIfCancellationRequested();
        if(schema){var prepared=DialectAdapter.PrepareSchema(node,dialect,direction,applyDirection:false);foreach(var error in prepared.Issues)issue(error.Code,pointer+error.Pointer,error.Message);return prepared.Node;}
        if(node is JsonObject obj){var result=new JsonObject();foreach(var(key,child)in obj){
            if(child is null){result[key]=null;continue;}
            var path=pointer+"/"+Escape(key);
            if(path=="/components/schemas"&&child is JsonObject map){var next=new JsonObject();foreach(var(name,shape)in map)next[name]=shape is null?null:Adapt(shape,dialect,true,path+"/"+Escape(name),direction);result[key]=next;}
            else if(key is "example" or "examples" or "default"||key.StartsWith("x-",StringComparison.Ordinal))result[key]=child.DeepClone();
            else result[key]=Adapt(child,dialect,key=="schema",path,direction);
        }return result;}
        if(node is JsonArray array)return new JsonArray(array.Select(x=>x is null?null:Adapt(x,dialect,false,pointer,direction)).ToArray());return node.DeepClone();
    }
    private JsonNode Reference(JsonNode schema)
    {
        var origin=graph.Describe(schema);var resource=graph.Resources.Single(x=>x.ResourceUri==origin.ResourceUri);
        return new JsonObject{["$ref"]=origin.ResourceUri.AbsoluteUri+"#"+Uri.EscapeDataString(origin.Pointer[resource.Pointer.Length..])};
    }
    internal JsonObject ResolveObject(JsonNode? node,string kind)
    {
        var seen=new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        while(node is JsonObject value&&value["$ref"] is JsonNode reference){
            if(!seen.Add(node)||seen.Count>64)throw new ApiException(422,"openapi_reference_cycle","OpenAPI 对象引用循环或超过预算。");
            Fields(value,"","$ref","summary","description");var text=Text(reference);if(text.Length==0)throw Invalid("OpenAPI 引用必须为字符串。");node=graph.Resolve(graph.Describe(node).ResourceUri,text).Node;
        }
        return node as JsonObject??throw Invalid(kind+" 必须为对象。");
    }
    private JsonObject ScopeRoot(JsonObject node)
    {
        if(node.Parent is null)return Root;var origin=graph.Describe(node);var source=graph.Resolve(origin.ResourceUri,"").Node as JsonObject;
        // External Operation/Path Item references use their containing document's
        // namespace. A standalone referenced object has no implicit root schemes.
        return source??Root;
    }
    private bool Fields(JsonObject node,string pointer,params string[] allowed){var known=true;foreach(var(key,_)in node)if(!allowed.Contains(key,StringComparer.Ordinal)){known=false;issue("unsupported_openapi_field",pointer+"/"+Escape(key),"该 OpenAPI 字段的行为未登记，原字段仍保留。");}return known;}
    private static IEnumerable<string> Schemes(JsonNode? security)=>security is JsonArray list?list.OfType<JsonObject>().SelectMany(x=>x.Select(y=>y.Key)).Distinct(StringComparer.Ordinal):[];
    private static bool EncodingEqual(JsonNode? a,JsonNode? b){
        if(a is not JsonObject left||b is not JsonObject right)return true;
        return left.Where(x=>right.ContainsKey(x.Key)).All(x=>JsonNode.DeepEquals(x.Value?["encoding"],right[x.Key]?["encoding"]));
    }
    private static string Wire(JsonObject value,string location){
        var style=Text(value["style"]);if(style.Length==0)style=location is "query" or "cookie"?"form":"simple";
        var explode=value.ContainsKey("explode")?Flag(value,"explode"):style=="form";
        return new JsonObject{["style"]=style,["explode"]=explode,["allowReserved"]=Flag(value,"allowReserved"),["allowEmptyValue"]=Flag(value,"allowEmptyValue"),["content"]=value["content"] is JsonObject c?new JsonArray(c.Select(x=>(JsonNode?)JsonValue.Create(x.Key)).OrderBy(x=>x!.ToJsonString(),StringComparer.Ordinal).ToArray()):null}.ToJsonString();
    }
    private static bool HasOpenApi(JsonNode node)=>node is JsonObject obj&&obj.ContainsKey("openapi");
    private static bool Flag(JsonObject obj,string key){if(!obj.ContainsKey(key))return false;return obj[key] is JsonValue v&&v.TryGetValue<bool>(out var flag)?flag:throw Invalid(key+" 必须为 boolean。");}
    private static JsonObject Object(JsonObject properties,JsonArray required,bool closed)=>new(){["type"]="object",["properties"]=properties,["required"]=required,["additionalProperties"]=!closed};
    private static string Text(JsonNode? node)=>ContractDocumentReader.Text(node);
    private static string Escape(string value)=>ContractDocumentReader.Escape(value);
    private static ApiException Invalid(string message)=>new(422,"invalid_openapi_contract",message);
    private static ApiException Budget()=>new(422,"openapi_model_budget","HTTP 契约模型超过预算。");
}

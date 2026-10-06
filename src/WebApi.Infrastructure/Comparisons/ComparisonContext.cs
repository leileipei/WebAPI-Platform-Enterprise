using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Comparisons;
namespace WebApi.Infrastructure.Comparisons;
internal sealed class ComparisonBudgetException : Exception;
internal sealed class ComparisonContext(ComparisonLimits limits,CancellationToken ct)
{
    private int nodes;
    public bool Invalid {get;private set;}
    public List<ComparisonFinding> Findings {get;}=[];
    private readonly Dictionary<string,ComparisonCoverageIssue> issues=new(StringComparer.Ordinal);
    public IReadOnlyList<ComparisonCoverageIssue> Issues=>issues.Values.OrderBy(x=>x.Source,StringComparer.Ordinal).ThenBy(x=>x.Pointer,StringComparer.Ordinal).ThenBy(x=>x.Code,StringComparer.Ordinal).ToArray();
    public void Issue(string code,string source,string pointer,string reason,bool invalid=false)
    {Invalid|=invalid;issues.TryAdd(source+"|"+pointer+"|"+code,new(code,source,pointer,reason));if(!invalid&&issues.Count>limits.MaxFindings)throw new ComparisonBudgetException();}
    public void Check(int depth=0){ct.ThrowIfCancellationRequested();if(++nodes>limits.MaxNodes||depth>limits.MaxDepth)throw new ComparisonBudgetException();}
    public JsonNode? Parse(string? text,string source,string pointer)
    {
        if(text is null)return null;
        try
        {
            using var d=JsonDocument.Parse(text,new(){MaxDepth=limits.MaxDepth});Inspect(d.RootElement,source,pointer,0);
            if(Invalid)return null;
            return JsonNode.Parse(text,new(),new(){MaxDepth=limits.MaxDepth});
        }
        catch(JsonException){Issue("invalid_json",source,pointer,"JSON 格式损坏或嵌套深度超限。",true);return null;}
    }
    private void Inspect(JsonElement node,string source,string pointer,int depth)
    {
        Check(depth);
        if(node.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in node.EnumerateObject()){if(!names.Add(p.Name))Issue("duplicate_key",source,pointer,"JSON 存在重复键。",true);Inspect(p.Value,source,pointer+"/"+ContractNormalizer.Pointer(p.Name),depth+1);}}
        else if(node.ValueKind==JsonValueKind.Array){var i=0;foreach(var item in node.EnumerateArray())Inspect(item,source,pointer+"/"+i++,depth+1);}
    }
    public JsonNode? Resolve(JsonNode? schema,JsonNode? root,string source,string pointer,HashSet<string>? visited=null,int depth=0)
    {
        Check(depth);if(schema is not JsonObject o){if(schema is not null)Issue("unsupported_schema",source,pointer,"Schema 不是受支持的对象结构。");return schema?.DeepClone();}
        if(o.ContainsKey("$ref")&&(o["$ref"] is not JsonValue refValue||!refValue.TryGetValue<string>(out var referenceText)||string.IsNullOrWhiteSpace(referenceText))){Issue("invalid_reference",source,pointer+"/$ref","$ref 必须为非空字符串。",true);return o.DeepClone();}
        if(o["$ref"] is JsonValue reference)
        {
            var value=reference.TryGetValue<string>(out var s)?s:null;visited??=new(StringComparer.Ordinal);
            if(value is null||!value.StartsWith("#/",StringComparison.Ordinal)||root is null||!visited.Add(value)){Issue("unresolved_ref",source,pointer,"外部、循环或无法解析的引用需人工评审。");return new JsonObject { ["$ref"]=value };}
            if(o.Count>1)Issue("ref_siblings",source,pointer,"引用旁的附加约束不能自动完整判断。");
            JsonNode? target=root;
            foreach(var token in value[2..].Split('/'))
            {var key=token.Replace("~1","/",StringComparison.Ordinal).Replace("~0","~",StringComparison.Ordinal);target=target is JsonObject obj?obj[key]:target is JsonArray array&&int.TryParse(key,out var index)&&index>=0&&index<array.Count?array[index]:null;}
            if(target is null){Issue("unresolved_ref",source,pointer,"本地引用目标不存在。");return new JsonObject { ["$ref"]=value };}
            return Resolve(target,root,source,pointer,new(visited,StringComparer.Ordinal),depth+1);
        }
        if(o["type"] is null)Issue("missing_schema_type",source,pointer,"Schema 缺少明确类型，自动判断范围有限。");
        else if(o["type"] is not JsonValue typeValue||!typeValue.TryGetValue<string>(out var type))Issue("invalid_schema_type",source,pointer,"Schema 类型必须是字符串。",true);
        else if(type is not ("string" or "integer" or "number" or "boolean" or "object" or "array"))Issue("unsupported_schema_type",source,pointer,"未支持的 Schema 类型。");
        if(o.ContainsKey("properties")&&o["properties"] is not JsonObject)Issue("invalid_properties",source,pointer,"properties 必须是对象。",true);
        if(o.ContainsKey("required")&&(o["required"] is not JsonArray required||required.Any(x=>x is not JsonValue v||!v.TryGetValue<string>(out _))))Issue("invalid_required",source,pointer,"required 必须是字段名数组。",true);
        if(o.ContainsKey("enum")&&(o["enum"] is not JsonArray values||values.Count==0))Issue("invalid_enum",source,pointer,"enum 必须是非空数组。",true);
        if(o.ContainsKey("nullable")&&(o["nullable"] is not JsonValue nullable||!nullable.TryGetValue<bool>(out _)))Issue("invalid_nullable",source,pointer,"nullable 必须是布尔值。",true);
        foreach(var bound in new[]{"minimum","maximum","minLength","maxLength","minItems","maxItems"})if(o.ContainsKey(bound)&&(o[bound] is not JsonValue value||!value.TryGetValue<decimal>(out _)))Issue("invalid_bound",source,pointer+"/"+bound,"约束值必须是可解析数值。",true);
        if(o.ContainsKey("items")&&o["items"] is null)Issue("invalid_items",source,pointer+"/items","items 不能为 null。",true);
        var result=new JsonObject();
        foreach(var p in o)
        {
            if(p.Key=="properties"&&p.Value is JsonObject properties){var next=new JsonObject();foreach(var property in properties){if(property.Value is null)Issue("invalid_property_schema",source,pointer+"/properties/"+ContractNormalizer.Pointer(property.Key),"属性 Schema 不能为 null。",true);next[property.Key]=Resolve(property.Value,root,source,pointer+"/properties/"+ContractNormalizer.Pointer(property.Key),visited is null?null:new(visited,StringComparer.Ordinal),depth+1);}result[p.Key]=next;}
            else if(p.Key=="items")result[p.Key]=Resolve(p.Value,root,source,pointer+"/items",visited is null?null:new(visited,StringComparer.Ordinal),depth+1);
            else {result[p.Key]=p.Value?.DeepClone();if(!SchemaCompatibilityRules.KnownKeys.Contains(p.Key))Issue("unsupported_keyword",source,pointer+"/"+ContractNormalizer.Pointer(p.Key),"该 Schema 关键字超出当前自动判断范围。");}
        }
        return result;
    }
    public void Finding(string source,string? operation,string pointer,string change,string risk,string reason,JsonNode? before=null,JsonNode? after=null,bool metadata=false)
    {
        ct.ThrowIfCancellationRequested();if(Findings.Count>=limits.MaxFindings)throw new ComparisonBudgetException();
        JsonElement? Element(JsonNode? n)=>n is null?null:JsonSerializer.SerializeToElement(ContractNormalizer.Safe(n));
        Findings.Add(new(source+"|"+operation+"|"+pointer+"|"+change,source,operation,pointer,change,risk,reason,metadata?null:Element(before),metadata?null:Element(after)));
    }
}

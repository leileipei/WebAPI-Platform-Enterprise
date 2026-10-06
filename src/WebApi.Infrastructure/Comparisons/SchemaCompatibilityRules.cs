using System.Text.Json.Nodes;
namespace WebApi.Infrastructure.Comparisons;
internal static class SchemaCompatibilityRules
{
    internal static readonly HashSet<string> KnownKeys=["type","format","properties","items","required","enum","nullable","minimum","maximum","minLength","maxLength","minItems","maxItems","description","title","example","examples","$ref"];
    private static readonly HashSet<string> Metadata=["description","title","example","examples"];
    internal static string Text(JsonNode? n)=>n is JsonValue v&&v.TryGetValue<string>(out var s)?s:"";
    internal static bool Flag(JsonNode? n)=>n is JsonValue v&&v.TryGetValue<bool>(out var b)&&b;
    private static bool Equal(JsonNode? a,JsonNode? b)=>ContractNormalizer.Canonical(ContractNormalizer.Normalize(a))==ContractNormalizer.Canonical(ContractNormalizer.Normalize(b));
    private static HashSet<string> Strings(JsonNode? a)=>a is JsonArray array?array.Select(x=>ContractNormalizer.Canonical(x)).ToHashSet(StringComparer.Ordinal):[];
    private static string Direction(string direction,string request,string response)=>direction=="request"?request:direction=="response"?response:"Unknown";
    internal static void Compare(ComparisonContext c,JsonNode? before,JsonNode? after,string direction,string source,string? operation,string pointer)
    {
        c.Check();if(Equal(before,after))return;
        if(before is not JsonObject a||after is not JsonObject b){c.Finding(source,operation,pointer,"Changed","Unknown","无法自动判断该 Schema 结构变化。",before,after);return;}
        var oldRequired=Strings(a["required"]);var newRequired=Strings(b["required"]);
        var ap=a["properties"] as JsonObject??new();var bp=b["properties"] as JsonObject??new();
        foreach(var name in ap.Select(p=>p.Key).Union(bp.Select(p=>p.Key),StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var path=pointer+"/properties/"+ContractNormalizer.Pointer(name);var key=ContractNormalizer.Canonical(JsonValue.Create(name));
            if(!ap.ContainsKey(name))c.Finding(source,operation,path,"Added",direction=="request"?(newRequired.Contains(key)?"Breaking":"Compatible"):"Unknown","新增字段；请求必填项和客户端响应假设需区分。",null,bp[name]);
            else if(!bp.ContainsKey(name))c.Finding(source,operation,path,"Removed",Direction(direction,"Unknown","Breaking"),"字段移除可能使旧契约失效。",ap[name]);
            else
            {
                if(oldRequired.Contains(key)!=newRequired.Contains(key))c.Finding(source,operation,path+"/required","Changed",Direction(direction,newRequired.Contains(key)?"Breaking":"Compatible",newRequired.Contains(key)?"Compatible":"Breaking"),"字段必填性发生变化。",JsonValue.Create(oldRequired.Contains(key)),JsonValue.Create(newRequired.Contains(key)));
                Compare(c,ap[name],bp[name],direction,source,operation,path);
            }
        }
        foreach(var key in a.Select(p=>p.Key).Union(b.Select(p=>p.Key),StringComparer.Ordinal).Where(x=>x is not ("properties" or "required")).Order(StringComparer.Ordinal))
        {
            if(Equal(a[key],b[key]))continue;var risk="Unknown";var metadata=Metadata.Contains(key);
            if(metadata)risk="Compatible";
            else if(key=="enum")
            {
                var old=Strings(a[key]);var next=Strings(b[key]);
                if(a[key] is JsonArray&&b[key] is JsonArray)risk=Direction(direction,old.Except(next).Any()?"Breaking":"Compatible",next.Except(old).Any()?"Breaking":"Compatible");
                else risk=Direction(direction,a[key] is null?"Breaking":"Compatible",a[key] is null?"Compatible":"Breaking");
            }
            else if(key is "minimum" or "maximum" or "minLength" or "maxLength" or "minItems" or "maxItems")
            {
                bool? tightening=a[key] is null?true:b[key] is null?false:null;
                if(a[key] is JsonValue av&&b[key] is JsonValue bv&&av.TryGetValue<decimal>(out var old)&&bv.TryGetValue<decimal>(out var next))tightening=key.StartsWith("min",StringComparison.Ordinal)?next>old:next<old;
                if(tightening is bool value)risk=Direction(direction,value?"Breaking":"Compatible",value?"Compatible":"Breaking");
            }
            else if(key=="nullable"&&a[key] is JsonValue an&&b[key] is JsonValue bn&&an.TryGetValue<bool>(out var oldNullable)&&bn.TryGetValue<bool>(out var newNullable))risk=Direction(direction,oldNullable&&!newNullable?"Breaking":"Compatible",!oldNullable&&newNullable?"Breaking":"Compatible");
            else if(key=="items"){Compare(c,a[key],b[key],direction,source,operation,pointer+"/items");continue;}
            c.Finding(source,operation,pointer+"/"+ContractNormalizer.Pointer(key),"Changed",risk,metadata?"仅元数据变化；示例原值不回显。":"Schema 约束变化，按请求/响应方向判断。",a[key],b[key],metadata);
        }
        if(!oldRequired.SetEquals(newRequired)&&oldRequired.Union(newRequired).Any(x=>!ap.ContainsKey(Text(JsonNode.Parse(x)))&&!bp.ContainsKey(Text(JsonNode.Parse(x)))))c.Finding(source,operation,pointer+"/required","Changed","Unknown","必填集合包含未展开的字段。",a["required"],b["required"]);
    }
}

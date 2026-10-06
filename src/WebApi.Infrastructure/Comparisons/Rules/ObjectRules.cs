using System.Text.Json.Nodes;
namespace WebApi.Infrastructure.Comparisons.Rules;
internal static class ObjectRules
{
    internal static bool Prove(JsonObject a,JsonObject b,ProofContext c)
    {
        if(!PrimitiveRules.Types(a).Contains("object"))return true;c.Check();var ar=PrimitiveRules.Required(a);var br=PrimitiveRules.Required(b);if(!br.IsSubsetOf(ar))return false;
        var ap=a["properties"] as JsonObject??new();var bp=b["properties"] as JsonObject??new();var ax=a["patternProperties"] as JsonObject??new();var bx=b["patternProperties"] as JsonObject??new();
        if(!ax.Select(x=>x.Key).ToHashSet(StringComparer.Ordinal).SetEquals(bx.Select(x=>x.Key)))return false;
        foreach(var(key,node)in bx)if(!c.Included(ax[key]!,node!))return false;
        foreach(var name in ap.Select(x=>x.Key).Union(bp.Select(x=>x.Key),StringComparer.Ordinal)){
            var from=ForName(a,name,c);var to=ForName(b,name,c);if(!c.Included(from,to))return false;
        }
        if(!c.Included(PrimitiveRules.Child(a,"additionalProperties"),PrimitiveRules.Child(b,"additionalProperties")))return false;
        if(!c.Included(PrimitiveRules.Child(a,"propertyNames"),PrimitiveRules.Child(b,"propertyNames")))return false;
        var amin=Math.Max(ar.Count,PrimitiveRules.Size(a,"minProperties",0));var bmin=PrimitiveRules.Size(b,"minProperties",0);var amax=PrimitiveRules.Size(a,"maxProperties",int.MaxValue);var bmax=PrimitiveRules.Size(b,"maxProperties",int.MaxValue);
        if(PrimitiveRules.False(PrimitiveRules.Child(a,"additionalProperties"))&&ax.Count==0)amax=Math.Min(amax,ap.Count);if(amin>amax)return true;if(amin<bmin||amax>bmax)return false;
        if(!Dependencies(a,b,c))return false;return true;
    }
    internal static JsonNode ForName(JsonObject obj,string name,ProofContext c)
    {
        var properties=obj["properties"] as JsonObject;var patterns=obj["patternProperties"] as JsonObject;var schemas=new List<JsonNode>();if(properties?.TryGetPropertyValue(name,out var property)==true&&property is not null)schemas.Add(property);
        if(patterns is not null)foreach(var(pattern,node)in patterns){c.Check();if(c.PropertyPatternMatches(pattern,name)&&node is not null)schemas.Add(node);}
        if(schemas.Count==0)return PrimitiveRules.Child(obj,"additionalProperties");return schemas.Count==1?schemas[0]:new JsonObject{["allOf"]=new JsonArray(schemas.Select(x=>x.DeepClone()).ToArray())};
    }
    private static bool Dependencies(JsonObject a,JsonObject b,ProofContext c)
    {
        foreach(var key in new[]{"dependentRequired","dependentSchemas"})if(b[key] is JsonObject target){var source=a[key] as JsonObject??new();foreach(var(name,value)in target){
            if(key=="dependentSchemas"){if(!source.ContainsKey(name)||!c.Included(source[name]!,value!))return false;}
            else{var needed=(value as JsonArray)!.Select(PrimitiveRules.Text).ToHashSet(StringComparer.Ordinal);var guaranteed=PrimitiveRules.Required(a);if(source[name] is JsonArray list)guaranteed.UnionWith(list.Select(PrimitiveRules.Text));if(!needed.IsSubsetOf(guaranteed))return false;}
        }}return true;
    }
}

using System.Text.Json.Nodes;
namespace WebApi.Infrastructure.Comparisons.Rules;
internal static class CompositionRules
{
    internal static bool Prove(JsonNode a,JsonNode b,ProofContext c)
    {
        c.Check();if(a is not JsonObject from||b is not JsonObject to)return false;
        // Any sufficient branch implication proves inclusion, never failure of inclusion.
        if(Only(to,"allOf")&&to["allOf"] is JsonArray allTarget)return allTarget.All(x=>x is not null&&c.Included(a,x));
        if(Only(from,"anyOf")&&from["anyOf"] is JsonArray anySource)return anySource.All(x=>x is not null&&c.Included(x,b));
        if(Only(to,"anyOf")&&to["anyOf"] is JsonArray anyTarget){
            if(Only(from,"oneOf")&&from["oneOf"] is JsonArray one&&JsonNode.DeepEquals(one,anyTarget))return true;
            if(anyTarget.Any(x=>x is not null&&c.Included(a,x)))return true;
        }
        if(Only(from,"allOf")&&from["allOf"] is JsonArray allSource&&allSource.Any(x=>x is not null&&c.Included(x,b)))return true;
        return false;
    }
    private static bool Only(JsonObject obj,string key)=>obj.ContainsKey(key)&&obj.All(x=>x.Key==key||PrimitiveRules.Metadata.Contains(x.Key));
}

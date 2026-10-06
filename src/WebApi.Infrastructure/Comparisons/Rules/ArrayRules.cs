using System.Text.Json.Nodes;
namespace WebApi.Infrastructure.Comparisons.Rules;
internal static class ArrayRules
{
    internal static bool Prove(JsonObject a,JsonObject b,ProofContext c)
    {
        if(!PrimitiveRules.Types(a).Contains("array"))return true;c.Check();var amin=PrimitiveRules.Size(a,"minItems",0);var bmin=PrimitiveRules.Size(b,"minItems",0);var amax=PrimitiveRules.Size(a,"maxItems",int.MaxValue);var bmax=PrimitiveRules.Size(b,"maxItems",int.MaxValue);if(amin>amax)return true;if(amin<bmin||amax>bmax)return false;
        var ap=a["prefixItems"] as JsonArray??new();var bp=b["prefixItems"] as JsonArray??new();for(var i=0;i<Math.Max(ap.Count,bp.Count)&&i<amax;i++)if(!c.Included(i<ap.Count?ap[i]!:PrimitiveRules.Child(a,"items"),i<bp.Count?bp[i]!:PrimitiveRules.Child(b,"items")))return false;
        if(amax>Math.Max(ap.Count,bp.Count)&&!c.Included(PrimitiveRules.Child(a,"items"),PrimitiveRules.Child(b,"items")))return false;
        if(PrimitiveRules.Flag(b["uniqueItems"])&&!PrimitiveRules.Flag(a["uniqueItems"]))return false;
        if(b["contains"] is JsonNode target){if(a["contains"] is not JsonNode source||!JsonNode.DeepEquals(source,target))return false;if(PrimitiveRules.Size(a,"minContains",1)<PrimitiveRules.Size(b,"minContains",1)||PrimitiveRules.Size(a,"maxContains",int.MaxValue)>PrimitiveRules.Size(b,"maxContains",int.MaxValue))return false;}return true;
    }
}

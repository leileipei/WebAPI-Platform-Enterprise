using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace WebApi.Infrastructure.Comparisons.Rules;
internal static class ProofWitnesses
{
    internal static IReadOnlyList<JsonNode?> Generate(JsonNode a,JsonNode b,ProofContext c)
    {
        var results=new List<JsonNode?>();var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var value in new[]{"null","0","1","-1","0.5","true","false","\"\"","\"a\"","\"b\"","\"x\"","[]","{}"})Add(JsonNode.Parse(value));
        Inspect(a,0);Inspect(b,0);return results;
        void Add(JsonNode? value){c.Check();if(results.Count>=256)return;var text=value?.ToJsonString()??"null";if(System.Text.Encoding.UTF8.GetByteCount(text)>256*1024)return;if(seen.Add(text))results.Add(value?.DeepClone());}
        void Inspect(JsonNode node,int depth){c.Check();if(depth>8||node is not JsonObject obj)return;
            if(PrimitiveRules.FiniteValues(obj) is IReadOnlyList<JsonNode?> finite)foreach(var value in finite.Take(32))Add(value);
            foreach(var key in new[]{"minimum","maximum","exclusiveMinimum","exclusiveMaximum","multipleOf"})if(PrimitiveRules.Number(obj[key]) is ExactNumber number){foreach(var x in new[]{number,number-ExactNumber.One,number+ExactNumber.One,number-ExactNumber.Half,number+ExactNumber.Half,number*new ExactNumber(2,1)})Add(x.Node());}
            foreach(var key in new[]{"minLength","maxLength"})if(obj[key] is not null){var size=PrimitiveRules.Size(obj,key,0);foreach(var n in new[]{size-1,size,size+1})if(n>=0&&n<=64)Add(JsonValue.Create(new string('a',n)));}
            if(obj["pattern"] is JsonValue pattern){var text=PrimitiveRules.Text(pattern);var literal=Regex.Match(text,"^\\^([a-zA-Z0-9_-]+)\\$$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));if(literal.Success)Add(JsonValue.Create(literal.Groups[1].Value));foreach(var n in new[]{2,3,5,8})Add(JsonValue.Create(new string('a',n)));}
            if(PrimitiveRules.Types(obj).Contains("object"))Objects(obj);if(PrimitiveRules.Types(obj).Contains("array"))Arrays(obj);
            foreach(var key in new[]{"allOf","anyOf","oneOf","prefixItems"})if(obj[key] is JsonArray list)foreach(var child in list)if(child is not null)Inspect(child,depth+1);
            foreach(var key in new[]{"not","if","then","else","items","contains","propertyNames"})if(obj[key] is JsonNode child)Inspect(child,depth+1);
        }
        void Objects(JsonObject obj){var required=PrimitiveRules.Required(obj);var properties=obj["properties"] as JsonObject??new();var baseObject=new JsonObject();foreach(var name in required)baseObject[name]=Sample(properties[name]??PrimitiveRules.Any(),0);Add(baseObject);Add(new JsonObject());
            var names=properties.Select(x=>x.Key).Concat(new[]{"extra","a","b","id","x-"}).ToHashSet(StringComparer.Ordinal);
            if(obj["patternProperties"] is JsonObject patterns)foreach(var(pattern,_)in patterns){var prefix=Regex.Match(pattern,"^\\^([a-zA-Z0-9_-]+)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));if(prefix.Success)names.Add(prefix.Groups[1].Value);}
            if(obj["propertyNames"] is JsonObject nameSchema&&PrimitiveRules.FiniteValues(nameSchema) is IReadOnlyList<JsonNode?> keys)foreach(var key in keys){var name=PrimitiveRules.Text(key);if(name.Length>0)names.Add(name);}
            if(obj["dependentRequired"] is JsonObject deps)names.UnionWith(deps.Select(x=>x.Key));if(obj["dependentSchemas"] is JsonObject depSchemas)names.UnionWith(depSchemas.Select(x=>x.Key));
            foreach(var name in names.Take(24))foreach(var value in new JsonNode?[]{null,JsonValue.Create(0),JsonValue.Create(0.5),JsonValue.Create(""),JsonValue.Create(true)}.Concat(Samples(properties[name]??PrimitiveRules.Any(),0,c))){var next=(JsonObject)baseObject.DeepClone();next[name]=value?.DeepClone();Add(next);}
            foreach(var name in required){var next=(JsonObject)baseObject.DeepClone();next.Remove(name);Add(next);}
            foreach(var key in new[]{"minProperties","maxProperties"})if(obj[key] is not null){var size=PrimitiveRules.Size(obj,key,0);foreach(var count in new[]{size-1,size,size+1})if(count>=0&&count<=8){var next=new JsonObject();for(var i=0;i<count;i++)next["p"+i]=null;Add(next);}}
        }
        void Arrays(JsonObject obj){var prefix=obj["prefixItems"] as JsonArray??new();var lengths=new HashSet<int>{0,1,2,3};foreach(var key in new[]{"minItems","maxItems","minContains","maxContains"})if(obj[key] is not null){var n=PrimitiveRules.Size(obj,key,0);foreach(var x in new[]{n-1,n,n+1})if(x>=0&&x<=8)lengths.Add(x);}lengths.Add(Math.Min(prefix.Count+1,8));
            foreach(var length in lengths){var original=new JsonArray();for(var i=0;i<length;i++)original.Add(Sample(i<prefix.Count?prefix[i]!:PrimitiveRules.Child(obj,"items"),0));Add(original);foreach(var value in new JsonNode?[]{null,JsonValue.Create(0),JsonValue.Create(0.5),JsonValue.Create(""),JsonValue.Create(true)}){var uniform=new JsonArray();for(var i=0;i<length;i++)uniform.Add(i<prefix.Count?Sample(prefix[i]!,0):value?.DeepClone());Add(uniform);for(var i=0;i<length;i++){var next=(JsonArray)original.DeepClone();next[i]=value?.DeepClone();Add(next);}}}
        }
    }
    private static JsonNode? Sample(JsonNode schema,int depth)
    {
        if(depth>4)return null;if(PrimitiveRules.FiniteValues(schema) is IReadOnlyList<JsonNode?> values&&values.Count>0)return values[0]?.DeepClone();if(schema is not JsonObject obj)return null;
        foreach(var key in new[]{"anyOf","oneOf"})if(obj[key] is JsonArray alternatives&&alternatives.FirstOrDefault(x=>x is not null) is JsonNode branch)return Sample(branch,depth+1);
        var types=PrimitiveRules.Types(obj);if(types.Contains("integer")||types.Contains("fraction"))return(PrimitiveRules.Number(obj["minimum"])??PrimitiveRules.Number(obj["multipleOf"])??ExactNumber.Zero).Node();if(types.Contains("string"))return JsonValue.Create(new string('a',Math.Min(PrimitiveRules.Size(obj,"minLength",0),16)));if(types.Contains("boolean"))return JsonValue.Create(false);if(types.Contains("object")){var result=new JsonObject();foreach(var name in PrimitiveRules.Required(obj))result[name]=Sample(obj["properties"]?[name]??PrimitiveRules.Any(),depth+1);return result;}if(types.Contains("array"))return new JsonArray();return null;
    }
    private static IEnumerable<JsonNode?> Samples(JsonNode schema,int depth,ProofContext c)
    {
        c.Check();if(depth>8)yield break;
        if(PrimitiveRules.FiniteValues(schema) is IReadOnlyList<JsonNode?> finite){foreach(var value in finite.Take(8))yield return value?.DeepClone();yield break;}
        if(schema is not JsonObject obj)yield break;
        foreach(var key in new[]{"anyOf","oneOf"})if(obj[key] is JsonArray alternatives){foreach(var branch in alternatives.Take(8))if(branch is not null)foreach(var candidate in Samples(branch,depth+1,c).Take(8))yield return candidate;yield break;}
        var seed=Sample(schema,0);yield return seed;
        if(seed is not JsonObject&&seed is not JsonArray){
            foreach(var key in new[]{"minimum","maximum","exclusiveMinimum","exclusiveMaximum","multipleOf"})if(PrimitiveRules.Number(obj[key]) is ExactNumber number)foreach(var value in new[]{number,number-ExactNumber.One,number+ExactNumber.One,number-ExactNumber.Half,number+ExactNumber.Half})yield return value.Node();
            foreach(var key in new[]{"minLength","maxLength"})if(obj[key] is not null){var length=PrimitiveRules.Size(obj,key,0);foreach(var size in new[]{length-1,length,length+1})if(size>=0&&size<=64)yield return JsonValue.Create(new string('a',size));}
            foreach(var value in new[]{"null","0","0.5","true","false","\"\"","\"a\""})yield return JsonNode.Parse(value);
        }
        if(seed is JsonObject instance&&obj["properties"] is JsonObject properties){
            foreach(var(name,shape)in properties.Take(24))if(shape is not null)foreach(var candidate in Samples(shape,depth+1,c).Take(8)){
                c.Check();var next=(JsonObject)instance.DeepClone();next[name]=candidate?.DeepClone();yield return next;
            }
        }
    }
}

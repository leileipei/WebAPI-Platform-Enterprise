using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Comparisons.Rules;
internal readonly record struct ExactNumber:IComparable<ExactNumber>
{
    internal BigInteger Numerator{get;}internal BigInteger Denominator{get;}
    internal ExactNumber(BigInteger numerator,BigInteger denominator){if(denominator==0)throw new DivideByZeroException();if(denominator<0){numerator=-numerator;denominator=-denominator;}var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator),denominator);Numerator=numerator/gcd;Denominator=denominator/gcd;}
    internal static ExactNumber Parse(string text){var match=Regex.Match(text,"^(-?)([0-9]+)(?:\\.([0-9]+))?(?:[eE]([+-]?[0-9]+))?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));if(!match.Success||match.Groups[2].Length+match.Groups[3].Length>512||match.Groups[4].Length>4)throw Budget();var exponent=match.Groups[4].Success?int.Parse(match.Groups[4].Value):0;var scale=match.Groups[3].Length-exponent;if(Math.Abs(scale)>256)throw Budget();var numerator=BigInteger.Parse(match.Groups[2].Value+match.Groups[3].Value);if(match.Groups[1].Value=="-")numerator=-numerator;return scale>=0?new(numerator,BigInteger.Pow(10,scale)):new(numerator*BigInteger.Pow(10,-scale),1);}
    private static ApiException Budget()=>new(422,"schema_number_budget","数值精度或指数超过精确证明预算。");
    public int CompareTo(ExactNumber other)=>(Numerator*other.Denominator).CompareTo(other.Numerator*Denominator);
    public static ExactNumber operator +(ExactNumber a,ExactNumber b)=>new(a.Numerator*b.Denominator+b.Numerator*a.Denominator,a.Denominator*b.Denominator);
    public static ExactNumber operator -(ExactNumber a,ExactNumber b)=>a+new ExactNumber(-b.Numerator,b.Denominator);
    public static ExactNumber operator *(ExactNumber a,ExactNumber b)=>new(a.Numerator*b.Numerator,a.Denominator*b.Denominator);
    public static ExactNumber operator /(ExactNumber a,ExactNumber b)=>new(a.Numerator*b.Denominator,a.Denominator*b.Numerator);
    internal JsonNode Node(){for(var scale=0;scale<=256;scale++){var factor=BigInteger.Pow(10,scale);if(factor%Denominator!=0)continue;var value=Numerator*(factor/Denominator);var text=BigInteger.Abs(value).ToString();if(scale>0){text=text.PadLeft(scale+1,'0');text=text.Insert(text.Length-scale,".");}return JsonNode.Parse((value<0?"-":"")+text)!;}throw Budget();}
    internal static readonly ExactNumber Zero=new(0,1),One=new(1,1),Half=new(1,2);
}
internal static class PrimitiveRules
{
    internal static readonly HashSet<string> Metadata=["title","description","default","examples","example","deprecated","readOnly","writeOnly","xml","externalDocs","contentEncoding","contentMediaType","$comment","$schema","$id","$anchor","$defs","definitions"];
    private static readonly HashSet<string> Simple=["type","enum","const","minimum","maximum","exclusiveMinimum","exclusiveMaximum","multipleOf","minLength","maxLength","pattern","format","properties","required","additionalProperties","patternProperties","propertyNames","minProperties","maxProperties","dependentRequired","dependentSchemas","items","prefixItems","minItems","maxItems","uniqueItems","contains","minContains","maxContains"];
    private static readonly HashSet<string> Universe=["string","integer","fraction","boolean","null","object","array"];
    internal static bool False(JsonNode node)=>node is JsonValue v&&v.TryGetValue<bool>(out var value)&&!value;
    internal static bool True(JsonNode node)=>node is JsonValue v&&v.TryGetValue<bool>(out var value)&&value||node is JsonObject obj&&obj.Count==0;
    internal static IReadOnlyList<JsonNode?>? FiniteValues(JsonNode node){if(node is not JsonObject obj)return null;if(obj.TryGetPropertyValue("const",out var value))return[value?.DeepClone()];return obj["enum"] is JsonArray array?array.Select(x=>x?.DeepClone()).ToArray():null;}
    internal static HashSet<string> Types(JsonNode node)
    {
        if(False(node))return[];if(node is not JsonObject obj)return new(Universe,StringComparer.Ordinal);var types=new HashSet<string>(StringComparer.Ordinal);
        if(obj["type"] is JsonValue value&&value.TryGetValue<string>(out var name))Add(name);else if(obj["type"] is JsonArray array)foreach(var child in array)Add(Text(child));else types.UnionWith(Universe);
        if(obj["allOf"] is JsonArray all)foreach(var child in all)if(child is not null)types.IntersectWith(Types(child));
        foreach(var key in new[]{"anyOf","oneOf"})if(obj[key] is JsonArray branches){var union=new HashSet<string>(StringComparer.Ordinal);foreach(var child in branches)if(child is not null)union.UnionWith(Types(child));types.IntersectWith(union);}return types;
        void Add(string text){if(text=="number"){types.Add("integer");types.Add("fraction");}else types.Add(text);}
    }
    internal static bool TypeSubset(JsonNode a,JsonNode b)=>Types(a).IsSubsetOf(Types(b));
    internal static bool TypeOnly(JsonNode node,string mode)=>True(node)||node is JsonObject obj&&obj.All(x=>x.Key=="type"||Metadata.Contains(x.Key)||x.Key=="format"&&mode=="Annotation");
    internal static bool SimpleKeys(JsonObject a,JsonObject b)=>a.Concat(b).All(p=>Simple.Contains(p.Key)||Metadata.Contains(p.Key));
    internal static bool Prove(JsonObject a,JsonObject b,ProofContext c)
    {
        c.Check();if(!TypeSubset(a,b))return false;var types=Types(a);if(types.Count==0)return true;
        if(types.Contains("integer")||types.Contains("fraction")){if(!Bounds(a,b,"minimum","maximum",true)||!Multiple(a,b,types))return false;}
        if(types.Contains("string")){
            if(!Bounds(a,b,"minLength","maxLength",false))return false;if(b["pattern"] is not null&&!JsonNode.DeepEquals(a["pattern"],b["pattern"]))return false;
            if(c.FormatMode=="Strict"&&b["format"] is not null&&!JsonNode.DeepEquals(a["format"],b["format"]))return false;
        }
        if(b.ContainsKey("enum")||b.ContainsKey("const"))return false;return true;
    }
    internal static ExactNumber? Number(JsonNode? node)=>node is JsonValue value&&value.GetValueKind()==JsonValueKind.Number?ExactNumber.Parse(value.ToJsonString()):null;
    internal static bool Bounds(JsonObject a,JsonObject b,string min,string max,bool exclusive)
    {
        var alo=Number(a[min]);var blo=Number(b[min]);var ahi=Number(a[max]);var bhi=Number(b[max]);var ae=false;var be=false;var ahe=false;var bhe=false;
        if(!exclusive){alo??=ExactNumber.Zero;blo??=ExactNumber.Zero;}
        if(exclusive){if(Number(a["exclusiveMinimum"]) is ExactNumber x&&(alo is null||x.CompareTo(alo.Value)>=0)){alo=x;ae=true;}if(Number(b["exclusiveMinimum"]) is ExactNumber y&&(blo is null||y.CompareTo(blo.Value)>=0)){blo=y;be=true;}if(Number(a["exclusiveMaximum"]) is ExactNumber z&&(ahi is null||z.CompareTo(ahi.Value)<=0)){ahi=z;ahe=true;}if(Number(b["exclusiveMaximum"]) is ExactNumber t&&(bhi is null||t.CompareTo(bhi.Value)<=0)){bhi=t;bhe=true;}}
        if(alo is not null&&ahi is not null&&(alo.Value.CompareTo(ahi.Value)>0||alo==ahi&&(ae||ahe)))return true;
        if(blo is not null&&(alo is null||alo.Value.CompareTo(blo.Value)<0||alo==blo&&be&&!ae))return false;if(bhi is not null&&(ahi is null||ahi.Value.CompareTo(bhi.Value)>0||ahi==bhi&&bhe&&!ahe))return false;return true;
    }
    private static bool Multiple(JsonObject a,JsonObject b,HashSet<string> types)
    {
        if(Number(b["multipleOf"]) is not ExactNumber target)return true;var source=Number(a["multipleOf"]);
        if(!types.Contains("fraction")){if(source is null)source=ExactNumber.One;else source=new ExactNumber(BigInteger.Abs(source.Value.Numerator),1);}
        if(source is null||target.Numerator<=0)return false;return(source.Value/target).Denominator==1;
    }
    internal static string Text(JsonNode? node)=>node is JsonValue v&&v.TryGetValue<string>(out var text)?text:"";
    internal static bool Flag(JsonNode? node)=>node is JsonValue v&&v.TryGetValue<bool>(out var flag)&&flag;
    internal static JsonNode Any()=>new JsonObject();
    internal static JsonNode Child(JsonObject obj,string key)=>obj[key]??Any();
    internal static HashSet<string> Required(JsonObject obj)=>obj["required"] is JsonArray list?list.Select(Text).ToHashSet(StringComparer.Ordinal):[];
    internal static int Size(JsonObject obj,string key,int fallback){if(obj[key] is null)return fallback;var n=Number(obj[key]);if(n is null||n.Value.Denominator!=1||n.Value.Numerator<0||n.Value.Numerator>int.MaxValue)throw new ApiException(422,"schema_proof_size_budget","数量约束超过有限证明预算。");return(int)n.Value.Numerator;}
}

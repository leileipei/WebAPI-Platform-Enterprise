using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Comparisons;
public static class ContractNormalizer
{
    internal static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web){MaxDepth=128};
    public static string Fingerprint(ComparisonInput input,string engineVersion)=>engineVersion=="compatibility-v2"
        ?Hash(CanonicalBytes(new{engineVersion,adapterVersion=input.AdapterVersion??"oas-http-model-v2",formatMode=input.FormatMode??"Annotation",from=V2Version(input.From),to=V2Version(input.To)}))
        :Hash(CanonicalBytes(new {engineVersion,from=Version(input.From),to=Version(input.To)}));
    private static object V2Version(ContractVersionInput input)=>new{contract=Version(input),input.Version.Dialect,sources=input.Sources is null?null:new{
        input.Sources.RootUri,input.Sources.BundleHash,
        documents=input.Sources.Documents.OrderBy(x=>x.LogicalUri.AbsoluteUri,StringComparer.Ordinal).Select(x=>new{x.LogicalUri,x.Format,rawHash=Hash(Encoding.UTF8.GetBytes(x.RawText))}),
        definitions=input.Sources.Metadata.Definitions.OrderBy(x=>x.Kind,StringComparer.Ordinal).ThenBy(x=>x.Id),input.Sources.Metadata.VersionDocumentHash
    }};
    public static byte[] CanonicalBytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(Normalize(JsonSerializer.SerializeToNode(value,JsonOptions)),JsonOptions);
    public static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string ParameterKey(string location,string name)=>location+":"+(location=="header"?name.ToLowerInvariant():name);
    internal static string SchemaKey(string type,string name,int? status,string content)=>type+":"+name+":"+status+":"+content;
    internal static string Pointer(string value)=>value.Replace("~","~0",StringComparison.Ordinal).Replace("/","~1",StringComparison.Ordinal);
    internal static JsonNode? Normalize(JsonNode? node,string? property=null)
    {
        if(node is JsonObject o){var result=new JsonObject();foreach(var p in o.OrderBy(p=>p.Key,StringComparer.Ordinal))result[p.Key]=Normalize(p.Value,p.Key);return result;}
        if(node is JsonArray a){var values=a.Select(x=>Normalize(x)).ToArray();if(property is "required" or "enum")values=values.DistinctBy(Canonical).OrderBy(Canonical,StringComparer.Ordinal).ToArray();return new JsonArray(values);}
        return node?.DeepClone();
    }
    internal static string Canonical(JsonNode? node)=>Normalize(node)?.ToJsonString(JsonOptions)??"null";
    internal static JsonNode? Safe(JsonNode? node)
    {
        if(node is JsonObject o){var result=new JsonObject();foreach(var p in o)if(p.Key is not ("example" or "examples"))result[p.Key]=Safe(p.Value);return result;}
        if(node is JsonArray a)return new JsonArray(a.Select(Safe).ToArray());return node?.DeepClone();
    }
    private static object? Json(string? value)
    {
        if(value is null)return null;
        try{return Normalize(JsonNode.Parse(value,new(),new(){MaxDepth=64}));}
        catch(Exception e) when(e is JsonException or ArgumentException){return new {unparsedHash=Hash(Encoding.UTF8.GetBytes(value))};}
    }
    private static object Version(ContractVersionInput input)=>new {
        input.Version.Id,input.Version.ApiId,input.Version.Version,input.Version.Revision,input.Version.ChangeType,
        openapiDocument=Json(input.Version.OpenapiDocument),openapiSource=Json(input.Version.OpenapiSource),input.Version.SourceFormat,input.Version.SchemaHash,
        parameters=input.Parameters.OrderBy(p=>ParameterKey(p.Location,p.Name),StringComparer.Ordinal).ThenBy(p=>p.Id).Select(p=>new {p.Id,p.ApiVersionId,p.Location,p.Name,p.DataType,p.Required,schema=Json(p.Schema),p.Description,example=Json(p.ExampleJson)}),
        schemas=input.Schemas.OrderBy(s=>SchemaKey(s.SchemaType,s.Name,s.StatusCode,s.ContentType),StringComparer.Ordinal).ThenBy(s=>s.Id).Select(s=>new {s.Id,s.ApiVersionId,s.SchemaType,s.Name,s.StatusCode,s.ContentType,schema=Json(s.SchemaJson),s.SchemaHash,example=Json(s.ExampleJson)})
    };
}

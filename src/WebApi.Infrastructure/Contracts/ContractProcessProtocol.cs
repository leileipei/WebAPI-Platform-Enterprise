using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Comparisons;
namespace WebApi.Infrastructure.Contracts;
public sealed record ContractProcessRequest(string Operation,JsonElement Payload);
public sealed record ContractProcessResponse(string Status,JsonElement? Result,IReadOnlyList<ContractIssue> Issues);
internal sealed record ProcessDocument(Uri Uri,string Utf8Base64,string Format);
internal sealed record ProcessSchemaPayload(Uri RootUri,IReadOnlyList<ProcessDocument> Documents,string BundleHash,string SchemaUtf8Base64,string? ExampleUtf8Base64,string Direction,string FormatMode,bool HasExample,Uri? ResourceUri,string Pointer,ContractLimits Limits,bool StructureOnly=false);
internal sealed record ProcessComparisonPayload(string InputUtf8Base64,ComparisonLimits Limits);
public static class ContractProcessProtocol
{
    public const int MaxInputBytes=16*1024*1024;
    public const int MaxOutputBytes=8*1024*1024;
    public static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web){MaxDepth=80};
    public static ContractProcessRequest ComparisonRequest(ComparisonInput input,ComparisonLimits? limits=null)=>new("compare",JsonSerializer.SerializeToElement(new ProcessComparisonPayload(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(input,ContractNormalizer.JsonOptions)),limits??new()),JsonOptions));
    public static ComparisonInput DecodeComparison(JsonElement json,out ComparisonLimits limits)
    {
        var payload=json.Deserialize<ProcessComparisonPayload>(JsonOptions)??throw Invalid();limits=payload.Limits;var defaults=new ComparisonLimits(MaxSideBytes:4*1024*1024,MaxPairBytes:8*1024*1024);
        foreach(var property in typeof(ComparisonLimits).GetProperties()){var value=(int)property.GetValue(limits)!;if(value<1||value>(int)property.GetValue(defaults)!)throw Invalid();}
        return JsonSerializer.Deserialize<ComparisonInput>(Decode(payload.InputUtf8Base64,MaxInputBytes),ContractNormalizer.JsonOptions)??throw Invalid();
    }
    public static ContractProcessRequest SchemaRequest(SchemaValidationInput input,ContractLimits? limits=null)
    {
        limits??=new();ContractBundleCodec.Verify(input.Bundle,limits);
        var documents=input.Bundle.Documents.Select(x=>new ProcessDocument(x.Source.LogicalUri,Encode(x.Source.RawText),x.Source.Format)).ToArray();
        var payload=new ProcessSchemaPayload(input.Bundle.RootUri,documents,input.Bundle.Hash,Encode(input.Schema.ToJsonString()),input.HasExample?Encode(input.Example?.ToJsonString()??"null"):null,input.Direction,input.FormatMode,input.HasExample,input.ResourceUri,input.Pointer,limits,input.StructureOnly);
        return new("schema",JsonSerializer.SerializeToElement(payload,JsonOptions));
    }
    public static SchemaValidationInput DecodeSchema(JsonElement json,out ContractLimits limits,CancellationToken ct)
    {
        var payload=json.Deserialize<ProcessSchemaPayload>(JsonOptions)??throw Invalid();
        limits=payload.Limits;CheckLimits(limits);
        if(payload.Documents.Count>limits.MaxResources)throw Invalid();
        var reader=new ContractDocumentReader();var documents=new List<ContractDocument>();
        var root=payload.Documents.SingleOrDefault(x=>x.Uri==payload.RootUri)??throw Invalid();
        var rootDocument=reader.Read(new(root.Uri,Decode(root.Utf8Base64,limits.MaxDocumentBytes),root.Format),limits,ct);documents.Add(rootDocument);
        foreach(var document in payload.Documents.Where(x=>x!=root))documents.Add(reader.ReadResource(new(document.Uri,Decode(document.Utf8Base64,limits.MaxDocumentBytes),document.Format),limits,rootDocument.Dialect,ct));
        var bundle=ContractBundleCodec.Create(payload.RootUri,documents,limits);if(bundle.Hash!=payload.BundleHash)throw Invalid();
        var schema=JsonNode.Parse(Decode(payload.SchemaUtf8Base64,limits.MaxBundleBytes),documentOptions:new(){MaxDepth=limits.MaxDepth})??throw Invalid();
        var example=payload.HasExample?JsonNode.Parse(Decode(payload.ExampleUtf8Base64??throw Invalid(),limits.MaxExampleBytes),documentOptions:new(){MaxDepth=limits.MaxDepth}):null;
        return new(bundle,schema,example,payload.Direction,payload.FormatMode,payload.HasExample,payload.ResourceUri,payload.Pointer,payload.StructureOnly);
    }
    private static string Encode(string text)=>Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static string Decode(string text,int maximum)
    {
        if(text.Length>4L*((maximum+2)/3))throw Invalid();var bytes=Convert.FromBase64String(text);if(bytes.Length>maximum)throw Invalid();return new UTF8Encoding(false,true).GetString(bytes);
    }
    private static void CheckLimits(ContractLimits limits)
    {
        var defaults=new ContractLimits();
        foreach(var property in typeof(ContractLimits).GetProperties()){var value=(int)property.GetValue(limits)!;if(value<1||value>(int)property.GetValue(defaults)!)throw Invalid();}
    }
    private static ApiException Invalid()=>new(422,"contract_process_input","契约执行输入不合法或超过预算。");
    public static ContractProcessResponse Incomplete(string code)=>new("Incomplete",null,[new(code,"","契约执行未完成。")]);
}

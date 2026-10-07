using System.Text.Json.Nodes;
using WebApi.Contracts.OpenApi;
namespace WebApi.Contracts.Catalog;
public sealed record SchemaValidationInput(ContractBundle Bundle,JsonNode Schema,JsonNode? Example,string Direction,string FormatMode,bool HasExample,Uri? ResourceUri=null,string Pointer="",bool StructureOnly=false);
public sealed record SchemaValidationIssue(string InstancePointer,string SchemaPointer,string Keyword,string Code,string Message,int? Line=null,int? Column=null);
public sealed record SchemaValidationResult(string Status,string Dialect,string SchemaHash,string ExampleHash,string FormatMode,IReadOnlyList<SchemaValidationIssue> Issues,IReadOnlyList<ContractIssue> CoverageIssues);

public sealed record ValidateSchemaRequest(long ExpectedVersionRevision,Guid? SchemaId=null,Guid? ParameterId=null,bool Draft=false,string? DraftSchemaJson=null,string ExampleJson="null",string Direction="request",string FormatMode="Annotation")
{
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string,System.Text.Json.JsonElement>? ExtraFields {get;init;}
}
public sealed record SchemaValidationView(long EvaluatedVersionRevision,SchemaValidationResult Result);

using System.Text.Json.Nodes;
using WebApi.Contracts.OpenApi;
using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class ContractCompletionReviewRegressionTests
{
    [Theory][InlineData("request","readOnly",false)][InlineData("response","writeOnly",false)][InlineData("request","readOnly",true)][InlineData("response","writeOnly",true)]
    public void ConflictingReferenceDirectionAnnotationsCannotProveFalseCompatibility(string direction,string annotation,bool structuralSibling)
    {
        var withReference=OpenApiCompatibilityV2Tests.Root("{\"type\":\"object\",\"required\":[\"p\"],\"properties\":{\"p\":{\"$ref\":\"#/components/schemas/Value\",\""+annotation+"\":false}}}",direction);
        withReference["components"]=new JsonObject{["schemas"]=new JsonObject{["Value"]=new JsonObject{["type"]="string",[annotation]=true}}};
        var required=OpenApiCompatibilityV2Tests.Root("{\"type\":\"object\",\"required\":[\"p\"],\"properties\":{\"p\":{\"type\":\"string\"}}}",direction);
        if(structuralSibling){
            JsonNode Schema(JsonObject root) {
                var operation=root["paths"]!["/items"]!["post"]!;
                return (direction=="request"?operation["requestBody"]:operation["responses"]!["200"])!["content"]!["application/json"]!["schema"]!;
            }
            Schema(withReference)["properties"]!["p"]!["minLength"]=1;Schema(required)["properties"]!["p"]!["minLength"]=1;
        }
        var report=direction=="request"?OpenApiCompatibilityV2Tests.Compare(withReference.ToJsonString(),required.ToJsonString()):OpenApiCompatibilityV2Tests.Compare(required.ToJsonString(),withReference.ToJsonString());
        Assert.NotEqual("Invalid",report.Coverage);Assert.True(report.Counts.Breaking+report.Counts.Unknown>0);
    }
    [Theory]
    [InlineData("{\"type\":[\"string\",\"null\"]}","null")]
    [InlineData("{\"type\":\"null\"}","null")]
    [InlineData("{\"type\":\"number\",\"exclusiveMinimum\":5}","6")]
    [InlineData("{\"type\":\"string\",\"nullable\":\"true\"}","\"x\"")]
    [InlineData("{\"type\":\"array\"}","[]")]
    public void Oas30OriginalKeywordShapesAreNotSilentlyUpgraded(string schema,string example)
    {
        var result=new SchemaEvaluator().Evaluate(SchemaEvaluationTests.Input(schema,example,version:"3.0.3"),new(),default);
        Assert.Equal("Invalid",result.Status);
        var root=OpenApiCompatibilityV2Tests.Root(schema,version:"3.0.3");Assert.Equal("Invalid",OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString()).Coverage);
    }
    [Fact] public void ReferencedExplicitOpenApiDocumentUsesItsOwnDialect()
    {
        var reader=new ContractDocumentReader();var uri=new Uri("https://contracts.invalid/root.json");var other=new Uri("https://contracts.invalid/legacy.json");
        const string schema="{\"$ref\":\"legacy.json#/components/schemas/Value\"}";
        var root=OpenApiCompatibilityV2Tests.Root();root["components"]=JsonNode.Parse("{\"schemas\":{\"Selected\":"+schema+"}}");
        var legacy=OpenApiCompatibilityV2Tests.Root(version:"3.0.3");legacy["components"]=JsonNode.Parse("{\"schemas\":{\"Value\":{\"type\":\"string\",\"nullable\":true}}}");
        var docs=new[]{reader.Read(new(uri,root.ToJsonString(),"json"),new(),default),reader.ReadResource(new(other,legacy.ToJsonString(),"json"),new(),ContractDialect.Oas31,default)};
        var input=new SchemaValidationInput(ContractBundleCodec.Create(uri,docs),JsonNode.Parse(schema)!,null,"request","Annotation",true,uri,"/components/schemas/Selected");
        Assert.Equal("Valid",new SchemaEvaluator().Evaluate(input,new(),default).Status);
    }
}

using System.Text.Json.Nodes;
using Xunit;
using System.Text.Json;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
namespace WebApi.Domain.Tests;
public class SchemaEvaluationTests
{
    internal static SchemaValidationInput Input(string schema,string example,string format="Annotation",string direction="request",string version="3.1.0",bool hasExample=true)
    {
        var uri=new Uri("https://contracts.invalid/openapi");
        var root=new JsonObject{["openapi"]=version,["info"]=new JsonObject{["title"]="Example",["version"]="1"},["paths"]=new JsonObject(),["components"]=new JsonObject{["schemas"]=new JsonObject{["Selected"]=JsonNode.Parse(schema)}}};
        var doc=new ContractDocumentReader().Read(new(uri,root.ToJsonString(),"json"),new(),default);
        return new(ContractBundleCodec.Create(uri,[doc]),JsonNode.Parse(schema)!,JsonNode.Parse(example),direction,format,hasExample,uri,"/components/schemas/Selected");
    }
    [Theory]
    [InlineData("{\"type\":\"integer\"}","0","Valid")]
    [InlineData("{\"type\":\"boolean\"}","false","Valid")]
    [InlineData("{\"type\":\"string\",\"maxLength\":0}","\"\"","Valid")]
    [InlineData("{\"type\":\"null\"}","null","Valid")]
    [InlineData("false","null","Invalid")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}","[1,\"x\"]","Invalid")]
    [InlineData("{\"type\":\"object\",\"required\":[\"name\"]}","{}","Invalid")]
    [InlineData("{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"integer\"}]}","true","Invalid")]
    [InlineData("{\"oneOf\":[{\"type\":\"integer\"},{\"type\":\"number\"}]}","1","Invalid")]
    [InlineData("{\"if\":{\"properties\":{\"kind\":{\"const\":\"a\"}}},\"then\":{\"required\":[\"a\"]},\"else\":{\"required\":[\"b\"]}}","{\"kind\":\"a\"}","Invalid")]
    [InlineData("{\"type\":\"object\",\"dependentRequired\":{\"a\":[\"b\"]}}","{\"a\":1}","Invalid")]
    public void FalseZeroEmptyStringAndNullAreRealExamples(string schema,string example,string expected)
    {
        var result=new SchemaEvaluator().Evaluate(Input(schema,example),new(),default);
        Assert.Equal(expected,result.Status);Assert.Empty(result.CoverageIssues);Assert.NotEmpty(result.ExampleHash);
    }
    [Fact] public void DiagnosticsNeverEchoSampleValues()
    {
        var result=new SchemaEvaluator().Evaluate(Input("{\"type\":\"integer\"}","\"PRIVATE-SAMPLE-123\""),new(),default);
        Assert.Equal("Invalid",result.Status);Assert.Contains(result.Issues,x=>x.Keyword=="type"&&x.InstancePointer=="");
        Assert.DoesNotContain("PRIVATE-SAMPLE",JsonSerializer.Serialize(result));
    }
    [Theory][InlineData("request","Valid")][InlineData("response","Invalid")]
    public void ReadOnlyRequiredRespectsDirection(string direction,string expected)
    {
        var result=new SchemaEvaluator().Evaluate(Input("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\",\"readOnly\":true}},\"required\":[\"id\"]}","{}",direction:direction),new(),default);
        Assert.Equal(expected,result.Status);
    }
    [Theory][InlineData("Annotation","Valid")][InlineData("Strict","Invalid")]
    public void FormatsAreAnnotationsUnlessStrict(string mode,string expected)=>Assert.Equal(expected,new SchemaEvaluator().Evaluate(Input("{\"type\":\"string\",\"format\":\"uuid\"}","\"invalid-uuid\"",mode),new(),default).Status);
    [Fact]public void UnknownStrictFormatIsIncomplete()=>Assert.Equal("Incomplete",new SchemaEvaluator().Evaluate(Input("{\"format\":\"enterprise-custom\"}","\"a\"","Strict"),new(),default).Status);
    [Fact]public void MissingReferenceIsIncomplete()=>Assert.Equal("Incomplete",new SchemaEvaluator().Evaluate(Input("{\"$ref\":\"missing.json\"}","0"),new(),default).Status);
    [Fact]public void UnsupportedDialectIsIncomplete()=>Assert.Equal("Incomplete",new SchemaEvaluator().Evaluate(Input("{\"$schema\":\"https://custom.invalid/meta\"}","0"),new(),default).Status);
    [Fact]public void OversizedExampleIsIncomplete()=>Assert.Equal("Incomplete",new SchemaEvaluator().Evaluate(Input("{}",JsonSerializer.Serialize(new string('x',262145))),new(),default).Status);
    [Fact]public void NoExampleOnlyChecksSchema()=>Assert.Equal("Valid",new SchemaEvaluator().Evaluate(Input("{\"type\":\"integer\"}","null",hasExample:false),new(),default).Status);
    [Fact]public void InvalidSchemaShapeIsInvalid()=>Assert.Equal("Invalid",new SchemaEvaluator().Evaluate(Input("{\"type\":123}","0"),new(),default).Status);
    [Fact]public void Oas30BooleanAdditionalPropertiesIsAllowed()
    {
        var prepared=DialectAdapter.PrepareSchema(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false}")!,ContractDialect.Oas30,"request");
        Assert.Empty(prepared.Issues);
        Assert.Equal("Valid",new SchemaEvaluator().Evaluate(Input("{\"type\":\"object\",\"additionalProperties\":false}","{}",version:"3.0.3"),new(),default).Status);
    }
    [Theory][InlineData("{\"next\":{\"value\":1}}","Valid")][InlineData("{\"next\":{\"value\":\"bad\"}}","Invalid")]
    public void RecursiveDynamicReferenceUsesLocalResource(string example,string expected)
    {
        var input=Input("{\"$id\":\"https://schema.invalid/node\",\"$dynamicAnchor\":\"node\",\"type\":\"object\",\"properties\":{\"next\":{\"$dynamicRef\":\"#node\"},\"value\":{\"type\":\"integer\"}}}",example);
        Assert.Equal(expected,new SchemaEvaluator().Evaluate(input,new(),default).Status);
    }
    [Fact]public void PointerReferenceUsesFullOpenApiResource()
    {
        var input=Input("{\"$defs\":{\"foo\":{\"type\":\"integer\"}},\"$ref\":\"#/components/schemas/Selected/$defs/foo\"}","\"bad\"");
        Assert.Equal("Invalid",new SchemaEvaluator().Evaluate(input,new(),default).Status);
    }
    [Theory][InlineData("{\"minLength\":1}","Valid")][InlineData("{\"minLength\":-1}","Invalid")]
    public void FixedBuiltinMetaSchemaIsAvailableOffline(string example,string expected)=>Assert.Equal(expected,new SchemaEvaluator().Evaluate(Input("{\"$ref\":\"https://json-schema.org/draft/2020-12/schema\"}",example),new(),default).Status);
    [Theory][InlineData("・𠀀")][InlineData("・𛀀")]
    public void IdnaContextIncludesSupplementaryHanAndKatakana(string hostname)=>Assert.Equal("Valid",new SchemaEvaluator().Evaluate(Input("{\"format\":\"idn-hostname\"}",JsonSerializer.Serialize(hostname),"Strict"),new(),default).Status);
    [Fact]public void ExampleNodeBudgetIsEnforcedBeforeEvaluation()
    {
        var example="["+string.Join(',',Enumerable.Repeat("null",50001))+"]";
        Assert.Equal("Incomplete",new SchemaEvaluator().Evaluate(Input("{}",example),new(),default).Status);
    }
    [Theory][InlineData("readOnly","request","Valid")][InlineData("readOnly","response","Invalid")][InlineData("writeOnly","response","Valid")][InlineData("writeOnly","request","Invalid")]
    public void RequiredDirectionAnnotationsFollowReferences(string annotation,string direction,string expected)
    {
        var schema=new JsonObject{["$id"]="https://schema.invalid/direction",["type"]="object",["$defs"]=new JsonObject{["Id"]=new JsonObject{["type"]="integer",[annotation]=true}},["properties"]=new JsonObject{["id"]=new JsonObject{["$ref"]="#/$defs/Id"}},["required"]=new JsonArray("id")};
        Assert.Equal(expected,new SchemaEvaluator().Evaluate(Input(schema.ToJsonString(),"{}",direction:direction),new(),default).Status);
    }
    [Fact]public void SameIdInConcurrentEvaluationsNeverSharesDefinitions()
    {
        var integer=Input("{\"$id\":\"https://schema.invalid/same\",\"type\":\"integer\"}","1");
        var text=Input("{\"$id\":\"https://schema.invalid/same\",\"type\":\"string\"}","1");
        Parallel.For(0,20,i=>Assert.Equal(i%2==0?"Valid":"Invalid",new SchemaEvaluator().Evaluate(i%2==0?integer:text,new(),default).Status));
    }
    [Fact]public void EmbeddedIdDiagnosticsKeepAvailableSourcePosition()
    {
        var result=new SchemaEvaluator().Evaluate(Input("{\"$id\":\"https://schema.invalid/position\",\"type\":\"integer\"}","\"wrong\""),new(),default);
        Assert.Equal("Invalid",result.Status);Assert.Contains(result.Issues,x=>x.Keyword=="type"&&x.Line==1&&x.Column>0);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Comparisons.Rules;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class CompatibilityProofTests
{
    internal static string FixturePath=>Path.Combine(AppContext.BaseDirectory,"fixtures/compatibility-v2/schema-cases.json");
    public static IEnumerable<object[]> Cases(){using var json=JsonDocument.Parse(File.ReadAllText(FixturePath));foreach(var value in json.RootElement.EnumerateArray())foreach(var direction in new[]{"request","response"})yield return [value.GetProperty("id").GetString()!,value.GetProperty("baseline").GetRawText(),value.GetProperty("target").GetRawText(),direction,value.GetProperty(direction).GetString()!,value.GetProperty("version").GetString()!];}
    internal static (ContractBundle Bundle,JsonNode Schema) Pack(string schema,string version="3.1.0",string? external=null)
    {
        var uri=new Uri("https://proof.invalid/openapi.json");var reader=new ContractDocumentReader();var root=new JsonObject{["openapi"]=version,["info"]=new JsonObject{["title"]="Proof",["version"]="1"},["paths"]=new JsonObject(),["components"]=new JsonObject{["schemas"]=new JsonObject{["Value"]=JsonNode.Parse(schema)}}};
        var document=reader.Read(new(uri,root.ToJsonString(),"json"),new(),default);var docs=new List<ContractDocument>{document};if(external is not null)docs.Add(reader.ReadResource(new(new Uri(uri,"other.json"),external,"json"),new(),document.Dialect,default));var bundle=ContractBundleCodec.Create(uri,docs);return(bundle,bundle.Documents[0].Root["components"]!["schemas"]!["Value"]!);
    }
    internal static CompatibilityProofResult Compare(string a,string b,string direction="request",string version="3.1.0",ContractLimits? limits=null,CancellationToken ct=default)
    {var from=Pack(a,version);var to=Pack(b,version);return new CompatibilityProof().Compare(from.Schema,to.Schema,from.Bundle,to.Bundle,direction,limits??new(),ct);}
    [Theory][MemberData(nameof(Cases))]
    public void DirectionalFamilies(string id,string baseline,string target,string direction,string expected,string version)
    {
        var result=Compare(baseline,target,direction,version);Assert.True(result.Risk==expected,$"{id}/{direction}: expected {expected}, got {result.Risk}; {JsonSerializer.Serialize(result)}");
        if(expected=="Compatible"){Assert.NotEmpty(result.AppliedRuleIds);Assert.Empty(result.CoverageIssues);}if(expected=="Unknown")Assert.NotEmpty(result.CoverageIssues);
        if(expected=="Breaking"){Assert.Contains(result.Findings,x=>x.Risk=="Breaking");Assert.All(result.Findings.Where(x=>x.Risk=="Breaking"),x=>Assert.Matches("^[0-9a-f]{64}$",x.WitnessHash!));}
    }
    [Fact] public void OptionalFieldAgainstClosedObjectIsNotCompatible()
    {var result=Compare("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\"}}}","{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"integer\"},\"b\":{\"type\":\"string\"}},\"additionalProperties\":false}");Assert.Equal("Breaking",result.Risk);}
    [Fact] public void MultipleOfUsesExactArithmetic()
    {Assert.Equal("Compatible",Compare("{\"type\":\"number\",\"multipleOf\":0.00000000000000000000000000003}","{\"type\":\"number\",\"multipleOf\":0.00000000000000000000000000001}").Risk);Assert.Equal("Breaking",Compare("{\"enum\":[9007199254740992]}","{\"enum\":[9007199254740993]}").Risk);}
    [Fact] public void OneOfOverlapIsNotAnyOf()
    {var result=Compare("{\"anyOf\":[{\"type\":\"integer\"},{\"type\":\"number\"}]}","{\"oneOf\":[{\"type\":\"integer\"},{\"type\":\"number\"}]}");Assert.Equal("Breaking",result.Risk);}
    [Fact] public void UnevaluatedPropertiesKeepsAnnotationContext()
    {var result=Compare("{\"type\":\"object\",\"allOf\":[{\"properties\":{\"a\":{\"type\":\"integer\"}}}],\"unevaluatedProperties\":false}","{\"type\":\"object\",\"allOf\":[{\"properties\":{\"a\":{\"type\":\"integer\"}}},{\"properties\":{\"b\":{\"type\":\"string\"}}}],\"unevaluatedProperties\":false}");Assert.Equal("Unknown",result.Risk);Assert.NotEmpty(result.CoverageIssues);}
    [Fact] public void RecursiveRefTerminatesAndUnknownIsVisible()
    {var result=Compare("{\"type\":\"object\",\"properties\":{\"child\":{\"$ref\":\"#/components/schemas/Value\"}}}","{\"type\":\"object\",\"properties\":{\"child\":{\"$ref\":\"#/components/schemas/Value\"},\"id\":{\"type\":\"integer\"}}}");Assert.Equal("Unknown",result.Risk);Assert.Contains(result.CoverageIssues,x=>x.Code.Contains("recursive",StringComparison.Ordinal));}
    [Fact] public void NoWitnessDoesNotProveInclusion()
    {var result=Compare("{\"type\":\"string\",\"pattern\":\"^Z{65}$\"}","{\"type\":\"string\",\"pattern\":\"^Z{64}$\"}");Assert.Equal("Unknown",result.Risk);Assert.NotEmpty(result.CoverageIssues);}
    [Fact] public void FiniteExternalReferencesRespectEachFixedBundle()
    {var from=Pack("{\"$ref\":\"other.json\"}",external:"{\"type\":\"integer\"}");var to=Pack("{\"$ref\":\"other.json\"}",external:"{\"type\":\"number\"}");Assert.Equal("Compatible",new CompatibilityProof().Compare(from.Schema,to.Schema,from.Bundle,to.Bundle,"request",new(),default).Risk);Assert.Equal("Breaking",new CompatibilityProof().Compare(from.Schema,to.Schema,from.Bundle,to.Bundle,"response",new(),default).Risk);}
    [Fact] public void MissingReferenceIsNeverCompatibleEvenWhenTextIsEqual()
    {var result=Compare("{\"$ref\":\"missing.json\"}","{\"$ref\":\"missing.json\"}");Assert.Equal("Unknown",result.Risk);Assert.NotEmpty(result.CoverageIssues);}
    [Fact] public void DynamicScopeChangeIsExplicitlyUnknown()
    {var result=Compare("{\"$dynamicAnchor\":\"node\",\"type\":\"object\",\"properties\":{\"child\":{\"$dynamicRef\":\"#node\"}}}","{\"$dynamicAnchor\":\"node\",\"type\":\"object\",\"properties\":{\"child\":{\"$dynamicRef\":\"#node\"}},\"required\":[\"id\"]}");Assert.Equal("Unknown",result.Risk);Assert.NotEmpty(result.CoverageIssues);}
    [Fact] public void UnsupportedDialectAndKeywordsHaveVisibleCoverage()
    {Assert.Equal("Unknown",Compare("{\"const\":1}","{\"const\":1}",version:"3.0.3").Risk);var result=Compare("{\"type\":\"integer\",\"x-custom\":1}","{\"type\":\"integer\",\"x-custom\":1}");Assert.Equal("Unknown",result.Risk);Assert.NotEmpty(result.CoverageIssues);}
    [Fact] public void FullParentConstraintsPreventFalseBreakingWitness()
    {var result=Compare("{\"type\":\"integer\",\"minimum\":0,\"maximum\":1,\"enum\":[1]}","{\"type\":\"integer\",\"minimum\":1,\"maximum\":1}");Assert.Equal("Compatible",result.Risk);Assert.DoesNotContain(result.Findings,x=>x.Risk=="Breaking");}
    [Fact] public void WitnessesNeverExposeSampleValues()
    {var result=Compare("{\"enum\":[\"PRIVATE-PROOF-SAMPLE\"]}","{\"const\":\"different\"}");Assert.Equal("Breaking",result.Risk);var bytes=JsonSerializer.Serialize(result);Assert.DoesNotContain("PRIVATE-PROOF-SAMPLE",bytes);Assert.DoesNotContain("different",bytes);}
    [Fact] public void CancellationAndBudgetsAreNotCompatible()
    {using var cancelled=new CancellationTokenSource();cancelled.Cancel();Assert.Equal("Unknown",Compare("true","true",ct:cancelled.Token).Risk);var result=Compare("{\"type\":\"integer\"}","{\"type\":\"number\"}",limits:new(MaxNodes:1));Assert.Equal("Unknown",result.Risk);Assert.NotEmpty(result.CoverageIssues);}
    [Fact] public void RepeatedReferencesDoNotMultiplyTheComparisonMemoryBudget()
    {
        var properties=new JsonObject();for(var i=0;i<200;i++)properties["p"+i]=new JsonObject{["$ref"]="#/components/schemas/Value/$defs/Big"};
        var before=new JsonObject{["type"]="object",["properties"]=properties,["$defs"]=new JsonObject{["Big"]=new JsonObject{["type"]="string",["description"]=new string('a',65536)}}};
        var after=before.DeepClone();after["title"]="annotation change";
        var result=Compare(before.ToJsonString(),after.ToJsonString());Assert.Equal("Unknown",result.Risk);Assert.Contains(result.CoverageIssues,x=>x.Code=="schema_proof_budget");
    }
    [Fact] public void UnicodePatternsAndExtremeNumericBudgetsAreExplicit()
    {
        Assert.Equal("Compatible",Compare("{\"type\":\"string\",\"pattern\":\"^汉😀+$\",\"minLength\":3}","{\"type\":\"string\",\"pattern\":\"^汉😀+$\",\"minLength\":2}").Risk);
        Assert.Equal("Breaking",Compare("{\"enum\":[\"汉😀\"]}","{\"const\":\"汉\"}").Risk);
        var huge=Compare("{\"type\":\"number\",\"minimum\":1e10000}","{\"type\":\"number\"}");Assert.Equal("Unknown",huge.Risk);Assert.NotEmpty(huge.CoverageIssues);
        Assert.Equal("Unknown",Compare("{\"type\":\"string\",\"minLength\":2147483648}","{\"type\":\"string\",\"minLength\":2147483649}").Risk);
    }

    [Fact] public void UnicodePatternPropertiesUseTheValidatorMatchingSemantics()
    {
        var before=new JsonObject{["type"]="object",["properties"]=new JsonObject{["é"]=true},["patternProperties"]=new JsonObject{[@"^\W+$|^\p{Z}$"]=new JsonObject{["type"]="string"}}};
        var after=before.DeepClone();after["properties"]!["é"]=new JsonObject{["type"]="string"};
        Assert.Equal("Breaking",Compare(before.ToJsonString(),after.ToJsonString()).Risk);
    }
    [Fact] public void NestedFiniteSchemasKeepExactNumbersAndAllConstraints()
    {
        var a="{\"type\":\"object\",\"required\":[\"id\"],\"properties\":{\"id\":{\"enum\":[9007199254740992]}}}";
        var b="{\"type\":\"object\",\"required\":[\"id\"],\"properties\":{\"id\":{\"enum\":[9007199254740993]}}}";
        Assert.Equal("Breaking",Compare(a,b).Risk);
        Assert.Equal("Compatible",Compare("{\"type\":\"object\",\"properties\":{\"id\":{\"enum\":[0,1],\"minimum\":1}}}","{\"type\":\"object\",\"properties\":{\"id\":{\"const\":1}}}").Risk);
    }

    [Fact] public void CounterexamplesPointToTheFailedBoundsRule()
    {
        var result=Compare("{\"type\":\"number\",\"minimum\":5}","{\"type\":\"number\",\"minimum\":10}");
        var finding=Assert.Single(result.Findings);Assert.Equal("schema.number_bounds",finding.RuleId);Assert.Contains("minimum",finding.Pointer);Assert.NotEmpty(finding.WitnessHash!);
    }

    [Fact] public void BundleOrderingDoesNotSelectTheContractDialect()
    {
        var external="{\"openapi\":\"3.0.3\",\"info\":{\"title\":\"External\",\"version\":\"1\"},\"paths\":{},\"components\":{\"schemas\":{\"Shared\":{\"type\":\"integer\"}}}}";
        var from=Pack("{\"$ref\":\"other.json#/components/schemas/Shared\",\"maximum\":5}",external:external);var to=Pack("{\"$ref\":\"other.json#/components/schemas/Shared\",\"minimum\":10}",external:external);
        var a=ContractBundleCodec.Create(from.Bundle.RootUri,[from.Bundle.Documents[1],from.Bundle.Documents[0]]);var b=ContractBundleCodec.Create(to.Bundle.RootUri,[to.Bundle.Documents[1],to.Bundle.Documents[0]]);
        Assert.Equal("Breaking",new CompatibilityProof().Compare(from.Schema,to.Schema,a,b,"request",new(),default).Risk);
    }

}

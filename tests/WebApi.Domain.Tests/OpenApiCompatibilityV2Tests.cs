using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Infrastructure.Comparisons;
using WebApi.Contracts.Comparisons;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class OpenApiCompatibilityV2Tests
{
    internal static JsonObject Root(string schema="{\"type\":\"integer\"}",string direction="request",string version="3.1.0")
    {
        var response=new JsonObject{["description"]="ok"};var op=new JsonObject{["responses"]=new JsonObject{["200"]=response}};
        if(direction=="request")op["requestBody"]=new JsonObject{["content"]=new JsonObject{["application/json"]=new JsonObject{["schema"]=JsonNode.Parse(schema)}}};
        else response["content"]=new JsonObject{["application/json"]=new JsonObject{["schema"]=JsonNode.Parse(schema)}};
        return new(){["openapi"]=version,["info"]=new JsonObject{["title"]="V2",["version"]="1"},["paths"]=new JsonObject{["/items"]=new JsonObject{["post"]=op}}};
    }
    private static JsonObject Op(JsonObject root)=>(JsonObject)root["paths"]!["/items"]!["post"]!;
    private static JsonObject Param(string name="ids",string location="query",string schema="{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}")=>new(){["name"]=name,["in"]=location,["schema"]=JsonNode.Parse(schema)};
    private static JsonObject Response(string type)=>new(){["description"]="ok",["content"]=new JsonObject{["application/json"]=new JsonObject{["schema"]=new JsonObject{["type"]=type}}}};
    private static void Security(JsonObject root)
    {root["components"]=new JsonObject{["securitySchemes"]=new JsonObject{["key"]=new JsonObject{["type"]="apiKey",["name"]="X-Key",["in"]="header"},["other"]=new JsonObject{["type"]="apiKey",["name"]="X-Other",["in"]="header"},["oauth"]=new JsonObject{["type"]="oauth2",["flows"]=new JsonObject{["clientCredentials"]=new JsonObject{["tokenUrl"]="https://auth.invalid/token",["scopes"]=new JsonObject{["read"]="Read",["write"]="Write"}}}}}};}
    public static IEnumerable<object[]> Cases()
    {
        object[] Case(string id,JsonObject a,JsonObject b,string risk)=>[id,a.ToJsonString(),b.ToJsonString(),risk];
        var a=Root();var b=(JsonObject)a.DeepClone();b["paths"]=new JsonObject();yield return Case("operation.remove",a,b,"Breaking");yield return Case("operation.add",b,a,"Compatible");
        a=Root();b=(JsonObject)a.DeepClone();a["paths"]!["/items"]!["parameters"]=new JsonArray(Param());Op(b)["parameters"]=new JsonArray(Param());yield return Case("parameter.path_inheritance",a,b,"Compatible");
        a=Root();Op(a)["parameters"]=new JsonArray(Param());b=(JsonObject)a.DeepClone();Op(b)["parameters"]![0]!["style"]="form";Op(b)["parameters"]![0]!["explode"]=true;Op(b)["parameters"]![0]!["allowReserved"]=false;yield return Case("parameter.defaults",a,b,"Compatible");
        Op(b)["parameters"]![0]!["explode"]=false;yield return Case("parameter.serialization",a,b,"Unknown");
        a=Root();Op(a)["parameters"]=new JsonArray(Param("X-Trace","header","{\"type\":\"string\"}"));b=(JsonObject)a.DeepClone();Op(b)["parameters"]![0]!["name"]="x-trace";yield return Case("parameter.header_case",a,b,"Compatible");
        a=Root();Op(a)["parameters"]=new JsonArray(Param());b=(JsonObject)a.DeepClone();Op(b)["parameters"]![0]!["required"]=true;yield return Case("parameter.required",a,b,"Breaking");
        a=Root();var contentParam=new JsonObject{["name"]="filter",["in"]="query",["content"]=new JsonObject{["application/json"]=new JsonObject{["schema"]=new JsonObject{["type"]="integer"}}}};Op(a)["parameters"]=new JsonArray(contentParam);b=(JsonObject)a.DeepClone();Op(b)["parameters"]![0]!["content"]!["application/json"]!["schema"]!["type"]="number";yield return Case("parameter.content",a,b,"Compatible");
        a=Root();b=(JsonObject)a.DeepClone();Op(b)["requestBody"]!["required"]=true;yield return Case("request.required",a,b,"Breaking");
        a=Root();Op(a)["requestBody"]!["content"]!["application/xml"]=new JsonObject{["schema"]=new JsonObject{["type"]="integer"}};b=(JsonObject)a.DeepClone();Op(b)["requestBody"]!["content"]!.AsObject().Remove("application/xml");yield return Case("request.media_remove",a,b,"Breaking");yield return Case("request.media_add",b,a,"Compatible");
        a=Root("{\"type\":\"object\"}");b=(JsonObject)a.DeepClone();Op(b)["requestBody"]!["content"]!["application/json"]!["encoding"]=new JsonObject{["value"]=new JsonObject{["style"]="form"}};yield return Case("request.encoding",a,b,"Unknown");
        a=Root(direction:"response");Op(a)["responses"]=new JsonObject{["2XX"]=Response("number")};b=(JsonObject)a.DeepClone();Op(b)["responses"]=new JsonObject{["200"]=Response("integer")};yield return Case("response.range_narrow",a,b,"Compatible");yield return Case("response.new_status",b,a,"Breaking");
        Op(a)["responses"]!["200"]=Response("string");yield return Case("response.explicit_priority",a,b,"Breaking");
        a=Root(direction:"response");Op(a)["responses"]=new JsonObject{["default"]=Response("number"),["400"]=Response("string")};b=(JsonObject)a.DeepClone();Op(b)["responses"]!["default"]=Response("integer");yield return Case("response.default_priority",a,b,"Compatible");
        a=Root(direction:"response");Op(a)["responses"]!["200"]!["content"]!["application/xml"]=new JsonObject{["schema"]=new JsonObject{["type"]="integer"}};b=(JsonObject)a.DeepClone();Op(b)["responses"]!["200"]!["content"]!.AsObject().Remove("application/xml");yield return Case("response.media_narrow",a,b,"Compatible");yield return Case("response.new_media",b,a,"Breaking");
        a=Root(direction:"response");Op(a)["responses"]!["200"]!["headers"]=new JsonObject{["X-Count"]=new JsonObject{["schema"]=new JsonObject{["type"]="number"},["required"]=true}};b=(JsonObject)a.DeepClone();Op(b)["responses"]!["200"]!["headers"]!["X-Count"]!["schema"]!["type"]="integer";yield return Case("response.header_schema",a,b,"Compatible");
        Op(b)["responses"]!["200"]!["headers"]!.AsObject().Remove("X-Count");yield return Case("response.required_header_remove",a,b,"Breaking");
        a=Root();Security(a);a["security"]=JsonNode.Parse("[{\"key\":[]}]");b=(JsonObject)a.DeepClone();b.AsObject().Remove("security");Op(b)["security"]=JsonNode.Parse("[{\"key\":[]}]");yield return Case("security.inheritance",a,b,"Compatible");
        b=(JsonObject)a.DeepClone();Op(b)["security"]=new JsonArray();yield return Case("security.anonymous",a,b,"Compatible");yield return Case("security.tighten",b,a,"Breaking");
        a["security"]=JsonNode.Parse("[{\"key\":[]},{\"other\":[]}]");b=(JsonObject)a.DeepClone();b["security"]=JsonNode.Parse("[{\"key\":[],\"other\":[]}]");yield return Case("security.or_and",a,b,"Breaking");yield return Case("security.and_or",b,a,"Compatible");
        a["security"]=JsonNode.Parse("[{\"oauth\":[\"read\"]}]");b=(JsonObject)a.DeepClone();b["security"]=JsonNode.Parse("[{\"oauth\":[\"read\",\"write\"]}]");yield return Case("security.scope_tighten",a,b,"Breaking");yield return Case("security.scope_relax",b,a,"Compatible");
        a=Root();Security(a);a["security"]=JsonNode.Parse("[{\"key\":[]}]");b=(JsonObject)a.DeepClone();b["components"]!["securitySchemes"]!["key"]!["name"]="Other-Key";yield return Case("security.implementation_change",a,b,"Unknown");
        a=Root("{\"type\":\"string\",\"nullable\":true}",version:"3.0.3");b=Root("{\"type\":[\"string\",\"null\"]}");yield return Case("schema.cross_dialect_nullable",a,b,"Compatible");
        a=Root();Op(a)["callbacks"]=new JsonObject();b=(JsonObject)a.DeepClone();yield return Case("callback.visible_coverage",a,b,"Unknown");
        a=Root();Op(a)["x-runtime-behavior"]=true;b=(JsonObject)a.DeepClone();yield return Case("extension.visible_coverage",a,b,"Unknown");
        a=Root();a["paths"]!["/items/{id}"]=a["paths"]!["/items"]!.DeepClone();a["paths"]!["/items/{name}"]=a["paths"]!["/items"]!.DeepClone();b=(JsonObject)a.DeepClone();yield return Case("path.template_ambiguity",a,b,"Unknown");
    }
    internal static ComparisonReport Compare(string a,string b,ComparisonLimits? limits=null)=>new ContractComparisonEngine().Compare(new(ComparisonTestData.Version(a),ComparisonTestData.Version(b)),limits??new(),default);
    [Theory][MemberData(nameof(Cases))]
    public void OpenApiFamiliesUseV2(string id,string a,string b,string risk)
    {
        var report=Compare(a,b);Assert.Equal("compatibility-v2",report.EngineVersion);
        if(risk=="Compatible"){Assert.Equal("Complete",report.Coverage);Assert.Equal(0,report.Counts.Breaking);Assert.Equal(0,report.Counts.Unknown);}
        else if(risk=="Breaking"){Assert.Contains(report.Findings,x=>x.Risk=="Breaking"&&x.RuleId is not null);}
        else{Assert.Equal("Limited",report.Coverage);Assert.True(report.Counts.Unknown>0,$"{id}: unknown behavior cannot have zero unknown count");}
    }
    [Fact] public void V2HasWholeOutputAndInputBudgets()
    {
        var a=Root();a["paths"]!["/other"]=a["paths"]!["/items"]!.DeepClone();var b=Root();b["paths"]=new JsonObject();
        Assert.Equal("Invalid",Compare(a.ToJsonString(),b.ToJsonString(),new(MaxFindings:1)).Coverage);
        Assert.Equal("Invalid",Compare(a.ToJsonString(),b.ToJsonString(),new(MaxNodes:1)).Coverage);
    }
    [Fact] public void ProofRoundtripRetainsCoverageAndAppliedRuleIds()
    {
        var result=CompatibilityProofTests.Compare("{\"type\":\"integer\"}","{\"type\":\"number\"}");
        var restored=JsonSerializer.Deserialize<CompatibilityProofResult>(JsonSerializer.Serialize(result))!;Assert.Equal(result.Risk,restored.Risk);Assert.Equal(result.AppliedRuleIds,restored.AppliedRuleIds);Assert.Equal(result.CoverageIssues,restored.CoverageIssues);
    }

    [Fact] public void ImpossibleNestedParentCannotProduceAFakeBreakingWitness()
    {
        var a="{\"type\":\"object\",\"required\":[\"body\"],\"properties\":{\"body\":{\"type\":\"object\",\"required\":[\"value\"],\"properties\":{\"value\":false}}}}";
        var b="{\"type\":\"object\",\"required\":[\"body\",\"extra\"],\"properties\":{\"body\":{\"type\":\"object\",\"required\":[\"value\"],\"properties\":{\"value\":false}},\"extra\":{\"type\":\"string\"}}}";
        var result=CompatibilityProofTests.Compare(a,b);Assert.Equal("Compatible",result.Risk);Assert.Empty(result.Findings);
    }

}

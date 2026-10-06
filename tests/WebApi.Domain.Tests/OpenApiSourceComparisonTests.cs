using System.Text.Json.Nodes;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Comparisons;
using WebApi.Infrastructure.Contracts;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class OpenApiSourceComparisonTests
{
    private static ContractVersionInput Version(string type)
    {
        var root=OpenApiCompatibilityV2Tests.Root("{\"$ref\":\"https://external.invalid/schema.json\"}");var version=ComparisonTestData.Version(root.ToJsonString());var reader=new ContractDocumentReader();var uri=new Uri("https://contracts.invalid/fixed/root.json");
        var documents=new[]{reader.Read(new(uri,root.ToJsonString(),"json"),new(),default),reader.ReadResource(new(new("https://external.invalid/schema.json"),"{\"type\":\""+type+"\"}","json"),new(),ContractDialect.Oas31,default)};var bundle=ContractBundleCodec.Create(uri,documents);
        return version with{Sources=new(uri,documents.Select(x=>x.Source).ToArray(),bundle.Hash,new(documents.Select(x=>new ContractResourceSource(x.Source.LogicalUri,x.Source.Format)).ToArray(),[]))};
    }
    [Fact] public void ComparisonUsesImmutableExternalSourcesRatherThanFetchingOrDroppingThem()
    {
        var report=new ContractComparisonEngine().Compare(new(Version("integer"),Version("number")),new(),default);Assert.Equal("Complete",report.Coverage);Assert.Equal(0,report.Counts.Breaking);Assert.Equal(0,report.Counts.Unknown);
    }
    [Fact] public void TamperedSourceBundleIsInvalidWithoutAnAcceptablePartialReport()
    {
        var a=Version("integer");var b=Version("number");b=b with{Sources=b.Sources! with{BundleHash="tampered"}};var report=new ContractComparisonEngine().Compare(new(a,b),new(),default);Assert.Equal("Invalid",report.Coverage);Assert.Empty(report.Findings);
    }
    [Fact] public void ImportedVersionComparesOnlyItsSelectedOperationWhileKeepingTheWholeSourcePackage()
    {
        ContractVersionInput Scoped(string outsideType){
            var selected=OpenApiCompatibilityV2Tests.Root();var source=(JsonObject)selected.DeepClone();source["paths"]!["/outside"]=OpenApiCompatibilityV2Tests.Root("{\"type\":\""+outsideType+"\"}")["paths"]!["/items"]!.DeepClone();var uri=new Uri("https://contracts.invalid/whole.json");var document=new ContractDocumentReader().Read(new(uri,source.ToJsonString(),"json"),new(),default);var bundle=ContractBundleCodec.Create(uri,[document]);
            var version=ComparisonTestData.Version(selected.ToJsonString());var hash=ContractNormalizer.Hash(System.Text.Encoding.UTF8.GetBytes(new ContractDocumentReader().Read(new(uri,selected.ToJsonString(),"json"),new(),default).CanonicalJson));
            return version with{Sources=new(uri,[document.Source],bundle.Hash,new([new(uri,"json")],[],hash))};
        }
        var report=new ContractComparisonEngine().Compare(new(Scoped("integer"),Scoped("string")),new(),default);Assert.Equal("Complete",report.Coverage);Assert.Equal(0,report.Counts.Breaking);Assert.DoesNotContain(report.Findings,x=>x.Operation?.Contains("outside",StringComparison.Ordinal)==true);
    }
    [Fact] public void MaintainedRequestParametersAreProvedTogetherAndRemovalStaysUnknown()
    {
        var input=ComparisonTestData.Parameters(false,true);input=input with{From=input.From with{Version=input.From.Version with{OpenapiDocument=OpenApiCompatibilityV2Tests.Root().ToJsonString()}},To=input.To with{Version=input.To.Version with{OpenapiDocument=OpenApiCompatibilityV2Tests.Root().ToJsonString()}}};
        var report=new ContractComparisonEngine().Compare(input,new(),default);Assert.Equal("Complete",report.Coverage);Assert.Contains(report.Findings,x=>x.Risk=="Breaking"&&x.WitnessHash is not null);
        report=new ContractComparisonEngine().Compare(input with{To=input.To with{Parameters=[]}},new(),default);Assert.Equal("Limited",report.Coverage);Assert.Contains(report.CoverageIssues,x=>x.Code=="removed_parameter");
    }
    [Fact]public void CompatibleChangesAreVisibleWithoutNoiseFromDifferentVersionUris()
    {
        var original=OpenApiCompatibilityV2Tests.Root();var same=OpenApiCompatibilityV2Tests.Compare(original.ToJsonString(),original.ToJsonString());Assert.Empty(same.Findings);
        var changed=OpenApiCompatibilityV2Tests.Compare(original.ToJsonString(),OpenApiCompatibilityV2Tests.Root("{\"type\":\"number\"}").ToJsonString());Assert.Contains(changed.Findings,x=>x.Risk=="Compatible"&&x.ChangeKind=="Changed"&&x.RuleId=="openapi.request");
    }
    [Theory][InlineData("{\"type\":\"number\",\"maximum\":10}","{\"type\":\"number\",\"maximum\":20}")][InlineData("{\"type\":\"string\",\"maxLength\":10}","{\"type\":\"string\",\"maxLength\":20}")][InlineData("{\"type\":\"string\"}","{\"type\":[\"string\",\"null\"]}")]
    public void NestedResponseWitnessesRespectEveryHttpParent(string before,string after)
    {
        var report=OpenApiCompatibilityV2Tests.Compare(OpenApiCompatibilityV2Tests.Root(before,"response").ToJsonString(),OpenApiCompatibilityV2Tests.Root(after,"response").ToJsonString());Assert.Contains(report.Findings,x=>x.Risk=="Breaking"&&x.WitnessHash is not null);Assert.Equal("Complete",report.Coverage);
    }
    [Fact]public void UnknownWireCannotClaimCompleteWhenUnchanged()
    {
        var root=OpenApiCompatibilityV2Tests.Root();var op=root["paths"]!["/items"]!["post"]!;op["parameters"]=JsonNode.Parse("[{\"name\":\"q\",\"in\":\"query\",\"style\":\"custom-wire\",\"schema\":{\"type\":\"string\"}}]");var report=OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString());Assert.Equal("Limited",report.Coverage);Assert.True(report.Counts.Unknown>0);
    }
    [Fact]public void UnknownSecurityExtensionCannotClaimCompleteWhenUnchanged()
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["components"]=JsonNode.Parse("{\"securitySchemes\":{\"key\":{\"type\":\"apiKey\",\"in\":\"header\",\"name\":\"X-Key\",\"x-runtime-validation\":true}}}");root["security"]=JsonNode.Parse("[{\"key\":[]}]");var report=OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString());Assert.Equal("Limited",report.Coverage);Assert.True(report.Counts.Unknown>0);
    }
    [Fact]public void ReferencedOperationResolvesAuthenticationInItsOwnSourceDocument()
    {
        ContractVersionInput Version(string header){
            var uri=new Uri("https://contracts.invalid/root.json");var externalUri=new Uri("https://external.invalid/operations.json");var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=JsonNode.Parse("{\"/items\":{\"$ref\":\"https://external.invalid/operations.json#/paths/~1items\"}}");
            void Security(JsonObject doc,string name){doc["components"]=new JsonObject{["securitySchemes"]=new JsonObject{["key"]=new JsonObject{["type"]="apiKey",["in"]="header",["name"]=name}}};doc["security"]=JsonNode.Parse("[{\"key\":[]}]");}
            Security(root,"ROOT");var external=OpenApiCompatibilityV2Tests.Root();Security(external,header);var reader=new ContractDocumentReader();var documents=new[]{reader.Read(new(uri,root.ToJsonString(),"json"),new(),default),reader.Read(new(externalUri,external.ToJsonString(),"json"),new(),default)};var bundle=ContractBundleCodec.Create(uri,documents);
            return ComparisonTestData.Version(root.ToJsonString()) with{Sources=new(uri,documents.Select(x=>x.Source).ToArray(),bundle.Hash,new(documents.Select(x=>new ContractResourceSource(x.Source.LogicalUri,x.Source.Format)).ToArray(),[]))};
        }
        var report=new ContractComparisonEngine().Compare(new(Version("EXTERNAL-A"),Version("EXTERNAL-B")),new(),default);Assert.Equal("Limited",report.Coverage);Assert.Contains(report.CoverageIssues,x=>x.Code=="security_implementation");
    }
    [Theory][InlineData("{\"type\":\"string\",\"enum\":true}")][InlineData("{\"type\":\"object\",\"required\":true}")]
    public void InvalidSchemaNeverProducesAnAcceptablePartialComparison(string schema)
    {
        var report=OpenApiCompatibilityV2Tests.Compare(OpenApiCompatibilityV2Tests.Root(schema).ToJsonString(),OpenApiCompatibilityV2Tests.Root(schema).ToJsonString());Assert.Equal("Invalid",report.Coverage);Assert.Empty(report.Findings);
    }
    [Fact]public void EmptyEnumIsAValidEmptyValueDomainInOas31()
    {
        var root=OpenApiCompatibilityV2Tests.Root("{\"enum\":[]}");var report=OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString());Assert.Equal("Complete",report.Coverage);Assert.Equal(0,report.Counts.Breaking);
    }
    [Theory][InlineData(64,"Complete")][InlineData(65,"Invalid")]
    public void InternalWrapperDoesNotChangeRawMaintainedSchemaDepthBudget(int depth,string coverage)
    {
        var schema="{\"type\":\"string\"}";for(var i=1;i<depth;i++)schema="{\"type\":\"array\",\"items\":"+schema+"}";var input=ComparisonTestData.Schemas(schema,schema);Assert.Equal(coverage,new ContractComparisonEngine().Compare(input,new(),default).Coverage);
    }
    [Fact]public void V2AdmitsLegalDocumentAndSourcePairAboveTheOldTwoMiBLimit()
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=new JsonObject();root["info"]!["description"]=new string('a',1_100_000);var raw=root.ToJsonString();var a=ComparisonTestData.Version(raw);a=a with{Version=a.Version with{OpenapiSource=raw,SourceFormat="json"}};var b=ComparisonTestData.Version(raw);b=b with{Version=b.Version with{OpenapiSource=raw,SourceFormat="json"}};
        var input=new ComparisonInput(a,b);Assert.Equal("Complete",new ContractComparisonEngine().Compare(input,new(),default).Coverage);Assert.Equal("Invalid",new ContractComparisonEngine().Compare(input,new(MaxSideBytes:2*1024*1024,MaxPairBytes:4*1024*1024),default).Coverage);
    }
    [Fact]public void ContradictoryMaintainedParameterSourceRemainsVisible()
    {
        var input=ComparisonTestData.Parameters(false,false);var root=OpenApiCompatibilityV2Tests.Root();root["paths"]!["/items"]!["post"]!["parameters"]=JsonNode.Parse("[{\"name\":\"name\",\"in\":\"query\",\"schema\":{\"type\":\"integer\"}}]");input=input with{From=input.From with{Version=input.From.Version with{OpenapiDocument=root.ToJsonString()}},To=input.To with{Version=input.To.Version with{OpenapiDocument=root.ToJsonString()}}};var report=new ContractComparisonEngine().Compare(input,new(),default);Assert.Equal("Limited",report.Coverage);Assert.Contains(report.CoverageIssues,x=>x.Code=="source_conflict");
    }
    [Fact]public void UnusedUnknownSchemaVocabularyStillPreventsCompleteCoverage()
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=new JsonObject();root["components"]=JsonNode.Parse("{\"schemas\":{\"Value\":{\"type\":\"string\",\"customRule\":true}}}");var report=OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString());Assert.Equal("Limited",report.Coverage);Assert.True(report.Counts.Unknown>0);
    }
    [Fact]public void MaintainedResponsesUseExactStatusBeforeDefault()
    {
        var a=ComparisonTestData.Version(OpenApiCompatibilityV2Tests.Root().ToJsonString());a=a with{Version=a.Version with{OpenapiDocument=a.Version.OpenapiDocument!.Replace("\"/items\"","\"/unused\"")}};var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=new JsonObject();a=a with{Version=a.Version with{OpenapiDocument=root.ToJsonString()},Schemas=[new(Guid.NewGuid(),a.Version.Id,"response","Default",null,"application/json","{\"type\":\"number\"}",null,null),new(Guid.NewGuid(),a.Version.Id,"response","Exact",200,"application/json","{\"type\":\"string\"}",null,null)]};var b=ComparisonTestData.Version(root.ToJsonString());b=b with{Schemas=[new(Guid.NewGuid(),b.Version.Id,"response","Exact",200,"application/json","{\"type\":\"integer\"}",null,null)]};var report=new ContractComparisonEngine().Compare(new(a,b),new(),default);Assert.Contains(report.Findings,x=>x.Risk=="Breaking"&&x.WitnessHash is not null);Assert.Equal("Complete",report.Coverage);
    }
    [Fact]public void UnknownEncodingBehaviorIsVisibleEvenWhenUnchanged()
    {
        var root=OpenApiCompatibilityV2Tests.Root("{\"type\":\"object\"}");root["paths"]!["/items"]!["post"]!["requestBody"]!["content"]!["application/json"]!["encoding"]=JsonNode.Parse("{\"value\":{\"x-wire-rule\":true}}");var report=OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString());Assert.Equal("Limited",report.Coverage);Assert.Contains(report.CoverageIssues,x=>x.Code=="unsupported_openapi_field");
    }
    [Fact]public void MappedMaintenanceDoesNotEraseOriginalSourceConflict()
    {
        ContractVersionInput Input(){
            var root=OpenApiCompatibilityV2Tests.Root("{\"type\":\"integer\"}");var version=ComparisonTestData.Version(root.ToJsonString());var id=Guid.NewGuid();var uri=new Uri("https://fixed.invalid/source.json");var doc=new ContractDocumentReader().Read(new(uri,root.ToJsonString(),"json"),new(),default);var bundle=ContractBundleCodec.Create(uri,[doc]);
            return version with{Schemas=[new(id,version.Version.Id,"request","Body",null,"application/json","{\"type\":\"string\"}",null,null)],Sources=new(uri,[doc.Source],bundle.Hash,new([new(uri,"json")],[new(id,"schema",uri,"/paths/~1items/post/requestBody/content/application~1json/schema",ContractNormalizer.Hash(System.Text.Encoding.UTF8.GetBytes("{\"type\":\"string\"}")),uri)]))};
        }
        var report=new ContractComparisonEngine().Compare(new(Input(),Input()),new(),default);Assert.Equal("Limited",report.Coverage);Assert.Contains(report.CoverageIssues,x=>x.Code=="source_conflict");
    }
    [Fact]public void UnmappedMaintainedResponseChangesAreIncludedInOperationParent()
    {
        var a=ComparisonTestData.Version(OpenApiCompatibilityV2Tests.Root("{\"type\":\"integer\"}","response").ToJsonString());var b=ComparisonTestData.Version(a.Version.OpenapiDocument!);
        a=a with{Schemas=[new(Guid.NewGuid(),a.Version.Id,"response","Body",200,"application/json","{\"type\":\"integer\"}",null,null)]};b=b with{Schemas=[new(Guid.NewGuid(),b.Version.Id,"response","Body",200,"application/json","{\"type\":\"number\"}",null,null)]};
        var report=new ContractComparisonEngine().Compare(new(a,b),new(),default);Assert.Contains(report.Findings,x=>x.Risk=="Breaking"&&x.WitnessHash is not null);Assert.Contains(report.CoverageIssues,x=>x.Code=="source_conflict");
    }
    [Fact]public void MaintainedDefaultResponseCoversStatusesBeyondTwoHundred()
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=new JsonObject();var a=ComparisonTestData.Version(root.ToJsonString());var b=ComparisonTestData.Version(root.ToJsonString());
        a=a with{Schemas=[new(Guid.NewGuid(),a.Version.Id,"response","Exact",200,"application/json","{\"type\":\"integer\"}",null,null)]};b=b with{Schemas=[new(Guid.NewGuid(),b.Version.Id,"response","Default",null,"application/json","{\"type\":\"integer\"}",null,null)]};
        var report=new ContractComparisonEngine().Compare(new(a,b),new(),default);Assert.Contains(report.Findings,x=>x.Risk=="Breaking"&&x.Pointer!="/definitions/responses/200");Assert.Equal("Complete",report.Coverage);
    }
    [Fact]public void OperationMetadataChangesRemainVisibleWithoutContractRisk()
    {
        var a=OpenApiCompatibilityV2Tests.Root();var b=(JsonObject)a.DeepClone();b["paths"]!["/items"]!["post"]!["summary"]="Renamed summary";
        var report=OpenApiCompatibilityV2Tests.Compare(a.ToJsonString(),b.ToJsonString());Assert.Equal("Complete",report.Coverage);Assert.Contains(report.Findings,x=>x.Risk=="Compatible"&&x.Pointer.EndsWith("/summary",StringComparison.Ordinal));
    }
    [Theory][InlineData("{\"enum\":true}","Invalid")][InlineData("{\"$ref\":\"#/components/schemas/Missing\"}","Invalid")][InlineData("{\"$dynamicAnchor\":\"item\",\"type\":\"string\"}","Limited")]
    public void UnusedSchemasHaveStructuralReferenceAndDynamicCoverage(string schema,string coverage)
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=new JsonObject();root["components"]=new JsonObject{["schemas"]=new JsonObject{["Unused"]=JsonNode.Parse(schema)}};var report=OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString());Assert.Equal(coverage,report.Coverage);if(coverage=="Invalid")Assert.Empty(report.Findings);else Assert.True(report.Counts.Unknown>0);
    }
    [Theory][InlineData("3.0.3","Invalid")][InlineData("3.1.0","Complete")]
    public void NonOAuthRolesRespectTheSelectedOpenApiDialect(string version,string coverage)
    {
        var root=OpenApiCompatibilityV2Tests.Root(version:version);root["components"]=JsonNode.Parse("{\"securitySchemes\":{\"key\":{\"type\":\"apiKey\",\"in\":\"header\",\"name\":\"X-Key\"}}}");root["security"]=JsonNode.Parse("[{\"key\":[\"reader\"]}]");Assert.Equal(coverage,OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString()).Coverage);
    }
    [Theory][InlineData("{\"clientCredentials\":{\"scopes\":{}}}","Invalid")][InlineData("{\"clientCredentials\":{\"tokenUrl\":\"https://auth.invalid/token\",\"scopes\":{},\"x-flow-runtime\":true}}","Limited")]
    public void OAuthFlowStructureAndUnknownFieldsAreNotSilentlyIgnored(string flows,string coverage)
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["components"]=new JsonObject{["securitySchemes"]=new JsonObject{["oauth"]=new JsonObject{["type"]="oauth2",["flows"]=JsonNode.Parse(flows)}}};root["security"]=JsonNode.Parse("[{\"oauth\":[]}]");Assert.Equal(coverage,OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString()).Coverage);
    }
    [Theory][InlineData("deepObject")][InlineData("pipeDelimited")]
    public void UndefinedComplexParameterWireCannotClaimComplete(string style)
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]!["/items"]!["post"]!["parameters"]=new JsonArray(new JsonObject{["name"]="q",["in"]="query",["style"]=style,["explode"]=false,["schema"]=new JsonObject{["type"]="string"}});Assert.Equal("Limited",OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString()).Coverage);
    }
    [Fact]public void UnknownResponseHeaderStyleCannotClaimComplete()
    {
        var root=OpenApiCompatibilityV2Tests.Root(direction:"response");root["paths"]!["/items"]!["post"]!["responses"]!["200"]!["headers"]=JsonNode.Parse("{\"X-Key\":{\"style\":\"custom\",\"schema\":{\"type\":\"string\"}}}");Assert.Equal("Limited",OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString()).Coverage);
    }
    [Fact]public void PrivateMaintenanceDocumentMayExceedOneSourceDocumentButNotSideBudget()
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=new JsonObject();var a=ComparisonTestData.Version(root.ToJsonString());var schema=new JsonObject{["type"]="string",["description"]=new string('a',1_050_000)}.ToJsonString();a=a with{Schemas=[new(Guid.NewGuid(),a.Version.Id,"request","Body",null,"application/json",schema,null,null),new(Guid.NewGuid(),a.Version.Id,"response","Response",200,"application/json",schema,null,null)]};var b=a with{Version=a.Version with{Id=Guid.NewGuid()}};
        Assert.Equal("Complete",new ContractComparisonEngine().Compare(new(a,b),new(),default).Coverage);
    }
    [Fact]public void ReferenceChainCannotExceedThePrivateTraversalBudget()
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=new JsonObject();var schemas=new JsonObject();for(var i=0;i<100;i++)schemas["S"+i]=i==99?new JsonObject{["type"]="string"}:new JsonObject{["$ref"]="#/components/schemas/S"+(i+1)};root["components"]=new JsonObject{["schemas"]=schemas};var report=OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString());Assert.Equal("Invalid",report.Coverage);Assert.Empty(report.Findings);
    }
    [Fact]public void OpenApiFindingPointersDoNotConcatenateTwoDocumentRoots()
    {
        var report=OpenApiCompatibilityV2Tests.Compare(OpenApiCompatibilityV2Tests.Root("{\"type\":\"integer\"}","response").ToJsonString(),OpenApiCompatibilityV2Tests.Root("{\"type\":\"number\"}","response").ToJsonString());var finding=Assert.Single(report.Findings,x=>x.Risk=="Breaking");Assert.DoesNotContain("/components/schemas/__webapi_http_contract",finding.Pointer,StringComparison.Ordinal);Assert.Equal(1,finding.Pointer.Split("/paths/",StringSplitOptions.None).Length-1);
    }
    [Fact]public void CurrentRulesRecordTheParameterAndSecurityFamiliesActuallyApplied()
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]!["/items"]!["post"]!["parameters"]=JsonNode.Parse("[{\"name\":\"q\",\"in\":\"query\",\"schema\":{\"type\":\"string\"}}]");root["components"]=JsonNode.Parse("{\"securitySchemes\":{\"key\":{\"type\":\"apiKey\",\"in\":\"header\",\"name\":\"X-Key\"}}}");root["security"]=JsonNode.Parse("[{\"key\":[]}]");var report=OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString());Assert.Contains("openapi.parameters",report.Provenance!.AppliedRuleIds);Assert.Contains("openapi.security",report.Provenance.AppliedRuleIds);
    }
    [Fact]public void StrictModeCoversUnusedUnregisteredFormats()
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=new JsonObject();root["components"]=JsonNode.Parse("{\"schemas\":{\"Unused\":{\"type\":\"string\",\"format\":\"private-format\"}}}");var input=new ComparisonInput(ComparisonTestData.Version(root.ToJsonString()),ComparisonTestData.Version(root.ToJsonString()),"Strict");Assert.Equal("Limited",new ContractComparisonEngine().Compare(input,new(),default).Coverage);Assert.Equal("Complete",new ContractComparisonEngine().Compare(input with{FormatMode="Annotation"},new(),default).Coverage);
    }
    [Fact]public void PreviouslySavedIndependentMaintenanceSourceCanBeReconstructed()
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]=new JsonObject();var version=ComparisonTestData.Version(root.ToJsonString());var id=Guid.NewGuid();var uri=new Uri("https://fixed.invalid/source.json");var document=new ContractDocumentReader().Read(new(uri,root.ToJsonString(),"json"),new(),default);var bundle=ContractBundleCodec.Create(uri,[document]);version=version with{Schemas=[new(id,version.Version.Id,"request","Body",null,"application/json","{\"type\":\"string\"}",null,null)],Sources=new(uri,[document.Source],bundle.Hash,new([new(uri,"json")],[new(id,"schema",uri,"/components/schemas/__maintained_"+id.ToString("N"),"maintenance-hash",uri)]))};var report=new ContractComparisonEngine().Compare(new(version,version),new(),default);Assert.Equal("Complete",report.Coverage);
    }
    [Theory][InlineData("{}")][InlineData("{\"700\":{\"description\":\"bad status\"}}")][InlineData("{\"named-status\":{\"description\":\"bad status\"}}")]
    public void InvalidResponseCoverageCannotBeSilentlySkipped(string responses)
    {
        var root=OpenApiCompatibilityV2Tests.Root();root["paths"]!["/items"]!["post"]!["responses"]=JsonNode.Parse(responses);var report=OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString());Assert.Equal("Invalid",report.Coverage);Assert.Empty(report.Findings);
    }
    [Fact]public void OpenApi30ReferenceSiblingsDoNotAcquireModernSchemaSemantics()
    {
        var root=OpenApiCompatibilityV2Tests.Root("{\"$ref\":\"#/components/schemas/Value\",\"$defs\":{\"Ignored\":{\"$ref\":\"#/missing\"}}}",version:"3.0.3");root["components"]=JsonNode.Parse("{\"schemas\":{\"Value\":{\"type\":\"integer\"}}}");Assert.Equal("Complete",OpenApiCompatibilityV2Tests.Compare(root.ToJsonString(),root.ToJsonString()).Coverage);
    }
    [Fact] public void FullHttpParentEmptinessPreventsParameterAndStatusFalsePositives()
    {
        var a=OpenApiCompatibilityV2Tests.Root("false");a["paths"]!["/items"]!["post"]!["requestBody"]!["required"]=true;var b=(JsonObject)a.DeepClone();b["paths"]!["/items"]!["post"]!["parameters"]=JsonNode.Parse("[{\"name\":\"q\",\"in\":\"query\",\"required\":true,\"schema\":{\"type\":\"string\"}}]");
        var report=OpenApiCompatibilityV2Tests.Compare(a.ToJsonString(),b.ToJsonString());Assert.Equal("Complete",report.Coverage);Assert.Equal(0,report.Counts.Breaking);
        a=OpenApiCompatibilityV2Tests.Root("false","response");a["paths"]!["/items"]!["post"]!["responses"]=new JsonObject{["200"]=a["paths"]!["/items"]!["post"]!["responses"]!["200"]!.DeepClone()};b=(JsonObject)a.DeepClone();b["paths"]!["/items"]!["post"]!["responses"]!["201"]=b["paths"]!["/items"]!["post"]!["responses"]!["200"]!.DeepClone();
        report=OpenApiCompatibilityV2Tests.Compare(a.ToJsonString(),b.ToJsonString());Assert.Equal("Complete",report.Coverage);Assert.Equal(0,report.Counts.Breaking);
    }
}

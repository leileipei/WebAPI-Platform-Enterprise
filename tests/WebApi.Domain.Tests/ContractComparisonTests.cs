using System.Text.Json;
using WebApi.Infrastructure.Comparisons;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class ContractComparisonTests
{
    private static readonly ContractComparisonEngine Engine=new();
    [Fact] public void OperationsAndParameterIdentityFollowSpec()
    {
        var doc="{\"openapi\":\"3.0.3\",\"paths\":{\"/orders\":{\"get\":{\"responses\":{\"200\":{\"description\":\"ok\"}}}}}}";
        var add=Engine.Compare(new(ComparisonTestData.Version(),ComparisonTestData.Version(doc)),new(),default);Assert.Contains(add.Findings,x=>x.ChangeKind=="Added"&&x.Risk=="Compatible");
        var remove=Engine.Compare(new(ComparisonTestData.Version(doc),ComparisonTestData.Version()),new(),default);Assert.Contains(remove.Findings,x=>x.ChangeKind=="Removed"&&x.Risk=="Breaking");
        Assert.Empty(Engine.Compare(ComparisonTestData.Parameters(false,false,"header","X-Name","x-name"),new(),default).Findings);
        Assert.Contains(Engine.Compare(ComparisonTestData.Parameters(false,true),new(),default).Findings,x=>x.Risk=="Breaking");
        Assert.Contains(Engine.Compare(ComparisonTestData.Parameters(false,false,"query","old","new"),new(),default).Findings,x=>x.Risk=="Unknown");
    }
    [Fact] public void LimitedZeroDiffStillHasUnknown()
    {var a=ComparisonTestData.Version() with {Version=ComparisonTestData.Version().Version with {OpenapiDocument=null}};var r=Engine.Compare(new(a,a),new(),default);Assert.Equal("Limited",r.Coverage);Assert.True(r.Counts.Unknown>0);Assert.Empty(r.Findings);}
    [Theory][InlineData("true")][InlineData("[]")]
    public void NonObjectParameterSchemaIsLimitedRatherThanCrash(string schema)
    {var doc="{\"openapi\":\"3.0.3\",\"paths\":{\"/x\":{\"get\":{\"parameters\":[{\"name\":\"name\",\"in\":\"query\",\"schema\":"+schema+"}],\"responses\":{\"200\":{\"description\":\"ok\"}}}}}}";foreach(var baseline in new[]{doc,"{\"openapi\":\"3.0.3\",\"paths\":{}}"}){var r=Engine.Compare(new(ComparisonTestData.Version(baseline),ComparisonTestData.Version(doc)),new(),default);Assert.Equal("Limited",r.Coverage);Assert.True(r.Counts.Unknown>0);}}
    [Theory]
    [InlineData("{\"openapi\":\"3.0.3\",\"servers\":[{\"url\":\"https://example.invalid\"}],\"paths\":{}}")]
    [InlineData("{\"openapi\":\"3.0.3\",\"paths\":{\"/x\":{\"servers\":[{\"url\":\"https://example.invalid\"}],\"get\":{\"responses\":{\"200\":{\"description\":\"ok\"}}}}}}")]
    [InlineData("{\"openapi\":\"3.0.3\",\"paths\":{\"/x\":{\"get\":{\"responses\":{\"200\":{\"headers\":{\"H\":{\"schema\":{\"type\":\"string\"}}},\"description\":\"ok\"}}}}}}")]
    [InlineData("{\"openapi\":\"3.0.3\",\"paths\":{\"/x\":{\"get\":{\"responses\":{\"200\":{\"links\":{\"next\":{\"operationId\":\"next\"}},\"description\":\"ok\"}}}}}}")]
    [InlineData("{\"openapi\":\"3.0.3\",\"paths\":{\"/x\":{\"get\":{\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"string\"},\"encoding\":{\"body\":{\"style\":\"form\"}}}}},\"responses\":{\"200\":{\"description\":\"ok\"}}}}}}")]
    public void UnsupportedOpenApiConstraintCannotClaimComplete(string doc)
    {foreach(var baseline in new[]{doc,"{\"openapi\":\"3.0.3\",\"paths\":{}}"}){var r=Engine.Compare(new(ComparisonTestData.Version(baseline),ComparisonTestData.Version(doc)),new(),default);Assert.Equal("Limited",r.Coverage);Assert.True(r.Counts.Unknown>0);}}
    [Theory][InlineData("{\"type\":\"object\",\"properties\":{\"data\":null}}")][InlineData("{\"type\":\"string\",\"$ref\":{}}")]
    public void NullPropertyAndInvalidReferenceCannotClaimComplete(string schema)
    {var r=Engine.Compare(ComparisonTestData.Schemas(schema,schema),new(),default);Assert.Equal("Invalid",r.Coverage);Assert.True(r.Counts.Unknown>0);}
    [Theory] [InlineData("{\"type\":\"string\",\"allOf\":[{\"type\":\"string\"}]}")] [InlineData("{\"$ref\":\"https://example.invalid/schema.json\"}")] [InlineData("{\"$ref\":\"#/missing\"}")]
    public void CompositeAndCyclicReferencesNeverClaimComplete(string schema)
    {var r=Engine.Compare(ComparisonTestData.Schemas(schema,schema),new(),default);Assert.Equal("Limited",r.Coverage);Assert.True(r.Counts.Unknown>0);}
    [Fact] public void CyclicLocalRefDoesNotLoop()
    {var d="{\"openapi\":\"3.0.3\",\"paths\":{},\"components\":{\"schemas\":{\"A\":{\"$ref\":\"#/components/schemas/A\"}}}}";Assert.Equal("Limited",Engine.Compare(new(ComparisonTestData.Version(d),ComparisonTestData.Version(d)),new(),default).Coverage);}
    [Fact] public void DamagedAndDuplicateInputsAreInvalid()
    {
        Assert.Equal("Invalid",Engine.Compare(ComparisonTestData.Schemas("{broken","{}"),new(),default).Coverage);
        var i=ComparisonTestData.Parameters(false,false);var duplicate=i.From with {Parameters=[i.From.Parameters[0],i.From.Parameters[0] with {Id=Guid.NewGuid()}]};Assert.Equal("Invalid",Engine.Compare(i with {From=duplicate},new(),default).Coverage);
        var d="{\"openapi\":\"3.0.3\",\"paths\":{},\"paths\":{}}";Assert.Equal("Invalid",Engine.Compare(new(ComparisonTestData.Version(d),ComparisonTestData.Version()),new(),default).Coverage);
    }
    [Fact] public void BudgetLimitsCannotBeAccepted()
    {
        var oversized=ComparisonTestData.Version() with {Version=ComparisonTestData.Version().Version with {OpenapiSource=new string('x',2*1024*1024)}};
        Assert.Equal("Invalid",Engine.Compare(new(oversized,ComparisonTestData.Version()),new(),default).Coverage);
        var i=ComparisonTestData.Schemas("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"string\"}}}","{\"type\":\"object\",\"properties\":{}}");
        Assert.Equal("Invalid",Engine.Compare(i,new(MaxFindings:1),default).Coverage);
        Assert.Equal("Invalid",Engine.Compare(i,new(MaxNodes:1),default).Coverage);
        using var c=new CancellationTokenSource();c.Cancel();Assert.Throws<OperationCanceledException>(()=>Engine.Compare(i,new(),c.Token));
    }
    [Fact] public void PointerAndContradictorySourcesArePreserved()
    {
        var r=Engine.Compare(ComparisonTestData.Schemas("{\"type\":\"object\",\"properties\":{\"a/b~c\":{\"type\":\"string\"}}}","{\"type\":\"object\",\"properties\":{}}","response"),new(),default);Assert.Contains(r.Findings,x=>x.Pointer.Contains("a~1b~0c",StringComparison.Ordinal));
        var i=ComparisonTestData.Parameters(false,false);var doc="{\"openapi\":\"3.0.3\",\"paths\":{\"/x\":{\"get\":{\"parameters\":[{\"name\":\"name\",\"in\":\"query\",\"schema\":{\"type\":\"integer\"}}],\"responses\":{\"200\":{\"description\":\"ok\"}}}}}}";
        Assert.Contains(Engine.Compare(i with {To=i.To with {Version=i.To.Version with {OpenapiDocument=doc}}},new(),default).CoverageIssues,x=>x.Code=="source_conflict");
    }
    [Fact] public void ExamplesNeverAppearInEvidenceAndUnsupportedVersionsAreLimited()
    {
        var r=Engine.Compare(ComparisonTestData.Schemas("{\"type\":\"string\",\"example\":\"EXAMPLE_PRIVATE_A\"}","{\"type\":\"string\",\"example\":\"EXAMPLE_PRIVATE_B\"}"),new(),default);Assert.DoesNotContain("EXAMPLE_PRIVATE",JsonSerializer.Serialize(r));Assert.Contains(r.Findings,x=>x.ChangeKind=="Changed"&&x.Risk=="Compatible");
        var d="{\"openapi\":\"3.1.0\",\"paths\":{}}";Assert.Equal("Limited",Engine.Compare(new(ComparisonTestData.Version(d),ComparisonTestData.Version(d)),new(),default).Coverage);
    }
    [Theory]
    [InlineData("{\"openapi\":\"3.0.3\",\"paths\":{},\"components\":[]}")]
    [InlineData("{\"openapi\":\"3.0.3\",\"paths\":{\"/x\":{\"get\":{\"requestBody\":[],\"responses\":{\"200\":{\"description\":\"ok\"}}}}}}")]
    [InlineData("{\"openapi\":\"3.0.3\",\"paths\":{\"/x\":{\"get\":{\"responses\":{\"200\":{\"content\":{\"application/json\":[]}}}}}}}")]
    public void MalformedOpenApiStructuresAreInvalidRatherThanCrash(string doc)=>Assert.Equal("Invalid",Engine.Compare(new(ComparisonTestData.Version(doc),ComparisonTestData.Version(doc)),new(),default).Coverage);
    [Theory] [InlineData("{\"type\":\"object\",\"required\":true}")] [InlineData("{\"type\":\"object\",\"properties\":[]}")] [InlineData("{\"type\":\"string\",\"enum\":[]}")]
    public void MalformedSchemaShapesCannotClaimComplete(string schema)=>Assert.Equal("Invalid",Engine.Compare(ComparisonTestData.Schemas(schema,schema),new(),default).Coverage);
    [Fact] public void UnsupportedUnchangedSchemaStillHasUnknown()
    {var r=Engine.Compare(ComparisonTestData.Schemas("{\"customRule\":true}","{\"customRule\":true}"),new(),default);Assert.Equal("Limited",r.Coverage);Assert.True(r.Counts.Unknown>0);}
    [Theory] [InlineData("application/json","application/xml")] [InlineData("application/xml","application/json")]
    public void MediaTypeChangeIsUnknownRatherThanFieldRemoval(string before,string after)
    {var input=ComparisonTestData.Schemas("{\"type\":\"string\"}","{\"type\":\"string\"}","response");input=input with {From=input.From with {Schemas=[input.From.Schemas[0] with {ContentType=before}]},To=input.To with {Schemas=[input.To.Schemas[0] with {ContentType=after}]}};var r=Engine.Compare(input,new(),default);Assert.DoesNotContain(r.Findings,x=>x.Risk=="Breaking");Assert.Contains(r.Findings,x=>x.Risk=="Unknown");Assert.Equal(0,r.Counts.Added);Assert.Equal(0,r.Counts.Removed);}
    [Fact] public void Depth64IsSupportedAnd65IsInvalid()
    {var schema="{\"type\":\"string\"}";for(var n=1;n<64;n++)schema="{\"type\":\"array\",\"items\":"+schema+"}";Assert.Equal("Complete",Engine.Compare(ComparisonTestData.Schemas(schema,schema),new(),default).Coverage);schema="{\"type\":\"array\",\"items\":"+schema+"}";Assert.Equal("Invalid",Engine.Compare(ComparisonTestData.Schemas(schema,schema),new(),default).Coverage);}
    [Fact] public void Output5000IsCompleteAnd5001IsInvalid()
    {
        var properties=new System.Text.Json.Nodes.JsonObject();for(var n=0;n<5000;n++)properties["p"+n]=new System.Text.Json.Nodes.JsonObject { ["type"]="string" };
        string Doc()=>new System.Text.Json.Nodes.JsonObject { ["type"]="object",["properties"]=properties.DeepClone() }.ToJsonString();
        var r=Engine.Compare(ComparisonTestData.Schemas("{\"type\":\"object\",\"properties\":{}}",Doc()),new(),default);Assert.Equal("Complete",r.Coverage);Assert.Equal(5000,r.Counts.Added);
        properties["extra"]=new System.Text.Json.Nodes.JsonObject { ["type"]="string" };Assert.Equal("Invalid",Engine.Compare(ComparisonTestData.Schemas("{\"type\":\"object\",\"properties\":{}}",Doc()),new(),default).Coverage);
    }
    [Fact] public void ExactByteAndPairBudgetBoundariesAreEnforced()
    {
        var input=new ComparisonInput(ComparisonTestData.Version(),ComparisonTestData.Version());var left=JsonSerializer.SerializeToUtf8Bytes(input.From).Length;var right=JsonSerializer.SerializeToUtf8Bytes(input.To).Length;
        Assert.Equal("Complete",Engine.Compare(input,new(MaxSideBytes:Math.Max(left,right),MaxPairBytes:left+right),default).Coverage);
        Assert.Equal("Invalid",Engine.Compare(input,new(MaxSideBytes:Math.Max(left,right)-1),default).Coverage);
        Assert.Equal("Invalid",Engine.Compare(input,new(MaxPairBytes:left+right-1),default).Coverage);
    }
}

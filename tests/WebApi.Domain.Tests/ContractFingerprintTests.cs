using WebApi.Infrastructure.Comparisons;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class ContractFingerprintTests
{
    [Fact] public void FingerprintIgnoresObjectOrderAndPublishingStateButNotRevision()
    {
        var a=ComparisonTestData.Version();var b=ComparisonTestData.Version();var input=new ComparisonInput(a,b);
        var hash=ContractNormalizer.Fingerprint(input,"compatibility-v1");
        var published=b with {Version=b.Version with {Status="Published",SealedAt=DateTimeOffset.UtcNow,OpenapiDocument="{\"paths\":{},\"openapi\":\"3.0.3\"}"}};
        Assert.Equal(hash,ContractNormalizer.Fingerprint(new(a,published),"compatibility-v1"));
        Assert.NotEqual(hash,ContractNormalizer.Fingerprint(new(a,b with {Version=b.Version with {Revision=2}}),"compatibility-v1"));
        Assert.NotEqual(hash,ContractNormalizer.Fingerprint(new(a,b),"compatibility-v2"));
        Assert.NotEqual(hash,ContractNormalizer.Fingerprint(new(a,b with {Version=b.Version with {OpenapiSource="different source"}}),"compatibility-v1"));
    }
    [Fact] public void SetOrderAndDatabaseOrderDoNotCreateChanges()
    {
        var i=ComparisonTestData.Schemas("{\"type\":\"object\",\"required\":[\"a\",\"b\"],\"properties\":{\"a\":{\"type\":\"string\"},\"b\":{\"type\":\"string\"}}}","{\"required\":[\"b\",\"a\"],\"properties\":{\"b\":{\"type\":\"string\"},\"a\":{\"type\":\"string\"}},\"type\":\"object\"}");
        Assert.Empty(new ContractComparisonEngine().Compare(i,new(),default).Findings);
    }
    [Fact] public void RegisteredLegacyFingerprintRetainsTheFixedOriginalShape()
    {
        var a=ComparisonTestData.Version();a=a with{Version=a.Version with{Id=Guid.Parse("00000000-0000-0000-0000-000000000010")}};var b=ComparisonTestData.Version();b=b with{Version=b.Version with{Id=Guid.Parse("00000000-0000-0000-0000-000000000011")}};
        Assert.Equal("a4dc058f65a4389ec264137fb9ad739c6e1c2a25a6e0a82502e22d7724e0a570",LegacyV1Fingerprint.Compute(new(a,b)));
        Assert.Equal(LegacyV1Fingerprint.Compute(new(a,b)),LegacyV1Fingerprint.Compute(new(a with{Version=a.Version with{Dialect="oas-3.0"}},b with{Version=b.Version with{Status="Published",SealedAt=DateTimeOffset.UtcNow}})));
        Assert.Throws<WebApi.Contracts.Common.ApiException>(()=>CompatibilityEngineRegistry.Fingerprint(new(a,b),"compatibility-client-supplied"));
    }

    [Fact] public void V2FingerprintBindsSourcesDialectAdapterAndFormatWhileV1RetainsItsProjection()
    {
        var a=ComparisonTestData.Version(OpenApiCompatibilityV2Tests.Root().ToJsonString());var b=ComparisonTestData.Version(OpenApiCompatibilityV2Tests.Root().ToJsonString());var input=new ComparisonInput(a,b);
        var source=new ComparisonSourceInput(new("https://contracts.invalid/root.json"),[new(new("https://contracts.invalid/root.json"),a.Version.OpenapiDocument!,"json")],"fixed-bundle",new([],[]));
        var mutations=new[]{input with{From=a with{Sources=source}},input with{From=a with{Version=a.Version with{Dialect="oas-3.1"}}},input with{FormatMode="Strict"},input with{AdapterVersion="different-adapter"}};
        foreach(var changed in mutations){Assert.NotEqual(ContractNormalizer.Fingerprint(input,"compatibility-v2"),ContractNormalizer.Fingerprint(changed,"compatibility-v2"));Assert.Equal(LegacyV1Fingerprint.Compute(input),LegacyV1Fingerprint.Compute(changed));}
    }

}

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
}

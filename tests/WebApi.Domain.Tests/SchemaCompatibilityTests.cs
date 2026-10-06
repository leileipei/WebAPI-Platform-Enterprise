using WebApi.Infrastructure.Comparisons;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class SchemaCompatibilityTests
{
    // Frozen v1 regression: these expectations intentionally describe the original algorithm.
    private static readonly LegacyV1ComparisonEngine Engine=new();
    [Theory]
    [InlineData("request","{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}","{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"]}","Breaking")]
    [InlineData("response","{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"]}","{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}","Breaking")]
    public void RequestRequiredAndResponseOptionalHaveDirectionalRisk(string direction,string before,string after,string risk)=>Assert.Contains(Engine.Compare(ComparisonTestData.Schemas(before,after,direction),new(MaxSideBytes:2*1024*1024,MaxPairBytes:4*1024*1024),default).Findings,x=>x.Risk==risk);
    [Theory]
    [InlineData("request","{\"type\":\"string\",\"enum\":[\"A\",\"B\"]}","{\"type\":\"string\",\"enum\":[\"A\"]}","Breaking")]
    [InlineData("response","{\"type\":\"string\",\"enum\":[\"A\"]}","{\"type\":\"string\",\"enum\":[\"A\",\"B\"]}","Breaking")]
    [InlineData("request","{\"type\":\"number\",\"minimum\":0}","{\"type\":\"number\",\"minimum\":10}","Breaking")]
    [InlineData("response","{\"type\":\"number\",\"maximum\":10}","{\"type\":\"number\",\"maximum\":20}","Breaking")]
    [InlineData("request","{\"type\":\"string\",\"enum\":[\"A\"]}","{\"type\":\"string\",\"enum\":[\"A\",\"B\"]}","Compatible")]
    [InlineData("response","{\"type\":\"number\",\"minimum\":0}","{\"type\":\"number\",\"minimum\":10}","Compatible")]
    [InlineData("request","{\"type\":\"string\",\"minLength\":5}","{\"type\":\"string\",\"minLength\":1}","Compatible")]
    [InlineData("response","{\"type\":\"string\",\"maxLength\":10}","{\"type\":\"string\",\"maxLength\":20}","Breaking")]
    [InlineData("request","{\"type\":\"string\",\"nullable\":true}","{\"type\":\"string\",\"nullable\":false}","Breaking")]
    [InlineData("response","{\"type\":\"string\",\"nullable\":false}","{\"type\":\"string\",\"nullable\":true}","Breaking")]
    [InlineData("request","{\"type\":\"integer\"}","{\"type\":\"string\"}","Unknown")]
    [InlineData("response","{\"type\":\"string\"}","{\"type\":\"string\",\"format\":\"uuid\"}","Unknown")]
    [InlineData("request","{\"type\":\"object\",\"properties\":{}}","{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}","Compatible")]
    [InlineData("response","{\"type\":\"object\",\"properties\":{}}","{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}","Unknown")]
    [InlineData("response","{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}","{\"type\":\"object\",\"properties\":{}}","Breaking")]
    [InlineData("request","{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}}}","{\"type\":\"object\",\"properties\":{}}","Unknown")]
    [InlineData("request","{\"type\":\"string\"}","{\"type\":\"string\",\"pattern\":\"a.*\"}","Unknown")]
    public void EnumAndBoundsRespectDirection(string direction,string before,string after,string risk)
    {var report=Engine.Compare(ComparisonTestData.Schemas(before,after,direction),new(MaxSideBytes:2*1024*1024,MaxPairBytes:4*1024*1024),default);Assert.Contains(report.Findings,x=>x.Risk==risk);if(risk=="Compatible")Assert.DoesNotContain(report.Findings,x=>x.Risk=="Breaking");}
    [Fact] public void MixedConstraintChangesCannotHideBreaking()
    {var r=Engine.Compare(ComparisonTestData.Schemas("{\"type\":\"number\",\"minimum\":0,\"maximum\":10}","{\"type\":\"number\",\"minimum\":5,\"maximum\":20}"),new(MaxSideBytes:2*1024*1024,MaxPairBytes:4*1024*1024),default);Assert.True(r.Counts.Breaking>0);}
}

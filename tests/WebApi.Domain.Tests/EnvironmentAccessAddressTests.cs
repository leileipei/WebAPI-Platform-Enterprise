using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Domain.Governance;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class EnvironmentAccessAddressTests
{
    [Fact] public void ExternalPrefixIsAddedExactlyOnceByRule()
    {
        Assert.Equal("https://api.test/gateway/mes/orders/{id}",ClientApiAddressBuilder.BuildTemplate("https://api.test","/gateway","/mes/orders/{id}"));
        Assert.Equal("https://api.test/mes/orders/{id}",ClientApiAddressBuilder.BuildTemplate("https://api.test","/","/mes/orders/{id}"));
        Assert.Equal("https://api.test/gateway/gateway/orders",ClientApiAddressBuilder.BuildTemplate("https://api.test","/gateway","/gateway/orders"));
    }
    [Theory]
    [InlineData("HTTPS://API.TEST:443/","https://api.test")]
    [InlineData("http://api.test:80","http://api.test")]
    [InlineData("http://[::1]:4196/","http://[::1]:4196")]
    [InlineData("https://例子.测试/","https://xn--fsqu00a.xn--0zwm56d")]
    public void OriginCanonicalizationPreservesMeaning(string input,string expected)
    {Assert.Equal(expected,EnvironmentAccessAddressValidator.Normalize(new(input,null,"/"),false).PublicOrigin);}
    [Theory]
    [InlineData("https://user:pass@api.test")]
    [InlineData("https://api.test?x=1")]
    [InlineData("https://api.test#x")]
    [InlineData("https://api.test/path")]
    [InlineData("https://api.test/../")]
    [InlineData("https://api.test/%2e%2e/")]
    [InlineData("https://api.test\\foo")]
    [InlineData("https://api.test:70000")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://api.test\n")]
    [InlineData("https://api.test/?")]
    [InlineData("https://api.test/#")]
    public void InvalidOriginIsRejectedWithoutNetwork(string input)
    {Assert.Equal(422,Assert.Throws<ApiException>(()=>EnvironmentAccessAddressValidator.Normalize(new(input,null,"/"),false)).Status);}
    [Theory]
    [InlineData("/gateway/","/gateway")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/v1/api_v2.-~", "/v1/api_v2.-~")]
    public void PrefixCanonicalization(string input,string expected)
    {Assert.Equal(expected,EnvironmentAccessAddressValidator.Normalize(new(null,null,input),false).BasePath);}
    [Theory]
    [InlineData("gateway")][InlineData("//gateway")][InlineData("/gateway//v1")]
    [InlineData("/../gateway")][InlineData("/./gateway")][InlineData("/gateway/%2f")]
    [InlineData("/gateway?x")][InlineData("/gateway#x")][InlineData("/网关")]
    public void UnsafePrefixIsRejected(string input)
    {Assert.Equal(422,Assert.Throws<ApiException>(()=>EnvironmentAccessAddressValidator.Normalize(new(null,null,input),false)).Status);}
    [Fact] public void ProductionOnlyPublicOriginRequiresHttps()
    {
        Assert.Throws<ApiException>(()=>EnvironmentAccessAddressValidator.Normalize(new("http://api.test",null,"/"),true));
        Assert.Equal("http://internal.test",EnvironmentAccessAddressValidator.Normalize(new("https://api.test","http://internal.test","/"),true).InternalOrigin);
        Assert.Null(EnvironmentAccessAddressValidator.Normalize(new("",null,"/"),true).PublicOrigin);
    }
    [Fact] public void LengthBudgetsAreNotSilentlyTruncated()
    {
        Assert.Throws<ApiException>(()=>EnvironmentAccessAddressValidator.Normalize(new("https://"+new string('a',2049),null,"/"),false));
        Assert.Equal(512,EnvironmentAccessAddressValidator.Normalize(new(null,null,"/"+new string('a',511)),false).BasePath.Length);
        Assert.Throws<ApiException>(()=>EnvironmentAccessAddressValidator.Normalize(new(null,null,"/"+new string('a',512)),false));
    }
    [Fact] public void ParameterValuesAreEncodedAsOneSegment()
    {Assert.Equal("https://api.test/gateway/orders/a%2Fb",ClientApiAddressBuilder.BuildExample("https://api.test","/gateway","/orders/{id}",new Dictionary<string,string>{{"id","a/b"}}));}
    [Fact] public void IncompleteAndCatchAllValuesRemainTemplates()
    {
        Assert.Equal("https://api.test/orders/{id}",ClientApiAddressBuilder.BuildExample("https://api.test","/","/orders/{id}",new Dictionary<string,string>()));
        Assert.Equal("https://api.test/files/{**path}",ClientApiAddressBuilder.BuildExample("https://api.test","/","/files/{**path}",new Dictionary<string,string>{{"path","a/b"}}));
    }
}

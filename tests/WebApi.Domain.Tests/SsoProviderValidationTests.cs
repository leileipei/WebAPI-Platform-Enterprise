using System.Text;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Sso;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class SsoProviderValidationTests
{
    private const string Valid="""{"organizationId":null,"name":"Enterprise","issuer":"https://identity.example.test/realms/enterprise","clientId":"console","secretRef":"file://sso/enterprise","scopes":["openid","profile","email"],"claimMapping":{"displayName":"name","email":"email"}}""";
    [Fact] public void ParsesValidProviderWithoutDiscardingFields()
    {
        var result=SsoProviderValidator.Parse(Encoding.UTF8.GetBytes(Valid));
        Assert.Equal("Enterprise",result.Name);Assert.Null(result.OrganizationId);Assert.Equal("file://sso/enterprise",result.SecretRef);
        Assert.Equal(new[]{"openid","profile","email"},result.Scopes);Assert.Equal("name",result.ClaimMapping.DisplayName);
    }
    [Theory]
    [InlineData("\"openid\",\"profile\",\"email\"","\"profile\"")]
    [InlineData("\"openid\",\"profile\",\"email\"","\"openid\",\"offline_access\"")]
    [InlineData("file://sso/enterprise","file:///etc/passwd")]
    [InlineData("file://sso/enterprise","file://sso/../password")]
    [InlineData("file://sso/enterprise","https://secret.example.test/")]
    [InlineData("https://identity.example.test/realms/enterprise","https://admin:password@identity.example.test/")]
    [InlineData("https://identity.example.test/realms/enterprise","https://identity.example.test/?token=secret")]
    [InlineData("https://identity.example.test/realms/enterprise","https://identity.example.test/#fragment")]
    [InlineData("\"displayName\":\"name\"","\"roles\":\"roles\"")]
    [InlineData("\"displayName\":\"name\"","\"displayName\":\"$.profile.name\"")]
    [InlineData("\"displayName\":\"name\"","\"displayName\":[\"name\"]")]
    [InlineData("\"organizationId\":null","\"organizationId\":\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("\"name\":\"Enterprise\"","\"name\":\" \"")]
    [InlineData("\"clientId\":\"console\"","\"clientId\":\" \"")]
    public void RejectsInvalidConfiguration(string from,string to)=>Assert.Equal(422,Assert.Throws<ApiException>(()=>SsoProviderValidator.Parse(Encoding.UTF8.GetBytes(Valid.Replace(from,to)))).Status);
    [Fact] public void RejectsDuplicateAndUnknownFields()
    {
        foreach(var json in new[]{Valid.Replace("\"name\":\"Enterprise\"","\"name\":\"Enterprise\",\"name\":\"Other\""),Valid.Replace("\"name\":\"Enterprise\"","\"name\":\"Enterprise\",\"clientSecret\":\"do-not-store\""),Valid.Replace("\"displayName\":\"name\"","\"displayName\":\"name\",\"displayName\":\"sub\"")})
            Assert.Equal(422,Assert.Throws<ApiException>(()=>SsoProviderValidator.Parse(Encoding.UTF8.GetBytes(json))).Status);
    }
    [Theory][InlineData("Enterprise",129)][InlineData("console",257)]
    public void RejectsOversizedFields(string value,int length)=>Assert.Equal(422,Assert.Throws<ApiException>(()=>SsoProviderValidator.Parse(Encoding.UTF8.GetBytes(Valid.Replace("\""+value+"\"","\""+new string('a',length)+"\"")))).Status);
    [Theory][InlineData("/apis",true)][InlineData("/settings/sso",true)][InlineData("/apis?filter=active",true)][InlineData("//evil.example",false)][InlineData("https://evil.example/",false)][InlineData("/\\evil.example",false)][InlineData("/auth/sso/complete",false)][InlineData("/login",false)][InlineData("/api/v1/auth/login",false)][InlineData("/apis%2f%2fevil",false)][InlineData("/apis\r\nLocation:evil",false)]
    [InlineData("/delivery/artifacts/artifact-a",true)]
    [InlineData("/delivery/policy",true)]
    [InlineData("/delivery%2fother",false)]
    public void ReturnPathCannotEscapeProductNavigation(string path,bool expected)=>Assert.Equal(expected,SsoProviderValidator.IsSafeReturnPath(path));
}

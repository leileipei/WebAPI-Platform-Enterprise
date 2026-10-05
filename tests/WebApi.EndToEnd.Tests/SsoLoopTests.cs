using System.Text.Json;
using Xunit;
namespace WebApi.EndToEnd.Tests;
public sealed class SsoLoopTests
{
    [Fact,Trait("Scenario","Sso")]
    public async Task RealOidcLoginAndRevocationSurviveRestartWithoutChangingLocalAdministrator()
    {
        var directory=System.Environment.GetEnvironmentVariable("WEBAPI_SSO_RESULT_DIRECTORY");Assert.NotNull(directory);
        var result=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory!,"result.json"))).RootElement;
        Assert.Equal("real-isolated-keycloak",result.GetProperty("classification").GetString());
        foreach(var name in new[]{"real-login","unknown-subject","wrong-secret","local-admin","cross-site-cookies","restart-replay","two-control-planes","provider-revocation","secret-rotation","interrupted-attempt"})
        {var check=result.GetProperty("checks").EnumerateArray().Single(c=>c.GetProperty("name").GetString()==name);Assert.True(check.GetProperty("pass").GetBoolean(),name);Assert.True(check.GetProperty("evidence").EnumerateObject().Any());}
        Assert.Equal("localhost",result.GetProperty("sameSiteEvidence").GetProperty("idpHost").GetString());Assert.Equal("127.0.0.1",result.GetProperty("sameSiteEvidence").GetProperty("platformHost").GetString());
    }
}

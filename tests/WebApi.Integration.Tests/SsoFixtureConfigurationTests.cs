using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebApi.ControlPlane;
using WebApi.Infrastructure.Sso;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoFixtureConfigurationTests
{
    [Fact]
    public async Task ExactFixtureOriginIsConfigurableWithoutSplittingUrlColons()
    {
        await using var app=ControlPlaneApp.Build(["--environment","Development"],builder=>builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{
            ["ConnectionStrings:WebApi"]="Host=unused;Database=unused;Username=unused;Password=synthetic",
            ["Sso:FixtureEnabled"]="true",["Sso:AllowedOrigins:0"]="http://localhost:45001",
            ["Sso:FixtureConnectOverridesJson"]="{\"http://localhost:45001\":\"keycloak\"}"
        }));
        var options=app.Services.GetRequiredService<IOptions<SsoOptions>>().Value;
        Assert.True(options.FixtureConnectOverrides.TryGetValue("http://localhost:45001",out var host));Assert.Equal("keycloak",host);
    }
}

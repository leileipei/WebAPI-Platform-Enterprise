using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.ControlPlane;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PersistentSessionTests
{
    [Fact] public async Task ConfiguredCookieNamesPreserveLegacySessionNamespace()
    {
        await using var api=new ApiFixture();await api.InitializeAsync(b=>{b.Configuration["Authentication:CookieName"]="WebApi.Local.Test.Session";b.Configuration["Antiforgery:CookieName"]="WebApi.Local.Test.Csrf";});
        using var csrf=await api.Client.GetAsync("/api/v1/auth/csrf");Assert.Contains(csrf.Headers.GetValues("Set-Cookie"),x=>x.StartsWith("WebApi.Local.Test.Csrf=",StringComparison.Ordinal));
        using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();Assert.Contains(login.Headers.GetValues("Set-Cookie"),x=>x.StartsWith("WebApi.Local.Test.Session=",StringComparison.Ordinal));Assert.DoesNotContain(login.Headers.GetValues("Set-Cookie"),x=>x.StartsWith("WebApi.Session=",StringComparison.Ordinal));
        using var me=await api.Client.GetAsync("/api/v1/auth/me");Assert.Equal(HttpStatusCode.OK,me.StatusCode);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task PersistedCookieHonorsApplicationIsolation(bool differentApplication)
    {
        var keys=Path.Combine(Path.GetTempPath(),"runtime-keys-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(keys);
        try
        {
            await using var api=new ApiFixture();await api.InitializeAsync(b=>{b.Configuration["DataProtection:KeysDirectory"]=keys;b.Configuration["DataProtection:ApplicationName"]="runtime-test";});
            using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();var cookie=login.Headers.GetValues("Set-Cookie").Single(x=>x.StartsWith("WebApi.Session=",StringComparison.Ordinal)).Split(';')[0];
            Assert.NotEmpty(Directory.GetFiles(keys,"*.xml"));await api.StopServerAsync();
            await using var next=ControlPlaneApp.Build(["--environment","Development"],b=>{b.WebHost.UseUrls("http://127.0.0.1:0");b.Logging.ClearProviders();b.Configuration["ConnectionStrings:WebApi"]=api.Database.ConnectionString;LoginProtectionTestConfiguration.Apply(b,api.LoginProtectionDirectory,api.LoginProtectionDeploymentId);b.Configuration["DataProtection:KeysDirectory"]=keys;b.Configuration["DataProtection:ApplicationName"]=differentApplication?"another-runtime":"runtime-test";});
            await next.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(next.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};client.DefaultRequestHeaders.Add("Cookie",cookie);
            using var me=await client.GetAsync("/api/v1/auth/me");Assert.Equal(differentApplication?HttpStatusCode.Unauthorized:HttpStatusCode.OK,me.StatusCode);
            if(!differentApplication){using var csrfMissing=await client.PostAsync("/api/v1/auth/logout",null);Assert.Equal(HttpStatusCode.Forbidden,csrfMissing.StatusCode);using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/logout");request.Headers.Add("Origin","https://foreign.invalid");using var foreign=await client.SendAsync(request);Assert.Equal(HttpStatusCode.Forbidden,foreign.StatusCode);}
            await next.StopAsync();
        }
        finally{Directory.Delete(keys,true);}
    }
    [Fact] public async Task ExplicitEmptyAllowlistRejectsTestBackend()
    {
        await using var api=new ApiFixture();await api.InitializeAsync(b=>b.Configuration["Upstream:RequireExplicitAllowedOrigins"]="true");await api.SeedCatalogAsync();using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();
        using var result=await api.WriteAsync(HttpMethod.Post,$"/api/v1/clusters/{api.Cluster.Id}/destinations",new{name="forbidden-test-default",address="http://test-backend:8080/",weight=1,enabled=true});Assert.Equal(HttpStatusCode.UnprocessableEntity,result.StatusCode);
    }
}

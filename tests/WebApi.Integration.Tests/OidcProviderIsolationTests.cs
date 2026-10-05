using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.ControlPlane.Sso;
using WebApi.ControlPlane;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Sso;
using WebApi.Infrastructure.Sso;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class OidcProviderIsolationTests
{
    private static async Task<Uri> Start(OidcProtocolFixture fixture,Guid provider,HttpClient browser)
    {
        var csrf=(await browser.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf"))!;
        using var response=await browser.PostAsync($"/api/v1/auth/sso/{provider}/start",new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",csrf["token"]}}));
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);return response.Headers.Location!;
    }
    [Fact] public async Task ParallelProvidersNeverShareOptionsAndEvictedProviderCanRebuild()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();var other=await fixture.AddProviderAsync("other-client");
        using(var scope=fixture.Api.Services())scope.ServiceProvider.GetRequiredService<IOptions<SsoOptions>>().Value.MaxCachedSchemes=1;
        using var second=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=fixture.Browser.BaseAddress};
        var firstLocation=await Start(fixture,fixture.Provider.Id,fixture.Browser);var secondLocation=await Start(fixture,other.Id,second);
        Assert.Contains("client_id=webapi-console",firstLocation.Query);Assert.Contains("client_id=other-client",secondLocation.Query);
        using var authorization=await fixture.Browser.GetAsync(firstLocation);using var completed=await fixture.Browser.GetAsync(authorization.Headers.Location);
        Assert.Equal(HttpStatusCode.SeeOther,completed.StatusCode);Assert.StartsWith("/auth/sso/complete",completed.Headers.Location!.ToString());
        using(var scope=fixture.Api.Services())scope.ServiceProvider.GetRequiredService<IOptions<SsoOptions>>().Value.MaxCachedSchemes=2;
        using var a=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=fixture.Browser.BaseAddress};
        using var b=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=fixture.Browser.BaseAddress};
        var parallel=await Task.WhenAll(Start(fixture,fixture.Provider.Id,a),Start(fixture,other.Id,b));
        Assert.Contains("client_id=webapi-console",parallel[0].Query);Assert.Contains("client_id=other-client",parallel[1].Query);
    }
    [Fact] public async Task ActiveHandlerIsLeasedAndCapacityExhaustionFailsClosed()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();var other=await fixture.AddProviderAsync("other-client");
        using(var scope=fixture.Api.Services())scope.ServiceProvider.GetRequiredService<IOptions<SsoOptions>>().Value.MaxCachedSchemes=1;
        fixture.Identity.DelayToken=true;var callback=await fixture.CallbackAsync();var completing=fixture.Browser.GetAsync(callback);
        await fixture.Identity.TokenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var browser=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=fixture.Browser.BaseAddress};
            var csrf=(await browser.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf"))!;
            using var response=await browser.PostAsync($"/api/v1/auth/sso/{other.Id}/start",new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",csrf["token"]}}));
            Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        }
        finally{fixture.Identity.TokenRelease.TrySetResult();}
        using var completed=await completing;Assert.StartsWith("/auth/sso/complete",completed.Headers.Location!.ToString());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SecretReplacementRequiresExplicitRevisionEvenAfterCacheEviction(bool evict)
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();
        var other=await fixture.AddProviderAsync("other-client");
        using(var scope=fixture.Api.Services())scope.ServiceProvider.GetRequiredService<IOptions<SsoOptions>>().Value.MaxCachedSchemes=1;
        using(var started=await fixture.StartAsync())Assert.Equal(HttpStatusCode.Redirect,started.StatusCode);
        if(evict){using var browser=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=fixture.Browser.BaseAddress};await Start(fixture,other.Id,browser);}
        await File.WriteAllTextAsync(fixture.SecretPath,"replacement-without-rotation");
        using(var denied=await fixture.StartAsync())Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        // An explicit rotation pins the new file contents in a new auth_revision.
        using var rotated=await fixture.Api.WriteAsync(HttpMethod.Post,$"/api/v1/settings/sso/providers/{fixture.Provider.Id}/rotate",new{},RevisionTag.Format(fixture.Provider.Revision));rotated.EnsureSuccessStatusCode();
        using var permitted=await fixture.StartAsync();Assert.Equal(HttpStatusCode.Redirect,permitted.StatusCode);
        await using var db=fixture.Api.Context();var row=await db.Set<SsoProvider>().SingleAsync(x=>x.Id==fixture.Provider.Id);
        var raw=System.Text.Json.JsonSerializer.Serialize(row);
        Assert.DoesNotContain("replacement-without-rotation",raw);
        Assert.DoesNotContain(Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("replacement-without-rotation"))),raw);
        Assert.DoesNotContain(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("replacement-without-rotation"))),raw);
    }
    [Fact] public async Task ConcurrentDuplicateCannotFailTheCallbackThatOwnsProcessing()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();fixture.Identity.DelayToken=true;
        var callback=await fixture.CallbackAsync();var winning=fixture.Browser.GetAsync(callback);
        await fixture.Identity.TokenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var duplicate=await fixture.Browser.GetAsync(callback);
            Assert.Equal(HttpStatusCode.SeeOther,duplicate.StatusCode);Assert.StartsWith("/login?ssoError=",duplicate.Headers.Location!.ToString());
            await using var db=fixture.Api.Context();Assert.Equal(1,await db.Set<SsoLoginAttempt>().CountAsync(x=>x.State=="Processing"));
        }
        finally{fixture.Identity.TokenRelease.TrySetResult();}
        using var completed=await winning;Assert.StartsWith("/auth/sso/complete",completed.Headers.Location!.ToString());
        Assert.Equal(1,fixture.Identity.Redemptions);
        await using var final=fixture.Api.Context();Assert.Equal(1,await final.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.login"));
        Assert.Equal(1,await final.Set<SsoLoginAttempt>().CountAsync(x=>x.State=="Succeeded"));
        Assert.Equal(1,await final.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.failed"));
    }
    [Fact] public async Task NewControlPlaneCannotAdoptReplacedSecretAtTheSameRevision()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();await File.WriteAllTextAsync(fixture.SecretPath,"new-instance-unrotated-secret");
        await using var second=ControlPlaneApp.Build(["--environment","Development"],builder=>{
            builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();builder.Configuration["ConnectionStrings:WebApi"]=fixture.Api.Database.ConnectionString;
            builder.Configuration["DataProtection:KeysDirectory"]=fixture.DataProtectionDirectory;builder.Configuration["DataProtection:ApplicationName"]=fixture.DataProtectionApplication;
            builder.Services.Configure<SsoOptions>(options=>{options.FixtureEnabled=true;options.AllowedOrigins=[fixture.Identity.Origin];options.SecretFiles=new(){{"enterprise",fixture.SecretPath}};options.PublicBaseUrl=fixture.Api.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);});
        });await second.StartAsync();var address=second.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var browser=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=new Uri(address)};
        var csrf=(await browser.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf"))!;
        using var denied=await browser.PostAsync($"/api/v1/auth/sso/{fixture.Provider.Id}/start",new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",csrf["token"]}}));
        Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);await second.StopAsync();
    }
}

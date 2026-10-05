using System.Net;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.ControlPlane;
using WebApi.Infrastructure.Sso;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoSessionRevocationTests
{
    private static async Task Login(OidcProtocolFixture fixture)
    {
        var callback=await fixture.CallbackAsync();using var completed=await fixture.Browser.GetAsync(callback);Assert.Equal(HttpStatusCode.SeeOther,completed.StatusCode);
        Assert.StartsWith("/auth/sso/complete",completed.Headers.Location!.ToString());Assert.Equal(HttpStatusCode.OK,(await fixture.Browser.GetAsync("/api/v1/auth/me")).StatusCode);
    }
    [Fact] public async Task DisableAndReenableNeverReviveCookie()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();await Login(fixture);var provider=fixture.Provider;
        (await fixture.Api.WriteAsync(HttpMethod.Post,$"/api/v1/settings/sso/providers/{provider.Id}/disable",new{},RevisionTag.Format(provider.Revision))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized,(await fixture.Browser.GetAsync("/api/v1/auth/me")).StatusCode);
        (await fixture.Api.WriteAsync(HttpMethod.Post,$"/api/v1/settings/sso/providers/{provider.Id}/test",new{},RevisionTag.Format(provider.Revision+1))).EnsureSuccessStatusCode();
        (await fixture.Api.WriteAsync(HttpMethod.Post,$"/api/v1/settings/sso/providers/{provider.Id}/enable",new{},RevisionTag.Format(provider.Revision+1))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized,(await fixture.Browser.GetAsync("/api/v1/auth/me")).StatusCode);
    }
    [Theory][InlineData("name",false)][InlineData("default",false)][InlineData("secret",true)][InlineData("rotate",true)]
    public async Task AuthRevisionRevokesButNameAndDefaultDoNot(string operation,bool revoked)
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();await Login(fixture);var provider=fixture.Provider;
        if(operation is "default" or "rotate")(await fixture.Api.WriteAsync(HttpMethod.Post,$"/api/v1/settings/sso/providers/{provider.Id}/{operation}",new{},RevisionTag.Format(provider.Revision))).EnsureSuccessStatusCode();
        else{if(operation=="secret"){using var configured=fixture.Api.Services();configured.ServiceProvider.GetRequiredService<IOptions<SsoOptions>>().Value.SecretFiles["new-version"]=fixture.SecretPath;}var body=SsoFixture.Body(operation=="name"?"改名":provider.Name,issuer:provider.Issuer) with{SecretRef=operation=="secret"?"file://sso/new-version":provider.SecretRef};(await fixture.Api.WriteAsync(HttpMethod.Put,$"/api/v1/settings/sso/providers/{provider.Id}",body,RevisionTag.Format(provider.Revision))).EnsureSuccessStatusCode();}
        Assert.Equal(revoked?HttpStatusCode.Unauthorized:HttpStatusCode.OK,(await fixture.Browser.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await fixture.Api.Client.GetAsync("/api/v1/auth/me")).StatusCode);
    }
    [Theory][InlineData("user")][InlineData("binding")]
    public async Task UserAndBindingRevocationAreReadFromDatabase(string kind)
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();await Login(fixture);
        await using var db=fixture.Api.Context();var identity=await db.Set<UserExternalIdentity>().SingleAsync();
        if(kind=="binding")await db.Set<UserExternalIdentity>().Where(x=>x.Id==identity.Id).ExecuteUpdateAsync(update=>update.SetProperty(x=>x.Enabled,false));
        else{var user=await db.Set<UserRecord>().SingleAsync(x=>x.Id==identity.UserId);(await fixture.Api.WriteAsync(HttpMethod.Put,$"/api/v1/users/{user.Id}",new UpdateUserRequest(user.DisplayName,"Disabled",user.Email),RevisionTag.Format(user.Revision))).EnsureSuccessStatusCode();}
        Assert.Equal(HttpStatusCode.Unauthorized,(await fixture.Browser.GetAsync("/api/v1/auth/me")).StatusCode);
    }
    [Fact] public async Task SecondControlPlaneSeesRevocationFromDatabaseAndLocalTicketDoesNotSlide()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();await Login(fixture);
        using var localLogin=await fixture.Api.LoginAsync();localLogin.EnsureSuccessStatusCode();var cookie=localLogin.Headers.GetValues("Set-Cookie").Single(value=>value.StartsWith("WebApi.Session=",StringComparison.Ordinal)).Split(';')[0]["WebApi.Session=".Length..];
        using var services=fixture.Api.Services();var format=services.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies").TicketDataFormat;var expiry=format.Unprotect(cookie)!.Properties.ExpiresUtc;
        await using var second=ControlPlaneApp.Build(["--environment","Development"],builder=>{
            builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();builder.Configuration["ConnectionStrings:WebApi"]=fixture.Api.Database.ConnectionString;
            builder.Configuration["DataProtection:KeysDirectory"]=fixture.DataProtectionDirectory;builder.Configuration["DataProtection:ApplicationName"]=fixture.DataProtectionApplication;
        });await second.StartAsync();var address=second.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Assert.Equal(HttpStatusCode.OK,(await fixture.Browser.GetAsync(address+"/api/v1/auth/me")).StatusCode);
        (await fixture.Api.WriteAsync(HttpMethod.Post,$"/api/v1/settings/sso/providers/{fixture.Provider.Id}/rotate",new{},RevisionTag.Format(fixture.Provider.Revision))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized,(await fixture.Browser.GetAsync(address+"/api/v1/auth/me")).StatusCode);
        using var localMe=await fixture.Api.Client.GetAsync("/api/v1/auth/me");localMe.EnsureSuccessStatusCode();Assert.False(localMe.Headers.Contains("Set-Cookie"));
        Assert.Equal(expiry,format.Unprotect(cookie)!.Properties.ExpiresUtc);await second.StopAsync();
    }
}

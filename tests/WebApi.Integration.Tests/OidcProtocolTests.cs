using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Sso;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class OidcProtocolTests
{
    private static bool HasSession(HttpResponseMessage response)=>response.Headers.TryGetValues("Set-Cookie",out var values)&&values.Any(value=>value.StartsWith("WebApi.Session=",StringComparison.Ordinal));
    [Fact] public async Task IdentityProviderCancellationIsAuditedAndAttemptIsFailed()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();fixture.Identity.Failure="cancelled";
        var callback=await fixture.CallbackAsync();using var response=await fixture.Browser.GetAsync(callback);
        Assert.Equal(HttpStatusCode.SeeOther,response.StatusCode);Assert.False(HasSession(response));Assert.Contains("ssoError=cancelled",response.Headers.Location!.ToString());Assert.Equal(0,fixture.Identity.Redemptions);
        await using var db=fixture.Api.Context();Assert.Equal(1,await db.Set<SsoLoginAttempt>().CountAsync(x=>x.State=="Failed"&&x.FailureCode=="cancelled"));
        Assert.Equal(1,await db.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.failed"));
    }
    [Fact] public async Task UnknownBindingReturnsSanitizedLoginRedirectAndMarksAttemptFailed()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();
        await using(var setup=fixture.Api.Context())await setup.Set<UserExternalIdentity>().Where(x=>x.ProviderId==fixture.Provider.Id).ExecuteDeleteAsync();
        var callback=await fixture.CallbackAsync();using var response=await fixture.Browser.GetAsync(callback);
        Assert.Equal(HttpStatusCode.SeeOther,response.StatusCode);Assert.False(HasSession(response));Assert.StartsWith("/login?ssoError=",response.Headers.Location!.ToString());
        await using var db=fixture.Api.Context();Assert.Equal(0,await db.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.login"));
        Assert.Equal(1,await db.Set<SsoLoginAttempt>().CountAsync(x=>x.State=="Failed"));Assert.Equal(1,await db.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.failed"));
    }
    [Theory][InlineData("state")][InlineData("nonce")][InlineData("signature")][InlineData("none")][InlineData("HS256")][InlineData("issuer")][InlineData("aud")][InlineData("azp")][InlineData("expired")][InlineData("jku")][InlineData("x5u")][InlineData("mappingType")][InlineData("unknownKid")]
    public async Task CallbackRejectsInvalidProtocolBeforeCookie(string failure)
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();fixture.Identity.Failure=failure;var callback=await fixture.CallbackAsync();var baseline=fixture.Identity.JwksRequests;
        using var response=await fixture.Browser.GetAsync(callback);Assert.False(HasSession(response));Assert.Equal(HttpStatusCode.SeeOther,response.StatusCode);Assert.Contains("ssoError=",response.Headers.Location!.ToString());
        await using var db=fixture.Api.Context();Assert.Equal(0,await db.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.login"));
        if(failure=="state")Assert.Equal(0,fixture.Identity.Redemptions);
        if(failure is "jku" or "x5u")Assert.Equal(baseline,fixture.Identity.JwksRequests);
        if(failure=="unknownKid")Assert.Equal(baseline+1,fixture.Identity.JwksRequests);
        Assert.Equal(HttpStatusCode.Unauthorized,(await fixture.Browser.GetAsync("/api/v1/auth/me")).StatusCode);
    }
    [Fact] public async Task ValidFlowUsesPkceAndOnlyPlatformCookieAndReplayDoesNotRedeemAgain()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();var callback=await fixture.CallbackAsync();
        using var response=await fixture.Browser.GetAsync(callback);Assert.Equal(HttpStatusCode.SeeOther,response.StatusCode);Assert.True(HasSession(response));Assert.StartsWith("/auth/sso/complete",response.Headers.Location!.ToString());
        Assert.True(fixture.Identity.PkceValidated);Assert.True(fixture.Identity.ClientSecretPostValidated);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),cookie=>cookie.StartsWith("WebApi.Session=",StringComparison.Ordinal)&&cookie.Contains("httponly")&&cookie.Contains("samesite=strict"));
        Assert.Equal(HttpStatusCode.OK,(await fixture.Browser.GetAsync("/api/v1/auth/me")).StatusCode);
        using var replay=await fixture.Browser.GetAsync(callback);Assert.False(HasSession(replay));Assert.Equal(1,fixture.Identity.Redemptions);
        await using var db=fixture.Api.Context();Assert.Equal(1,await db.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.login"));
    }
    [Fact] public async Task StartRequiresOriginAndFormCsrfAndRejectsAccountSwitchAndUnsafePath()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();
        Assert.Equal(HttpStatusCode.Forbidden,(await fixture.Browser.PostAsync($"/api/v1/auth/sso/{fixture.Provider.Id}/start",new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode);
        using var cross=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/auth/sso/{fixture.Provider.Id}/start"){Content=new FormUrlEncodedContent(new Dictionary<string,string>())};cross.Headers.Add("Origin","https://other.example");
        Assert.Equal(HttpStatusCode.Forbidden,(await fixture.Browser.SendAsync(cross)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await fixture.StartAsync("//other.example/")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await fixture.StartAsync(browser:fixture.Api.Client)).StatusCode);
        using var start=await fixture.StartAsync();Assert.Equal(HttpStatusCode.Redirect,start.StatusCode);
        Assert.All(start.Headers.GetValues("Set-Cookie"),cookie=>{Assert.Contains("httponly",cookie);Assert.Contains("samesite=lax",cookie);Assert.Contains("/auth/oidc/callback/",cookie);});
    }
    [Fact] public async Task WrongProviderOrMissingCorrelationNeverRedeemsAndPostCallbackIsRejected()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();var other=await fixture.AddProviderAsync("other-client");var callback=await fixture.CallbackAsync();
        using var wrong=await fixture.Browser.GetAsync(callback.ToString().Replace(fixture.Provider.Id.ToString(),other.Id.ToString(),StringComparison.Ordinal));Assert.False(HasSession(wrong));
        using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=fixture.Browser.BaseAddress};
        using var missing=await client.GetAsync(callback);Assert.False(HasSession(missing));Assert.Equal(0,fixture.Identity.Redemptions);
        using var post=await fixture.Browser.PostAsync(callback.AbsolutePath,new FormUrlEncodedContent(new Dictionary<string,string>()));Assert.Equal(HttpStatusCode.MethodNotAllowed,post.StatusCode);
    }
    [Fact] public async Task RotatedPublicKeyCanRefreshOnceAndComplete()
    {
        await using var fixture=new OidcProtocolFixture();await fixture.InitializeAsync();var callback=await fixture.CallbackAsync();var baseline=fixture.Identity.JwksRequests;
        fixture.Identity.Failure="rotation";fixture.Identity.PublicKid="rotated";
        using var response=await fixture.Browser.GetAsync(callback);Assert.True(HasSession(response));Assert.Equal(baseline+1,fixture.Identity.JwksRequests);
    }
    [Fact] public async Task ProductionCookieHeadersAreSecureWithoutClaimingTlsTransportAcceptance()
    {
        using var rsa=RSA.Create(2048);var certificateRequest=new CertificateRequest("CN=SSO TLS fixture",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var names=new SubjectAlternativeNameBuilder();names.AddIpAddress(IPAddress.Loopback);certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate=certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        await using var fixture=new ApiFixture();var metadata=new SsoFixture.Network();
        await fixture.InitializeAsync(builder=>{
            builder.WebHost.ConfigureKestrel(server=>server.Listen(IPAddress.Loopback,0,listen=>listen.UseHttps(certificate)));
            builder.Environment.EnvironmentName="Production";builder.Services.AddSingleton<IOidcMetadataClient>(metadata);builder.Services.AddSingleton<ISsoSecretResolver>(metadata);
            builder.Services.Configure<SsoOptions>(options=>options.PublicBaseUrl="https://platform.example.test");
        });
        await fixture.SeedScopeAsync(platformAdmin:true);await SsoFixture.PromoteAsync(fixture);
        var provider=new SsoProvider{Name="Synthetic",Issuer="https://id.example.test",ClientId="synthetic",SecretRef="file://sso/enterprise",Enabled=true};
        using(var scope=fixture.Services())scope.ServiceProvider.GetRequiredService<SsoSecretVersion>().Pin(provider,"synthetic-test-secret");
        await using(var db=fixture.Context()){db.Add(provider);await db.SaveChangesAsync();}
        // Trust exactly this synthetic leaf certificate. This is fixture TLS, not production PKI acceptance.
        using var client=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false,ServerCertificateCustomValidationCallback=(_,cert,_,errors)=>
            cert?.GetCertHashString()==certificate.GetCertHashString()&&(errors&~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)==0}){BaseAddress=fixture.Client.BaseAddress};
        using var csrf=await client.GetAsync("/api/v1/auth/csrf");csrf.EnsureSuccessStatusCode();var token=(await csrf.Content.ReadFromJsonAsync<Dictionary<string,string>>())!["token"];
        using var start=new HttpRequestMessage(HttpMethod.Post,$"/api/v1/auth/sso/{provider.Id}/start"){Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",token}})};
        using var response=await client.SendAsync(start);Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);
        Assert.All(response.Headers.GetValues("Set-Cookie"),cookie=>{Assert.Contains("secure",cookie);Assert.Contains("httponly",cookie);Assert.Contains("samesite=lax",cookie);});
        using var login=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/login"){Content=JsonContent.Create(new{username=fixture.User.Username,password=fixture.Password})};login.Headers.Add("X-CSRF-Token",token);
        using var signed=await client.SendAsync(login);signed.EnsureSuccessStatusCode();
        Assert.Contains(signed.Headers.GetValues("Set-Cookie"),cookie=>cookie.StartsWith("WebApi.Session=",StringComparison.Ordinal)&&cookie.Contains("secure")&&cookie.Contains("samesite=strict"));
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebApi.Contracts.Common;
using WebApi.Contracts.Sso;
using WebApi.Infrastructure.Sso;
namespace WebApi.Integration.Tests.Support;
public sealed class OidcProtocolFixture:IAsyncDisposable
{
    public ApiFixture Api {get;}=new();
    public OidcTestServer Identity {get;}=new();
    private DirectoryInfo directory=null!;
    public HttpClient Browser {get;private set;}=null!;
    public SsoProviderDto Provider {get;private set;}=null!;
    public string SecretPath=>Path.Combine(directory.FullName,"client-secret");
    public string DataProtectionDirectory=>Path.Combine(directory.FullName,"keys");
    public string DataProtectionApplication {get;}="oidc-fixture-"+Guid.NewGuid().ToString("N");
    public async Task InitializeAsync()
    {
        await Identity.InitializeAsync();directory=Directory.CreateTempSubdirectory("oidc-fixture-");var path=Path.Combine(directory.FullName,"client-secret");
        await File.WriteAllTextAsync(path,Identity.Secret);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        await Api.InitializeAsync(builder=>{
            builder.Configuration["DataProtection:KeysDirectory"]=DataProtectionDirectory;builder.Configuration["DataProtection:ApplicationName"]=DataProtectionApplication;
            builder.Services.Configure<SsoOptions>(options=>{options.FixtureEnabled=true;options.AllowedOrigins=[Identity.Origin];options.SecretFiles=new(){{"enterprise",path}};});
        });
        using(var scope=Api.Services())scope.ServiceProvider.GetRequiredService<IOptions<SsoOptions>>().Value.PublicBaseUrl=Api.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        await Api.SeedScopeAsync(platformAdmin:true);await SsoFixture.PromoteAsync(Api);(await Api.LoginAsync()).EnsureSuccessStatusCode();Provider=await AddProviderAsync();
        using var user=await Api.WriteAsync(HttpMethod.Post,"/api/v1/users/sso",new CreateSsoUserRequest("synthetic","预开通用户",null,Provider.Id,"SyntheticSubject"));user.EnsureSuccessStatusCode();
        Browser=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=Api.Client.BaseAddress};
    }
    public async Task<SsoProviderDto> AddProviderAsync(string clientId="webapi-console")
    {
        var body=SsoFixture.Body(issuer:Identity.Issuer) with{ClientId=clientId};
        using var response=await Api.WriteAsync(HttpMethod.Post,"/api/v1/settings/sso/providers",body);response.EnsureSuccessStatusCode();var provider=(await response.Content.ReadFromJsonAsync<SsoProviderDto>())!;
        using var test=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/settings/sso/providers/{provider.Id}/test",new{},RevisionTag.Format(provider.Revision));test.EnsureSuccessStatusCode();
        using var enabled=await Api.WriteAsync(HttpMethod.Post,$"/api/v1/settings/sso/providers/{provider.Id}/enable",new{},RevisionTag.Format(provider.Revision));enabled.EnsureSuccessStatusCode();
        return (await enabled.Content.ReadFromJsonAsync<SsoProviderDto>())!;
    }
    public async Task<HttpResponseMessage> StartAsync(string path="/apis",HttpClient? browser=null)
    {
        var client=browser??Browser;var csrf=(await client.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf"))!;
        return await client.PostAsync($"/api/v1/auth/sso/{Provider.Id}/start",new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",csrf["token"]},{"returnPath",path}}));
    }
    public async Task<Uri> CallbackAsync()
    {
        using var start=await StartAsync();start.EnsureSuccessStatusCodeIfNotRedirect();using var authorization=await Browser.GetAsync(start.Headers.Location);
        if(authorization.Headers.Location is null)throw new InvalidOperationException("Fixture authorization failed.");
        return authorization.Headers.Location;
    }
    public async ValueTask DisposeAsync(){Browser?.Dispose();await Api.DisposeAsync();await Identity.DisposeAsync();directory?.Delete(true);}
}
internal static class ProtocolResponseChecks
{
    internal static void EnsureSuccessStatusCodeIfNotRedirect(this HttpResponseMessage response){if(response.StatusCode!=HttpStatusCode.Redirect)response.EnsureSuccessStatusCode();}
}

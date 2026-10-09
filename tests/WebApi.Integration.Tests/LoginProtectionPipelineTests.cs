using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.ControlPlane;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
namespace WebApi.Integration.Tests;
public sealed class LoginProtectionPipelineTests
{
    private sealed class CountingHasher:IPasswordHasher<UserRecord>
    {
        private readonly PasswordHasher<UserRecord> inner=new();public int Count;
        public string HashPassword(UserRecord user,string password)=>inner.HashPassword(user,password);
        public PasswordVerificationResult VerifyHashedPassword(UserRecord user,string hashed,string provided){Interlocked.Increment(ref Count);return inner.VerifyHashedPassword(user,hashed,provided);}
    }
    private sealed class UnavailableStore:ILoginRateStore
    {public ValueTask<LoginRateDecision> TryAcquireAsync(LoginRateRequest request,CancellationToken ct)=>ValueTask.FromResult(new LoginRateDecision(LoginRateDecisionKind.Unavailable));}
    private sealed class FailingWriter:IAuthenticationAuditWriter
    {public Task WriteAsync(AuthenticationAuditEvent entry,CancellationToken ct)=>throw new IOException("CANARY-AUDIT-SECRET");}
    private static async Task<HttpResponseMessage> Login(HttpClient client,string user,string password,string ip="192.0.2.1")
    {
        var csrf=await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/login"){Content=JsonContent.Create(new{username=user,password})};request.Headers.Add("X-CSRF-Token",csrf.GetProperty("token").GetString());request.Headers.Add("X-Forwarded-For",ip);return await client.SendAsync(request);
    }
    [Fact] public async Task TwoHostsShareAccountBudgetSuccessDoesNotResetAndDenialDoesNotHash()
    {
        var hasher=new CountingHasher();await using var f=new ApiFixture();await f.InitializeAsync(b=>{b.Configuration["HttpSecurity:TrustedProxyIps:0"]="127.0.0.1";b.Services.AddSingleton<IPasswordHasher<UserRecord>>(hasher);});
        await using var second=ControlPlaneApp.Build(["--environment","Development"],b=>{b.WebHost.UseUrls("http://127.0.0.1:0");b.Logging.ClearProviders();b.Configuration["ConnectionStrings:WebApi"]=f.Database.ConnectionString;b.Configuration["HttpSecurity:TrustedProxyIps:0"]="127.0.0.1";LoginProtectionTestConfiguration.Apply(b,f.LoginProtectionDirectory,f.LoginProtectionDeploymentId);b.Services.AddSingleton<IPasswordHasher<UserRecord>>(hasher);});
        await second.StartAsync();using var client=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=new Uri(second.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
        for(var i=0;i<10;i++){using var response=await Login(i%2==0?f.Client:client,f.User.Username,i==4?f.Password:"wrong-password",$"192.0.2.{i+1}");Assert.Equal(i==4?HttpStatusCode.OK:HttpStatusCode.Unauthorized,response.StatusCode);}
        var before=hasher.Count;Assert.True(before>0);using var limited=await Login(client,f.User.Username,f.Password,"198.51.100.1");Assert.Equal(HttpStatusCode.TooManyRequests,limited.StatusCode);Assert.Equal(before,hasher.Count);Assert.True(limited.Headers.RetryAfter!.Delta>TimeSpan.Zero);Assert.DoesNotContain(limited.Headers.TryGetValues("Set-Cookie",out var values)?values:[],x=>x.StartsWith("WebApi.Session="));Assert.Contains("no-store",limited.Headers.CacheControl!.ToString());
    }
    [Fact] public async Task RedisUnavailableSkipsPasswordHash()
    {
        var hasher=new CountingHasher();await using var f=new ApiFixture();await f.InitializeAsync(b=>{b.Services.AddSingleton<IPasswordHasher<UserRecord>>(hasher);b.Services.AddSingleton<ILoginRateStore>(new UnavailableStore());});
        using var response=await f.LoginAsync();Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal(0,hasher.Count);Assert.Contains("login_protection_unavailable",await response.Content.ReadAsStringAsync());
    }
    [Fact] public async Task LogoutClearsCookieEvenWhenAuditFails()
    {
        bool broken=false;await using var f=new ApiFixture();await f.InitializeAsync(b=>b.Services.AddScoped<IAuthenticationAuditWriter>(sp=>broken?new FailingWriter():new AuthenticationAuditWriter(sp.GetRequiredService<WebApi.Infrastructure.Persistence.WebApiDbContext>())));
        using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();broken=true;
        using var logout=await f.WriteAsync(HttpMethod.Post,"/api/v1/auth/logout");Assert.Equal(HttpStatusCode.NoContent,logout.StatusCode);Assert.Contains(logout.Headers.GetValues("Set-Cookie"),value=>value.StartsWith("WebApi.Session=;",StringComparison.Ordinal));
        using var me=await f.Client.GetAsync("/api/v1/auth/me");Assert.Equal(HttpStatusCode.Unauthorized,me.StatusCode);
    }
    [Fact] public async Task ExistingCookieStillWorksDuringRealRedisConnectionFailure()
    {
        bool unavailable=false;using var offline=new RedisLoginRateStore("127.0.0.1:1,abortConnect=false,connectTimeout=100",Guid.NewGuid().ToString("N"));
        await using var f=new ApiFixture();await f.InitializeAsync(b=>b.Services.AddSingleton<ILoginRateStore>(sp=>new SwitchStore(sp.GetRequiredService<LoginProtectionDeploymentSettings>(),offline,()=>unavailable)));
        using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();unavailable=true;
        using var denied=await f.LoginAsync();Assert.Equal(HttpStatusCode.ServiceUnavailable,denied.StatusCode);
        using var me=await f.Client.GetAsync("/api/v1/auth/me");Assert.Equal(HttpStatusCode.OK,me.StatusCode);
    }
    private sealed class SwitchStore(LoginProtectionDeploymentSettings settings,RedisLoginRateStore offline,Func<bool> unavailable):ILoginRateStore,IDisposable
    {
        private readonly RedisLoginRateStore live=new(settings.RedisConnection,settings.RedisPrefix);
        public ValueTask<LoginRateDecision> TryAcquireAsync(LoginRateRequest request,CancellationToken ct)=>(unavailable()?offline:live).TryAcquireAsync(request,ct);
        public void Dispose()=>live.Dispose();
    }
    [Fact] public async Task AuditFailureCannotIssueCookieOrExposeException()
    {
        await using var f=new ApiFixture();await f.InitializeAsync(b=>b.Services.AddScoped<IAuthenticationAuditWriter>(_=>new FailingWriter()));
        foreach(var password in new[]{"wrong-password",f.Password}){using var response=await f.LoginAsync(password);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var body=await response.Content.ReadAsStringAsync();Assert.Contains("authentication_audit_unavailable",body);Assert.DoesNotContain("CANARY",body);Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie",out var cookies)?cookies:[],x=>x.StartsWith("WebApi.Session="));}
    }
}

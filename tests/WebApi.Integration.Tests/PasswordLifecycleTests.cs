using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Settings;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;

public sealed class PasswordLifecycleTests
{
    private const string Changed="Changed-private-password-123!";
    private static async Task<HttpResponseMessage> WriteAsync(HttpClient client,string path,object body,string? key=null)
    {
        var csrf=await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
        using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(body)};
        request.Headers.Add("X-CSRF-Token",csrf.GetProperty("token").GetString());
        request.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString("N")); return await client.SendAsync(request);
    }
    private static async Task<HttpClient> LoginClientAsync(ApiFixture api)
    {
        var client=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=api.Client.BaseAddress};
        using var response=await WriteAsync(client,"/api/v1/auth/login",new {username=api.User.Username,password=api.Password});
        response.EnsureSuccessStatusCode();return client;
    }
    private static object Change(string current,string next=Changed,string? confirm=null)=>new {currentPassword=current,newPassword=next,confirmPassword=confirm??next};

    [Fact]
    public async Task Successful_change_restores_permissions_and_never_persists_secrets()
    {
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);
        using var client=await LoginClientAsync(api); var started=DateTimeOffset.UtcNow;
        using var response=await WriteAsync(client,"/api/v1/auth/change-password",Change(api.Password));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);var identity=await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(identity.GetProperty("mustChangePassword").GetBoolean());Assert.NotEmpty(identity.GetProperty("permissions").EnumerateArray());
        Assert.False(identity.TryGetProperty("securityStamp",out _));Assert.False(identity.TryGetProperty("passwordHash",out _));
        using var business=await client.GetAsync("/api/v1/organizations");Assert.Equal(HttpStatusCode.OK,business.StatusCode);
        await using var db=api.Context();var after=await db.Set<UserRecord>().AsNoTracking().SingleAsync(x=>x.Id==api.User.Id);
        Assert.False(after.MustChangePassword);Assert.NotNull(after.PasswordChangedAt);Assert.InRange(after.PasswordChangedAt.Value,started,DateTimeOffset.UtcNow);
        Assert.Equal(api.User.Revision+1,after.Revision);Assert.NotEqual(api.User.SecurityStamp,after.SecurityStamp);
        var hasher=new PasswordHasher<UserRecord>();Assert.NotEqual(PasswordVerificationResult.Failed,hasher.VerifyHashedPassword(after,after.PasswordHash!,Changed));
        Assert.Equal(PasswordVerificationResult.Failed,hasher.VerifyHashedPassword(after,after.PasswordHash!,api.Password));
        var audits=await db.Set<AuditLog>().Where(x=>x.Action=="auth.password.change").ToArrayAsync();Assert.Single(audits);
        var text=string.Join('\n',audits.Select(x=>x.BeforeJson+"\n"+x.AfterJson));
        foreach(var secret in new[]{api.Password,Changed,after.PasswordHash!,after.SecurityStamp,api.User.SecurityStamp})Assert.DoesNotContain(secret,text,StringComparison.Ordinal);
        Assert.Contains("MustChangePassword",text);Assert.Contains("PasswordChangedAt",text);
        Assert.Empty(await db.Set<IdempotencyRecord>().ToArrayAsync());Assert.Empty(await db.Set<OutboxMessage>().ToArrayAsync());
    }

    [Fact]
    public async Task Only_one_concurrent_old_stamp_can_change()
    {
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync();
        using var first=await LoginClientAsync(api);using var second=await LoginClientAsync(api);
        var results=await Task.WhenAll(WriteAsync(first,"/api/v1/auth/change-password",Change(api.Password,Changed+"one")),WriteAsync(second,"/api/v1/auth/change-password",Change(api.Password,Changed+"two")));
        try{Assert.Equal(1,results.Count(r=>r.StatusCode==HttpStatusCode.OK));}
        finally{foreach(var result in results)result.Dispose();}
        await using var db=api.Context();Assert.Single(await db.Set<AuditLog>().Where(x=>x.Action=="auth.password.change").ToArrayAsync());
    }

    [Fact]
    public async Task Lost_response_recovers_by_new_login_not_old_cookie_replay()
    {
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync();
        using var changing=await LoginClientAsync(api);using var old=await LoginClientAsync(api);
        using(var result=await WriteAsync(changing,"/api/v1/auth/change-password",Change(api.Password),"same-change-key"))Assert.Equal(HttpStatusCode.OK,result.StatusCode);
        using var replay=await WriteAsync(old,"/api/v1/auth/change-password",Change(api.Password),"same-change-key");Assert.Equal(HttpStatusCode.Unauthorized,replay.StatusCode);
        using var recovered=await WriteAsync(old,"/api/v1/auth/login",new {username=api.User.Username,password=Changed});Assert.Equal(HttpStatusCode.OK,recovered.StatusCode);
        Assert.False((await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
    }

    [Theory]
    [InlineData("too-short",null)] [InlineData("Changed-private-password-123!","wrong-confirm")]
    [InlineData("same-as-current",null)] [InlineData("overlong",null)]
    public async Task Invalid_new_password_does_not_mutate_account(string input,string? confirmation)
    {
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync();using var client=await LoginClientAsync(api);
        var next=input=="same-as-current"?api.Password:input=="overlong"?new string('A',1025):input;
        using var result=await WriteAsync(client,"/api/v1/auth/change-password",Change(api.Password,next,confirmation));Assert.Equal(HttpStatusCode.UnprocessableEntity,result.StatusCode);
        await using var db=api.Context();var after=await db.Set<UserRecord>().AsNoTracking().SingleAsync();Assert.True(after.MustChangePassword);Assert.Equal(api.User.SecurityStamp,after.SecurityStamp);
    }

    [Fact]
    public async Task Password_bytes_are_not_normalized()
    {
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync();using var client=await LoginClientAsync(api);
        const string exact="  Combining-e\u0301-password-123!  ";using var changed=await WriteAsync(client,"/api/v1/auth/change-password",Change(api.Password,exact));Assert.Equal(HttpStatusCode.OK,changed.StatusCode);
        using var trimmed=await WriteAsync(client,"/api/v1/auth/login",new {username=api.User.Username,password=exact.Trim()});Assert.Equal(HttpStatusCode.Unauthorized,trimmed.StatusCode);
        using var composed=await WriteAsync(client,"/api/v1/auth/login",new {username=api.User.Username,password=exact.Normalize(NormalizationForm.FormC)});Assert.Equal(HttpStatusCode.Unauthorized,composed.StatusCode);
        using var raw=await WriteAsync(client,"/api/v1/auth/login",new {username=api.User.Username,password=exact});Assert.Equal(HttpStatusCode.OK,raw.StatusCode);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_password_and_stamp()
    {
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync();using var client=await LoginClientAsync(api);
        await using(var db=api.Context())await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION p4_reject_audit() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN IF NEW.action = ''auth.password.change'' THEN RAISE EXCEPTION ''isolated audit unavailable''; END IF; RETURN NEW; END'; CREATE TRIGGER p4_reject_audit BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION p4_reject_audit()");
        using var result=await WriteAsync(client,"/api/v1/auth/change-password",Change(api.Password));Assert.Equal(HttpStatusCode.InternalServerError,result.StatusCode);
        await using var afterDb=api.Context();var after=await afterDb.Set<UserRecord>().AsNoTracking().SingleAsync();Assert.True(after.MustChangePassword);Assert.Equal(api.User.SecurityStamp,after.SecurityStamp);Assert.Null(after.PasswordChangedAt);
        Assert.NotEqual(PasswordVerificationResult.Failed,new PasswordHasher<UserRecord>().VerifyHashedPassword(after,after.PasswordHash!,api.Password));
        Assert.Empty(await afterDb.Set<AuditLog>().Where(x=>x.Action=="auth.password.change").ToArrayAsync());
    }

    [Fact]
    public async Task Redis_failure_is_closed_without_password_change()
    {
        SwitchableRealRateStore? rates=null;
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync(b=>{
            rates=new SwitchableRealRateStore(b.Configuration["Redis:Connection"]!,b.Configuration["Authentication:LoginProtection:RedisPrefix"]!);
            b.Services.AddSingleton<ILoginRateStore>(rates);
        });using var client=await LoginClientAsync(api);rates!.Fail=true;
        using var result=await WriteAsync(client,"/api/v1/auth/change-password",Change(api.Password));Assert.Equal(HttpStatusCode.ServiceUnavailable,result.StatusCode);
        await using var db=api.Context();Assert.Equal(api.User.SecurityStamp,(await db.Set<UserRecord>().SingleAsync()).SecurityStamp);
    }

    [Fact]
    public async Task Change_limits_are_separate_from_login_and_include_retry_after()
    {
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync();
        await using(var db=api.Context())
        {
            var settings=((SecuritySettings)SettingsValueCodec.Default("security")) with {LoginAccountMaxAttempts=2};
            db.Add(new SystemSetting {Key="system.security",Value=SettingsValueCodec.Json(settings).GetRawText(),UpdatedBy=api.User.Id});await db.SaveChangesAsync();
        }
        using var client=await LoginClientAsync(api);
        for(var i=0;i<2;i++){using var invalid=await WriteAsync(client,"/api/v1/auth/change-password",Change("wrong-current"));Assert.Equal(HttpStatusCode.Forbidden,invalid.StatusCode);}
        using var limited=await WriteAsync(client,"/api/v1/auth/change-password",Change(api.Password));Assert.Equal(HttpStatusCode.TooManyRequests,limited.StatusCode);Assert.True(limited.Headers.RetryAfter?.Delta>TimeSpan.Zero);
        using var login=await WriteAsync(client,"/api/v1/auth/login",new {username=api.User.Username,password=api.Password});Assert.Equal(HttpStatusCode.OK,login.StatusCode);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Oversize_requests_are_rejected_with_or_without_content_length(bool chunked)
    {
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync();using var client=await LoginClientAsync(api);
        var csrf=await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/change-password");
        var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {currentPassword=api.Password,newPassword=Changed,confirmPassword=Changed,padding=new string('x',32768)}));
        request.Content=chunked?new ChunkedContent(bytes):new ByteArrayContent(bytes);request.Content.Headers.ContentType=new("application/json");
        request.Headers.Add("X-CSRF-Token",csrf.GetProperty("token").GetString());request.Headers.Add("Idempotency-Key","oversize-test");
        using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.RequestEntityTooLarge,response.StatusCode);
    }

    [Fact]
    public async Task Target_user_and_missing_idempotency_are_rejected()
    {
        await using var api=new ApiFixture();api.User.MustChangePassword=true;await api.InitializeAsync();using var client=await LoginClientAsync(api);
        using var target=await WriteAsync(client,"/api/v1/auth/change-password",new {currentPassword=api.Password,newPassword=Changed,confirmPassword=Changed,userId=Guid.NewGuid()});Assert.Equal(HttpStatusCode.UnprocessableEntity,target.StatusCode);
        var csrf=await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/change-password"){Content=JsonContent.Create(Change(api.Password))};request.Headers.Add("X-CSRF-Token",csrf.GetProperty("token").GetString());
        using var missing=await client.SendAsync(request);Assert.Equal(HttpStatusCode.UnprocessableEntity,missing.StatusCode);
    }

    private sealed class ChunkedContent(byte[] bytes):HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length){length=0;return false;}
    }
    private sealed class SwitchableRealRateStore(string connection,string prefix):ILoginRateStore,IDisposable
    {
        private readonly RedisLoginRateStore good=new(connection,prefix),bad=new("127.0.0.1:1",prefix+"-unreachable");
        public bool Fail {get;set;}
        public ValueTask<LoginRateDecision> TryAcquireAsync(LoginRateRequest request,CancellationToken ct)=>(Fail?bad:good).TryAcquireAsync(request,ct);
        public void Dispose(){good.Dispose();bad.Dispose();}
    }
}

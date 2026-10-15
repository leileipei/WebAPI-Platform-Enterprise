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
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PasswordResetTests
{
    private const string Old="Legacy-target-private-password-123!",Temporary="Temporary-private-password-456!";
    // This target represents a pre-upgrade local account, not the CreateUser flow.
    private static async Task<UserRecord> TargetAsync(ApiFixture api,string source="local",string status="Active")
    {
        var user=new UserRecord {Username="target_"+Guid.NewGuid().ToString("N"),DisplayName="重置目标",AuthSource=source,Status=status,SecurityStamp=Guid.NewGuid().ToString("N")};
        if(source=="local")user.PasswordHash=new PasswordHasher<UserRecord>().HashPassword(user,Old);
        await using var db=api.Context();db.Add(user);await db.SaveChangesAsync();return user;
    }
    private static async Task<HttpResponseMessage> CommandAsync(HttpClient client,string path,object body,string? tag=null,string? key=null)
    {
        var csrf=await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(body)};
        request.Headers.Add("X-CSRF-Token",csrf.GetProperty("token").GetString());request.Headers.Add("Idempotency-Key",key??Guid.NewGuid().ToString("N"));
        if(tag is not null)request.Headers.Add("If-Match",tag);return await client.SendAsync(request);
    }
    private static async Task<HttpClient> TargetClientAsync(ApiFixture api,UserRecord target,string password=Old)
    {
        var client=new HttpClient(new HttpClientHandler {CookieContainer=new CookieContainer(),AllowAutoRedirect=false}){BaseAddress=api.Client.BaseAddress};
        using var login=await CommandAsync(client,"/api/v1/auth/login",new{username=target.Username,password});login.EnsureSuccessStatusCode();return client;
    }

    [Fact]
    public async Task Reset_invalidates_old_session_and_requires_temporary_login_change()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);var target=await TargetAsync(api);
        using var admin=await api.LoginAsync();admin.EnsureSuccessStatusCode();using var old=await TargetClientAsync(api,target);
        using var reset=await CommandAsync(api.Client,$"/api/v1/users/{target.Id}/password-reset",new{temporaryPassword=Temporary},"\"1\"");Assert.Equal(HttpStatusCode.OK,reset.StatusCode);
        var dto=await reset.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(target.Id,dto.GetProperty("userId").GetGuid());Assert.Equal(2,dto.GetProperty("revision").GetInt64());Assert.True(dto.GetProperty("mustChangePassword").GetBoolean());Assert.Equal(3,dto.EnumerateObject().Count());
        using var me=await old.GetAsync("/api/v1/auth/me");Assert.Equal(HttpStatusCode.Unauthorized,me.StatusCode);
        using var temporary=await TargetClientAsync(api,target,Temporary);using var restricted=await temporary.GetAsync("/api/v1/auth/me");Assert.True((await restricted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mustChangePassword").GetBoolean());
        using var changed=await CommandAsync(temporary,"/api/v1/auth/change-password",new {currentPassword=Temporary,newPassword="Permanent-private-password-789!",confirmPassword="Permanent-private-password-789!"});Assert.Equal(HttpStatusCode.OK,changed.StatusCode);
        await using var db=api.Context();var after=await db.Set<UserRecord>().AsNoTracking().SingleAsync(x=>x.Id==target.Id);Assert.False(after.MustChangePassword);Assert.NotEqual(target.SecurityStamp,after.SecurityStamp);
        var audit=await db.Set<AuditLog>().SingleAsync(x=>x.Action=="user.password.reset");Assert.Equal(api.User.Id,audit.UserId);
        var text=audit.BeforeJson+audit.AfterJson+string.Join('\n',(await db.Set<IdempotencyRecord>().ToArrayAsync()).Select(x=>Encoding.UTF8.GetString(x.ResponseBytes??[])));
        foreach(var secret in new[]{Old,Temporary,target.PasswordHash!,target.SecurityStamp,after.PasswordHash!,after.SecurityStamp})Assert.DoesNotContain(secret,text,StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expired_etag_cannot_replay_reset_success_and_same_key_cannot_change_input()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);var target=await TargetAsync(api);using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();
        var path=$"/api/v1/users/{target.Id}/password-reset";
        using var first=await CommandAsync(api.Client,path,new{temporaryPassword=Temporary},"\"1\"","reset-key");Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        using var stale=await CommandAsync(api.Client,path,new{temporaryPassword=Temporary},"\"1\"","reset-key");Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        using var different=await CommandAsync(api.Client,path,new{temporaryPassword=Temporary+"changed"},"\"2\"","reset-key");Assert.Equal(HttpStatusCode.Conflict,different.StatusCode);
        await using var db=api.Context();Assert.Single(await db.Set<IdempotencyRecord>().Where(x=>x.Operation=="user.password.reset").ToArrayAsync());Assert.Single(await db.Set<AuditLog>().Where(x=>x.Action=="user.password.reset").ToArrayAsync());
    }

    [Theory]
    [InlineData("sso","Active")] [InlineData("local","Disabled")] [InlineData("self","Active")]
    public async Task Sso_disabled_and_self_resets_are_denied(string source,string status)
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();
        var target=source=="self"?api.User:await TargetAsync(api,source,status);
        using var result=await CommandAsync(api.Client,$"/api/v1/users/{target.Id}/password-reset",new{temporaryPassword=Temporary},"\"1\"");Assert.Equal(HttpStatusCode.Conflict,result.StatusCode);
        await using var db=api.Context();Assert.Equal(target.PasswordHash,(await db.Set<UserRecord>().AsNoTracking().SingleAsync(x=>x.Id==target.Id)).PasswordHash);
    }

    [Fact]
    public async Task Nonplatform_and_restricted_actors_cannot_reset()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();var target=await TargetAsync(api);using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();
        using var denied=await CommandAsync(api.Client,$"/api/v1/users/{target.Id}/password-reset",new{temporaryPassword=Temporary},"\"1\"");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        await using(var db=api.Context())await db.Set<UserRecord>().Where(x=>x.Id==api.User.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.MustChangePassword,true));
        using var restricted=await CommandAsync(api.Client,$"/api/v1/users/{target.Id}/password-reset",new{temporaryPassword=Temporary},"\"1\"");Assert.Equal(HttpStatusCode.Forbidden,restricted.StatusCode);
    }

    [Fact]
    public async Task Resetting_sole_local_admin_preserves_roles_and_scopes()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);
        await using(var db=api.Context())
        {
            var sso=new UserRecord {Username="sso_platform",DisplayName="SSO管理者",AuthSource="sso",SecurityStamp=Guid.NewGuid().ToString("N")};
            var role=await db.Set<UserRole>().Where(x=>x.UserId==api.User.Id).Select(x=>x.RoleId).SingleAsync();db.Add(sso);db.Add(new UserRole {UserId=sso.Id,RoleId=role});await db.SaveChangesAsync();
            using var services=api.Services();var service=services.ServiceProvider.GetRequiredService<PasswordLifecycleService>();
            var requestType=typeof(ChangePasswordRequest).Assembly.GetType("WebApi.Contracts.Security.ResetPasswordRequest");Assert.NotNull(requestType);
            var request=Activator.CreateInstance(requestType,Temporary);var method=service.GetType().GetMethod("ResetAsync");Assert.NotNull(method);
            var task=(Task)method.Invoke(service,[api.User.Id,request,"\"1\"",new ActorContext(sso.Id,"sole-admin-reset"),new CommandRequestContext("sole-admin-reset"),CancellationToken.None])!;await task;
            Assert.Equal(role,await db.Set<UserRole>().Where(x=>x.UserId==api.User.Id).Select(x=>x.RoleId).SingleAsync());
            Assert.Single(await db.Set<UserProjectScope>().Where(x=>x.UserId==api.User.Id&&x.AccessMode=="read_write").ToArrayAsync());
        }
        await using var afterDb=api.Context();var after=await afterDb.Set<UserRecord>().SingleAsync(x=>x.Id==api.User.Id);Assert.True(after.MustChangePassword);Assert.Equal("Active",after.Status);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_reset_and_idempotent_receipt()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);var target=await TargetAsync(api);using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();
        await using(var db=api.Context())await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION p4_reject_reset_audit() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN IF NEW.action = ''user.password.reset'' THEN RAISE EXCEPTION ''isolated reset audit unavailable''; END IF; RETURN NEW; END'; CREATE TRIGGER p4_reject_reset_audit BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION p4_reject_reset_audit()");
        using var response=await CommandAsync(api.Client,$"/api/v1/users/{target.Id}/password-reset",new{temporaryPassword=Temporary},"\"1\"");Assert.Equal(HttpStatusCode.InternalServerError,response.StatusCode);
        await using var afterDb=api.Context();var after=await afterDb.Set<UserRecord>().SingleAsync(x=>x.Id==target.Id);Assert.Equal(target.PasswordHash,after.PasswordHash);Assert.Equal(target.SecurityStamp,after.SecurityStamp);Assert.False(after.MustChangePassword);Assert.Equal(1,after.Revision);Assert.Empty(await afterDb.Set<IdempotencyRecord>().ToArrayAsync());
    }

    [Fact]
    public async Task Fingerprints_are_purpose_bound_keyed_and_input_specific()
    {
        var hasher=new LoginKeyHasher(Enumerable.Range(0,32).Select(x=>(byte)x).ToArray());var method=hasher.GetType().GetMethod("PasswordResetFingerprint");Assert.NotNull(method);
        var actor=Guid.Parse("11111111-1111-1111-1111-111111111111");var target=Guid.Parse("22222222-2222-2222-2222-222222222222");
        string Fingerprint(Guid a,Guid t,long rev,string password)=>(string)method.Invoke(hasher,[a,t,rev,password])!;
        var original=Fingerprint(actor,target,42,"temporary-password-123");
        Assert.Equal("820bc52f6da8de6f3174c9ce5a2ffdc76396e919a25f00d3254e5852861145c9",original);
        Assert.Equal(5,new[]{original,Fingerprint(actor,target,42,"different-password"),Fingerprint(target,actor,42,"temporary-password-123"),Fingerprint(actor,Guid.NewGuid(),42,"temporary-password-123"),Fingerprint(actor,target,43,"temporary-password-123")}.Distinct().Count());
    }

    [Theory] [InlineData("short")] [InlineData("oversize")] [InlineData("missing-etag")]
    public async Task Invalid_reset_inputs_leave_target_unchanged(string kind)
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);var target=await TargetAsync(api);using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();
        var password=kind=="short"?"short":kind=="oversize"?new string('A',16384):Temporary;
        using var response=await CommandAsync(api.Client,$"/api/v1/users/{target.Id}/password-reset",new{temporaryPassword=password},kind=="missing-etag"?null:"\"1\"");
        Assert.Equal(kind=="oversize"?HttpStatusCode.RequestEntityTooLarge:kind=="missing-etag"?HttpStatusCode.PreconditionFailed:HttpStatusCode.UnprocessableEntity,response.StatusCode);
        await using var db=api.Context();Assert.Equal(target.SecurityStamp,(await db.Set<UserRecord>().SingleAsync(x=>x.Id==target.Id)).SecurityStamp);
    }
}

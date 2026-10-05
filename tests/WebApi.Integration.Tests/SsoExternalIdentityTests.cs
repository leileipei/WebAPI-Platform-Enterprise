using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Sso;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoExternalIdentityTests
{
    private static CreateSsoUserRequest Body(Guid provider,string subject="Subject-A",string? email=null)=>new("sso_"+Guid.NewGuid().ToString("N"),"预开通用户",email,provider,subject);
    private static async Task<HttpResponseMessage> Create(SsoFixture f,object body)=>await f.Api.WriteAsync(HttpMethod.Post,"/api/v1/users/sso",body);
    [Fact] public async Task SameEmailNeverBindsLocalUserAndCreatesNoRolesOrScopes()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();var hash=f.Api.User.PasswordHash;
        await using(var db=f.Api.Context()){var local=await db.Set<UserRecord>().SingleAsync();local.Email="same@example.test";await db.SaveChangesAsync();}
        using var created=await Create(f,Body(provider.Id,email:"same@example.test"));created.EnsureSuccessStatusCode();
        var dto=(await created.Content.ReadFromJsonAsync<UserDto>())!;Assert.Equal("sso",dto.AuthSource);Assert.Empty(dto.RoleIds);Assert.NotEqual(f.Api.User.Id,dto.Id);
        await using var context=f.Api.Context();Assert.Null((await context.Set<UserRecord>().SingleAsync(x=>x.Id==dto.Id)).PasswordHash);
        Assert.Empty(await context.Set<UserProjectScope>().Where(x=>x.UserId==dto.Id).ToArrayAsync());
        Assert.Equal(hash,(await context.Set<UserRecord>().SingleAsync(x=>x.Id==f.Api.User.Id)).PasswordHash);
        using var binding=await f.Api.WriteAsync(HttpMethod.Put,$"/api/v1/users/{f.Api.User.Id}/external-identity",new UpdateExternalIdentityRequest(provider.Id,"Subject-A",true),"\"1\"");
        Assert.Equal(HttpStatusCode.Conflict,binding.StatusCode);Assert.Equal(hash,(await context.Set<UserRecord>().AsNoTracking().SingleAsync(x=>x.Id==f.Api.User.Id)).PasswordHash);
    }
    [Fact] public async Task SubjectIsCaseSensitiveAndUnique()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();
        (await Create(f,Body(provider.Id,"Subject-A"))).EnsureSuccessStatusCode();(await Create(f,Body(provider.Id,"subject-a"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,(await Create(f,Body(provider.Id,"Subject-A"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await Create(f,Body(provider.Id,new string('x',256)))).StatusCode);
        await using var db=f.Api.Context();Assert.Equal(2,await db.Set<UserExternalIdentity>().CountAsync());Assert.Equal(3,await db.Set<UserRecord>().CountAsync());
    }
    [Theory][InlineData("system.sso.manage")][InlineData("user.manage")]
    public async Task BindingNeedsBothPermissions(string missing)
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();
        using var created=await Create(f,Body(provider.Id));created.EnsureSuccessStatusCode();var user=(await created.Content.ReadFromJsonAsync<UserDto>())!;
        await using(var db=f.Api.Context()){var permission=await db.Set<Permission>().SingleAsync(x=>x.Code==missing);db.RemoveRange(await db.Set<RolePermission>().Where(x=>x.PermissionId==permission.Id).ToArrayAsync());await db.SaveChangesAsync();}
        Assert.Equal(HttpStatusCode.Forbidden,(await f.Api.Client.GetAsync($"/api/v1/users/{user.Id}/external-identity")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await Create(f,Body(provider.Id,"other"))).StatusCode);
    }
    [Fact] public async Task BindingCorrectionRequiresDisabledUserAndRotatesStamp()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();
        using var created=await Create(f,Body(provider.Id));created.EnsureSuccessStatusCode();var user=(await created.Content.ReadFromJsonAsync<UserDto>())!;
        var path=$"/api/v1/users/{user.Id}/external-identity";
        Assert.Equal(HttpStatusCode.Conflict,(await f.Api.WriteAsync(HttpMethod.Put,path,new UpdateExternalIdentityRequest(provider.Id,"Subject-B",true),"\"1\"")).StatusCode);
        (await f.Api.WriteAsync(HttpMethod.Put,$"/api/v1/users/{user.Id}",new UpdateUserRequest(user.DisplayName,"Disabled",user.Email),"\"1\"")).EnsureSuccessStatusCode();
        string stamp;await using(var db=f.Api.Context())stamp=(await db.Set<UserRecord>().SingleAsync(x=>x.Id==user.Id)).SecurityStamp;
        using var changed=await f.Api.WriteAsync(HttpMethod.Put,path,new UpdateExternalIdentityRequest(provider.Id,"Subject-B",true),"\"1\"");changed.EnsureSuccessStatusCode();
        Assert.Equal("Subject-B",(await changed.Content.ReadFromJsonAsync<ExternalIdentityDto>())!.Subject);
        Assert.Equal(HttpStatusCode.PreconditionFailed,(await f.Api.WriteAsync(HttpMethod.Put,path,new UpdateExternalIdentityRequest(provider.Id,"Subject-C",true),"\"1\"")).StatusCode);
        await using var context=f.Api.Context();Assert.NotEqual(stamp,(await context.Set<UserRecord>().SingleAsync(x=>x.Id==user.Id)).SecurityStamp);
        var audits=await context.Set<AuditLog>().Where(x=>x.Action.StartsWith("user.sso")||x.Action.StartsWith("user.external")).ToArrayAsync();
        Assert.All(audits,log=>{Assert.DoesNotContain("Subject-A",log.AfterJson??"");Assert.DoesNotContain("Subject-B",log.AfterJson??"");});
    }
    [Fact] public async Task PasswordAndPermissionPayloadsAreRejectedAndSsoCannotUsePasswordLogin()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();var body=Body(provider.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await Create(f,new{body.Username,body.DisplayName,body.Email,body.ProviderId,body.Subject,password="synthetic",roleIds=Array.Empty<Guid>()})).StatusCode);
        using var created=await Create(f,body);created.EnsureSuccessStatusCode();
        using var client=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=f.Api.Client.BaseAddress};
        var csrf=(await client.GetFromJsonAsync<Dictionary<string,string>>("/api/v1/auth/csrf"))!;using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/auth/login"){Content=JsonContent.Create(new{username=body.Username,password=f.Api.Password})};request.Headers.Add("X-CSRF-Token",csrf["token"]);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.SendAsync(request)).StatusCode);
        (await f.Api.LoginAsync()).EnsureSuccessStatusCode();
    }
    [Fact] public async Task LocalEmailUniquenessIsPreservedWhileSsoEmailIsDisplayOnly()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();
        await using(var db=f.Api.Context()){var local=await db.Set<UserRecord>().SingleAsync();local.Email="local@example.test";await db.SaveChangesAsync();}
        using var duplicate=await f.Api.WriteAsync(HttpMethod.Post,"/api/v1/users",new CreateUserRequest("other_local","其他本地",f.Api.Password,"local@example.test"));
        Assert.Equal(HttpStatusCode.Conflict,duplicate.StatusCode);
        var provider=await f.CreateAsync();(await Create(f,Body(provider.Id,email:"local@example.test"))).EnsureSuccessStatusCode();
    }
}

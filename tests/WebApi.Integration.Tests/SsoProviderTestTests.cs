using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoProviderTestTests
{
    [Fact] public async Task TestCannotAuthorizeDifferentRevision()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();f.IdentityService.Delay=true;
        var testing=f.SendAsync(HttpMethod.Post,$"/{provider.Id}/test",new{},"\"1\"");await f.IdentityService.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var rename=await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",SsoFixture.Body("并发改名"),"\"1\"");rename.EnsureSuccessStatusCode();
        f.IdentityService.Release.TrySetResult();using var result=await testing;Assert.Equal(HttpStatusCode.PreconditionFailed,result.StatusCode);
        await using var db=f.Api.Context();Assert.Empty(await db.Set<SsoProviderTest>().ToArrayAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/enable",new{},"\"2\"")).StatusCode);
    }
    [Fact] public async Task TestExpiresAndDoesNotClaimClientOrUserAuthentication()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();
        using var tested=await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/test",new{},"\"1\"");tested.EnsureSuccessStatusCode();
        var result=await tested.Content.ReadFromJsonAsync<JsonElement>();Assert.Contains("客户端认证与用户登录尚未验证",result.ToString());Assert.Equal("Passed",result.GetProperty("status").GetString());
        f.Time.Now+=TimeSpan.FromMinutes(16);Assert.Equal(HttpStatusCode.UnprocessableEntity,(await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/enable",new{},"\"1\"")).StatusCode);
        await f.EnableAsync(provider);
    }
    [Fact] public async Task EnableRequiresAvailableLocalAdministrator()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();
        using var tested=await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/test",new{},"\"1\"");tested.EnsureSuccessStatusCode();
        await using(var db=f.Api.Context()){var user=await db.Set<UserRecord>().SingleAsync();user.AuthSource="sso";user.PasswordHash=null;await db.SaveChangesAsync();}
        Assert.Equal(HttpStatusCode.Conflict,(await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/enable",new{},"\"1\"")).StatusCode);
    }
}

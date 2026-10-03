using System.Net;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SessionTests
{
    [Fact] public async Task CookieLoginRequiresRealPassword()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();
        using var anonymous=await api.Client.GetAsync("/api/v1/auth/me"); Assert.Equal(HttpStatusCode.Unauthorized,anonymous.StatusCode);
        using var wrong=await api.LoginAsync("incorrect"); Assert.Equal(HttpStatusCode.Unauthorized,wrong.StatusCode);
        using var correct=await api.LoginAsync(); Assert.Equal(HttpStatusCode.OK,correct.StatusCode);
        Assert.Contains(correct.Headers.GetValues("Set-Cookie"),x=>x.Contains("httponly",StringComparison.OrdinalIgnoreCase));
        using var me=await api.Client.GetAsync("/api/v1/auth/me"); Assert.Equal(HttpStatusCode.OK,me.StatusCode);Assert.Contains(api.User.Username,await me.Content.ReadAsStringAsync());
    }
    [Fact] public async Task MissingCsrfBlocksWrite()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();using var login=await api.LoginAsync();
        using var result=await api.Client.PostAsync("/api/v1/auth/logout",null);Assert.Equal(HttpStatusCode.Forbidden,result.StatusCode);
        using var me=await api.Client.GetAsync("/api/v1/auth/me");Assert.Equal(HttpStatusCode.OK,me.StatusCode);
    }
    [Fact] public async Task DisabledUserSessionRejected()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();using var login=await api.LoginAsync();
        await using(var db=api.Context()) {var user=await db.Set<UserRecord>().SingleAsync();user.Status="Disabled";await db.SaveChangesAsync();}
        using var me=await api.Client.GetAsync("/api/v1/auth/me");Assert.Equal(HttpStatusCode.Unauthorized,me.StatusCode);Assert.Null(me.Headers.Location);
    }
    [Fact] public async Task LoginAuditContainsNoPassword()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();using var login=await api.LoginAsync();Assert.Equal(HttpStatusCode.OK,login.StatusCode);
        await using var db=api.Context();var entry=await db.Set<AuditLog>().SingleAsync(x=>x.Action=="auth.login");
        Assert.Equal(api.User.Id,entry.UserId);Assert.NotNull(entry.Ip);Assert.False(string.IsNullOrWhiteSpace(entry.TraceId));Assert.True(entry.CreatedAt>DateTimeOffset.UtcNow.AddMinutes(-1));
        var text=(entry.BeforeJson??"")+(entry.AfterJson??"");Assert.DoesNotContain(api.Password,text);Assert.DoesNotContain("Cookie",text,StringComparison.OrdinalIgnoreCase);
    }
}

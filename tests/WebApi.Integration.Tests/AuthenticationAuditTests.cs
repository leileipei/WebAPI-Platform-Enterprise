using Xunit;
using System.Net;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
namespace WebApi.Integration.Tests;
public sealed class AuthenticationAuditTests
{
    private static string Connection=>Environment.GetEnvironmentVariable("WEBAPI_TEST_REDIS")??"redis:6379";
    [Fact] public async Task FixedAnonymousAuditIsPlatformOnlyAndContainsNoInputSecrets()
    {
        await using var f=new ApiFixture();await f.InitializeAsync();await f.SeedScopeAsync();(await f.LoginAsync()).EnsureSuccessStatusCode();
        await using(var db=f.Context())await new AuthenticationAuditWriter(db).WriteAsync(new("auth.login.failed",null,IPAddress.Parse("192.0.2.1"),"fixture-trace",new string('a',64),"invalid_credentials"),default);
        await using(var db=f.Context())
        {
            var row=await db.Set<AuditLog>().SingleAsync(x=>x.Action=="auth.login.failed");Assert.Null(row.UserId);Assert.Null(row.OrganizationId);Assert.Null(row.ProjectId);Assert.Null(row.EnvironmentId);Assert.Contains("invalid_credentials",row.AfterJson!);Assert.DoesNotContain(f.User.Username,row.AfterJson!);Assert.DoesNotContain(f.Password,row.AfterJson!);
        }
        var csv=await f.Client.GetStringAsync("/api/v1/audit-logs/export");Assert.DoesNotContain("auth.login.failed",csv);
        await SystemSettingsFixture.PromoteAsync(f);csv=await f.Client.GetStringAsync("/api/v1/audit-logs/export");Assert.Contains("auth.login.failed",csv);Assert.DoesNotContain(f.Password,csv);Assert.DoesNotContain("192.0.2.1",csv);
    }
    [Fact] public async Task ConcurrentLeaseCommitsOnlyOneAuditAndReleaseAllowsRetry()
    {
        await using var f=new ApiFixture();await f.InitializeAsync();var prefix="test-audit-"+Guid.NewGuid().ToString("N");using var gate=new RedisAuthenticationAuditGate(Connection,prefix);
        var claims=await Task.WhenAll(Enumerable.Range(0,100).Select(_=>gate.ClaimAsync(new string('b',64),default).AsTask()));var lease=Assert.Single(claims,x=>x is not null)!;
        await gate.ReleaseAsync(lease,default);var retry=await gate.ClaimAsync(new string('b',64),default);Assert.NotNull(retry);
        await using(var db=f.Context())await new AuthenticationAuditWriter(db).WriteAsync(new("auth.login.throttled",null,IPAddress.Loopback,"audit-trace",null,"login_rate_limited",retry),default);
        await gate.ConfirmAsync(retry!,default);Assert.Null(await gate.ClaimAsync(new string('b',64),default));
        // DB idempotence also protects a crashed caller that committed before confirming Redis.
        await using(var db=f.Context())await new AuthenticationAuditWriter(db).WriteAsync(new("auth.login.throttled",null,IPAddress.Loopback,"late-trace",null,"login_rate_limited",retry),default);
        await using(var db=f.Context())Assert.Equal(1,await db.Set<AuditLog>().CountAsync(x=>x.Action=="auth.login.throttled"));
    }
    [Fact] public async Task OldLeaseCannotReleaseOrConfirmReplacement()
    {
        var prefix="test-audit-"+Guid.NewGuid().ToString("N");using var gate=new RedisAuthenticationAuditGate(Connection,prefix);var ip=new string('c',64);
        var old=await gate.ClaimAsync(ip,default);Assert.NotNull(old);
        using var mux=await ConnectionMultiplexer.ConnectAsync(Connection);await mux.GetDatabase().HashSetAsync(old!.BucketKey.Split('|')[0],"until",0);
        var current=await gate.ClaimAsync(ip,default);Assert.NotNull(current);Assert.NotEqual(old.Token,current!.Token);
        await gate.ReleaseAsync(old,default);await gate.ConfirmAsync(old,default);Assert.Null(await gate.ClaimAsync(ip,default));
        await gate.ConfirmAsync(current,default);Assert.Null(await gate.ClaimAsync(ip,default));
    }
}

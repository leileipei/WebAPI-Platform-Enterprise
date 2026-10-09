using Xunit;
using System.Net;
using System.Security.Cryptography;
using StackExchange.Redis;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Security;
namespace WebApi.Integration.Tests;
public sealed class LoginRateStoreTests
{
    private static string Connection=>Environment.GetEnvironmentVariable("WEBAPI_TEST_REDIS")??"redis:6379";
    private static LoginIdentityKeys Identity(string ip,string user)=>new LoginKeyHasher(new byte[32]).Keys(IPAddress.Parse(ip),user);
    [Fact] public async Task TwoStoresAtomicallyShareBudgetsAndRejectionsDoNotConsumeOtherDimension()
    {
        var prefix="test-login-"+Guid.NewGuid().ToString("N");
        using var a=new RedisLoginRateStore(Connection,prefix);using var b=new RedisLoginRateStore(Connection,prefix);
        var request=new LoginRateRequest(Identity("192.0.2.1","concurrent"),new(2,60,10,60));
        var results=await Task.WhenAll(Enumerable.Range(0,100).Select(i=>(i%2==0?a:b).TryAcquireAsync(request,default).AsTask()));
        Assert.Equal(2,results.Count(r=>r.Kind==LoginRateDecisionKind.Allowed));
        Assert.All(results.Where(r=>r.Kind==LoginRateDecisionKind.Limited),r=>Assert.InRange(r.RetryAfterSeconds,1,60));
        // Exhaust account with one IP; rejected second-IP attempt must not consume its IP budget.
        var limits=new LoginRateLimits(2,60,1,60);
        Assert.Equal(LoginRateDecisionKind.Allowed,(await a.TryAcquireAsync(new(Identity("192.0.2.2","first"),limits),default)).Kind);
        Assert.Equal(LoginRateDecisionKind.Limited,(await b.TryAcquireAsync(new(Identity("192.0.2.3","first"),limits),default)).Kind);
        Assert.Equal(LoginRateDecisionKind.Allowed,(await b.TryAcquireAsync(new(Identity("192.0.2.3","second"),limits),default)).Kind);
        Assert.Equal(LoginRateDecisionKind.Allowed,(await b.TryAcquireAsync(new(Identity("192.0.2.3","third"),limits),default)).Kind);
        Assert.Equal(LoginRateDecisionKind.Limited,(await b.TryAcquireAsync(new(Identity("192.0.2.3","fourth"),limits),default)).Kind);
    }
    [Fact] public async Task CorruptRedisStateFailsClosedAndIpRejectionDoesNotConsumeAccount()
    {
        var prefix="test-login-"+Guid.NewGuid().ToString("N");using var store=new RedisLoginRateStore(Connection,prefix);var limits=new LoginRateLimits(1,60,2,60);
        Assert.Equal(LoginRateDecisionKind.Allowed,(await store.TryAcquireAsync(new(Identity("192.0.2.11","original"),limits),default)).Kind);
        Assert.Equal(LoginRateDecisionKind.Limited,(await store.TryAcquireAsync(new(Identity("192.0.2.11","target"),limits),default)).Kind);
        Assert.Equal(LoginRateDecisionKind.Allowed,(await store.TryAcquireAsync(new(Identity("192.0.2.12","target"),limits),default)).Kind);
        Assert.Equal(LoginRateDecisionKind.Allowed,(await store.TryAcquireAsync(new(Identity("192.0.2.13","target"),limits),default)).Kind);
        var request=new LoginRateRequest(Identity("192.0.2.14","corrupt"),limits);using var mux=await ConnectionMultiplexer.ConnectAsync(Connection);await mux.GetDatabase().HashSetAsync(store.BudgetKeys(request)[0],"count","invalid");
        Assert.Equal(LoginRateDecisionKind.Unavailable,(await store.TryAcquireAsync(request,default)).Kind);
    }
    [Fact] public async Task RejectDoesNotExtendWindowAndWindowExpires()
    {
        var prefix="test-login-"+Guid.NewGuid().ToString("N");using var store=new RedisLoginRateStore(Connection,prefix);
        var request=new LoginRateRequest(Identity("192.0.2.4","window"),new(1,1,1,1));
        Assert.Equal(LoginRateDecisionKind.Allowed,(await store.TryAcquireAsync(request,default)).Kind);
        using var mux=await ConnectionMultiplexer.ConnectAsync(Connection);var db=mux.GetDatabase();
        var key=store.BudgetKeys(request)[0];var before=await db.KeyTimeToLiveAsync(key);
        Assert.Equal(LoginRateDecisionKind.Limited,(await store.TryAcquireAsync(request,default)).Kind);
        Assert.True(await db.KeyTimeToLiveAsync(key)<=before);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));LoginRateDecision result;
        do{await Task.Delay(30,timeout.Token);result=await store.TryAcquireAsync(request,timeout.Token);}while(result.Kind==LoginRateDecisionKind.Limited);
        Assert.Equal(LoginRateDecisionKind.Allowed,result.Kind);
    }
    [Fact] public async Task UnavailableFailsClosedAndCancellationPropagates()
    {
        using var store=new RedisLoginRateStore("127.0.0.1:1,abortConnect=false,connectTimeout=200",Guid.NewGuid().ToString("N"));
        var request=new LoginRateRequest(Identity("192.0.2.5","unavailable"),new(1,1,1,1));
        var started=DateTime.UtcNow;Assert.Equal(LoginRateDecisionKind.Unavailable,(await store.TryAcquireAsync(request,default)).Kind);
        Assert.True(DateTime.UtcNow-started<TimeSpan.FromSeconds(2));
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.TryAcquireAsync(request,canceled.Token).AsTask());
    }
}

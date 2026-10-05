using StackExchange.Redis;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Policies;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class RedisRateLimitTests
{
    private static RateLimitConfiguration Config(int window=600000)=>new("TokenBucket","Route",1,window,3,"Reject");
    private static RateLimitRequest Request(RateLimitConfiguration? config=null)=>new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),null,config??Config());
    private sealed class RedisExecutor(IDatabase db,long time) : ITokenBucketExecutor
    {
        public long Time=time;public string? LastKey;
        public async Task<long[]> TakeAsync(string key,RateLimitConfiguration config,CancellationToken ct)
        {
            LastKey=key;var now=Interlocked.Read(ref Time);var prelude=$"local upstream=redis; local redis={{call=function(cmd,...) if cmd=='TIME' then return {{'{now/1000}','{now%1000*1000}'}} end return upstream.call(cmd,...) end}}; ";
            var result=await db.ScriptEvaluateAsync(prelude+TokenBucketScript.Text,[key],[config.RefillTokens,config.WindowMs,config.Burst]);return ((RedisResult[])result!).Select(v=>(long)v).ToArray();
        }
    }
    [Fact] public async Task TwoClientsShareExactBurstAtFrozenRedisTime()
    {
        using var a=await ConnectionMultiplexer.ConnectAsync("redis:6379");using var b=await ConnectionMultiplexer.ConnectAsync("redis:6379");var prefix="test:policies:"+Guid.NewGuid();var request=Request();var first=new RedisExecutor(a.GetDatabase(),1000000);var second=new RedisExecutor(b.GetDatabase(),1000000);using var storeA=new RedisTokenBucketStore(first,prefix,TimeSpan.FromSeconds(5));using var storeB=new RedisTokenBucketStore(second,prefix,TimeSpan.FromSeconds(5));
        try {var decisions=await Task.WhenAll(Enumerable.Range(0,20).Select(i=>(i%2==0?storeA:storeB).TakeAsync(request,CancellationToken.None).AsTask()));Assert.Equal(3,decisions.Count(d=>d.Kind==RateLimitDecisionKind.Allowed));Assert.Equal(17,decisions.Count(d=>d.Kind==RateLimitDecisionKind.Exceeded));Assert.All(decisions.Where(d=>d.Kind==RateLimitDecisionKind.Exceeded),d=>Assert.Equal(600,d.RetryAfterSeconds));}finally {if(first.LastKey is not null) await a.GetDatabase().KeyDeleteAsync(first.LastKey);}
    }
    [Fact] public async Task ElapsedTimeRefillsAndSetsBoundedTtl()
    {using var mux=await ConnectionMultiplexer.ConnectAsync("redis:6379");var executor=new RedisExecutor(mux.GetDatabase(),1000000);using var store=new RedisTokenBucketStore(executor,"test:policies:"+Guid.NewGuid(),TimeSpan.FromSeconds(5));var request=Request(Config(1000));try {for(var i=0;i<3;i++) Assert.Equal(RateLimitDecisionKind.Allowed,(await store.TakeAsync(request,default)).Kind);Assert.Equal(RateLimitDecisionKind.Exceeded,(await store.TakeAsync(request,default)).Kind);executor.Time+=500;Assert.Equal(RateLimitDecisionKind.Exceeded,(await store.TakeAsync(request,default)).Kind);executor.Time+=500;Assert.Equal(RateLimitDecisionKind.Allowed,(await store.TakeAsync(request,default)).Kind);Assert.Equal(RateLimitDecisionKind.Exceeded,(await store.TakeAsync(request,default)).Kind);var ttl=await mux.GetDatabase().KeyTimeToLiveAsync(executor.LastKey!);Assert.InRange(ttl!.Value.TotalSeconds,1,6);}finally {if(executor.LastKey is not null) await mux.GetDatabase().KeyDeleteAsync(executor.LastKey);}}
    [Fact] public async Task DifferentAppsAndRoutesIsolated()
    {using var mux=await ConnectionMultiplexer.ConnectAsync("redis:6379");var executor=new RedisExecutor(mux.GetDatabase(),1000000);using var store=new RedisTokenBucketStore(executor,"test:policies:"+Guid.NewGuid(),TimeSpan.FromSeconds(5));var request=Request(Config() with {KeyBy="ApplicationRoute"}) with {ApplicationId=Guid.NewGuid()};var keys=new HashSet<string>();try {foreach(var r in new[]{request,request with {ApplicationId=Guid.NewGuid()},request with {RouteId=Guid.NewGuid()}}) {for(var i=0;i<3;i++) Assert.Equal(RateLimitDecisionKind.Allowed,(await store.TakeAsync(r,default)).Kind);keys.Add(executor.LastKey!);Assert.Equal(RateLimitDecisionKind.Exceeded,(await store.TakeAsync(r,default)).Kind);}Assert.Equal(3,keys.Count);}finally {foreach(var key in keys) await mux.GetDatabase().KeyDeleteAsync(key);}}
    [Fact] public async Task SameAppCredentialsShareBucket()
    {using var mux=await ConnectionMultiplexer.ConnectAsync("redis:6379");var executor=new RedisExecutor(mux.GetDatabase(),1000000);using var store=new RedisTokenBucketStore(executor,"test:policies:"+Guid.NewGuid(),TimeSpan.FromSeconds(5));var app=Guid.NewGuid();var r=Request(Config() with {KeyBy="ApplicationRoute"}) with {ApplicationId=app};try {for(var i=0;i<3;i++) Assert.Equal(RateLimitDecisionKind.Allowed,(await store.TakeAsync(r,default)).Kind);Assert.Equal(RateLimitDecisionKind.Exceeded,(await store.TakeAsync(r with {ApplicationId=app},default)).Kind);}finally {if(executor.LastKey is not null) await mux.GetDatabase().KeyDeleteAsync(executor.LastKey);}}
    [Fact] public async Task RedisClockRollbackDoesNotRefillAlreadyAccountedTime()
    {
        using var mux=await ConnectionMultiplexer.ConnectAsync("redis:6379");var executor=new RedisExecutor(mux.GetDatabase(),1000000);using var store=new RedisTokenBucketStore(executor,"test:policies:"+Guid.NewGuid(),TimeSpan.FromSeconds(5));var request=Request(Config(1000));
        try {for(var i=0;i<3;i++) Assert.Equal(RateLimitDecisionKind.Allowed,(await store.TakeAsync(request,default)).Kind);executor.Time-=1000;Assert.Equal(RateLimitDecisionKind.Exceeded,(await store.TakeAsync(request,default)).Kind);executor.Time+=1000;Assert.Equal(RateLimitDecisionKind.Exceeded,(await store.TakeAsync(request,default)).Kind);executor.Time+=1000;Assert.Equal(RateLimitDecisionKind.Allowed,(await store.TakeAsync(request,default)).Kind);}finally {if(executor.LastKey is not null)await mux.GetDatabase().KeyDeleteAsync(executor.LastKey);}
    }
    private sealed class SlowExecutor : ITokenBucketExecutor {public int Sends;public Task<long[]> TakeAsync(string key,RateLimitConfiguration config,CancellationToken ct) {Interlocked.Increment(ref Sends);return Task.Delay(500).ContinueWith(_=>new long[]{1,0});}}
    [Fact] public async Task TimeoutAfterDispatchDoesNotRedispatch()
    {var executor=new SlowExecutor();using var store=new RedisTokenBucketStore(executor,"test:"+Guid.NewGuid(),TimeSpan.FromMilliseconds(20));Assert.Equal(RateLimitDecisionKind.StoreUnavailable,(await store.TakeAsync(Request(),default)).Kind);await Task.Delay(550);Assert.Equal(1,executor.Sends);}
    [Fact] public async Task CancelledCallerNotReportedAsBackendFailure()
    {var executor=new SlowExecutor();using var store=new RedisTokenBucketStore(executor,"test:"+Guid.NewGuid(),TimeSpan.FromSeconds(1));using var ct=new CancellationTokenSource();var pending=store.TakeAsync(Request(),ct.Token).AsTask();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.Equal(1,executor.Sends);}
    [Fact] public async Task ProductionRedisTimeUsesSharedBucketWithoutTestClock()
    {
        var prefix="test:policies:"+Guid.NewGuid();var request=Request();using var storeA=new RedisTokenBucketStore(new TrafficPolicySettings("redis:6379",prefix,5000));using var storeB=new RedisTokenBucketStore(new TrafficPolicySettings("redis:6379",prefix,5000));using var mux=await ConnectionMultiplexer.ConnectAsync("redis:6379");var key=$"{prefix}:{{{request.EnvironmentId:N}}}:policy:{request.RuntimePolicyId:N}:route:{request.RouteId:N}";
        var started=System.Diagnostics.Stopwatch.GetTimestamp();try {var decisions=await Task.WhenAll(Enumerable.Range(0,20).Select(i=>(i%2==0?storeA:storeB).TakeAsync(request,default).AsTask()));var elapsed=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;Assert.DoesNotContain(decisions,d=>d.Kind==RateLimitDecisionKind.StoreUnavailable);var allowed=decisions.Count(d=>d.Kind==RateLimitDecisionKind.Allowed);Assert.InRange(allowed,3,3+(int)Math.Floor(elapsed/600000));Assert.Equal(20-allowed,decisions.Count(d=>d.Kind==RateLimitDecisionKind.Exceeded));}finally {await mux.GetDatabase().KeyDeleteAsync(key);}
    }

}

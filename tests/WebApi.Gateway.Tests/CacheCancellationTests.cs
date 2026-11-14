using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using Xunit;

namespace WebApi.Gateway.Tests;

// These fault injections deliberately hold a calling thread until cancellation.
// Keep that synthetic blocking away from unrelated timed integration assertions.
[Collection("Response cache Redis")]
public sealed class CacheCancellationTests
{
    private sealed class Immediate:IResponseCacheExecutor
    {
        internal int Calls;
        public Task<string[]> ExecuteAsync(string operation,string prefix,CacheLookupKey key,string? payload,long maxBytes,int maxEntries,int ttlMs,CancellationToken ct)
        {Interlocked.Increment(ref Calls);return Task.FromResult(new[]{operation=="get"?"miss":"stored"});}
    }
    private sealed class CompletedAfterCancellation(Action? cancelRequest):IResponseCacheExecutor
    {
        internal int Calls;
        internal bool CancellationObserved;
        public Task<string[]> ExecuteAsync(string operation,string prefix,CacheLookupKey key,string? payload,long maxBytes,int maxEntries,int ttlMs,CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            cancelRequest?.Invoke();
            // Model a synchronous dispatch prefix that returns an already-completed
            // task after cancellation. The guard bounds the test, not the cache budget.
            CancellationObserved=ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            return Task.FromResult(new[]{operation=="get"?"miss":"stored"});
        }
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task CompletedResultAfterCancellationCannotBecomeSuccess(bool read,bool callerCancels)
    {
        using var f=new CacheEligibilityTests.SettingsFixture();
        using var request=new CancellationTokenSource();
        var executor=new CompletedAfterCancellation(callerCancels?request.Cancel:null);
        using var store=new CachePipelineFixture.ObservedStore(new RedisResponseCacheStore(executor,f.Settings));
        var key=new CacheLookupKey(new string('c',64));
        var entry=new CacheEntry(new Dictionary<string,string[]>(),[1],DateTimeOffset.UtcNow,0,TimeSpan.FromSeconds(60));
        CacheReadResult? readResult=null;CacheWriteResult? writeResult=null;
        var error=await Record.ExceptionAsync(async()=>{
            if(read)readResult=await store.GetAsync(key,request.Token);
            else writeResult=await store.PutAsync(key,entry,request.Token);
        });

        Assert.True(executor.CancellationObserved,"The executor must observe cancellation before returning its completed task.");
        Assert.Equal(1,executor.Calls);
        if(callerCancels)Assert.IsAssignableFrom<OperationCanceledException>(error);
        else
        {
            Assert.Null(error);
            if(read)Assert.Equal(CacheReadKind.Unavailable,readResult!.Kind);
            else Assert.Equal(CacheWriteKind.Bypass,writeResult!.Kind);
        }
        Assert.Equal(read?0:1,store.WriteAttempts);Assert.Equal(0,store.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedResultBeforeCancellationRemainsSuccessful(bool read)
    {
        using var f=new CacheEligibilityTests.SettingsFixture();var executor=new Immediate();
        using var store=new CachePipelineFixture.ObservedStore(new RedisResponseCacheStore(executor,f.Settings));
        var key=new CacheLookupKey(new string('d',64));
        if(read)Assert.Equal(CacheReadKind.Miss,(await store.GetAsync(key,default)).Kind);
        else
        {
            var entry=new CacheEntry(new Dictionary<string,string[]>(),[1],DateTimeOffset.UtcNow,0,TimeSpan.FromSeconds(60));
            Assert.Equal(CacheWriteKind.Stored,(await store.PutAsync(key,entry,default)).Kind);
        }
        Assert.Equal(1,executor.Calls);Assert.Equal(read?0:1,store.Writes);
    }
}

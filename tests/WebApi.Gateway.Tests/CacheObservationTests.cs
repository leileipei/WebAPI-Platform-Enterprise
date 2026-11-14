using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class CacheObservationTests
{
    [Fact]
    public async Task BypassedWriteDoesNotSatisfyStoredEntryCount()
    {
        using var settings=new CacheEligibilityTests.SettingsFixture();
        using var store=new CachePipelineFixture.ObservedStore(new RedisResponseCacheStore(settings.Settings));
        var expired=new CacheEntry(new Dictionary<string,string[]>(),[1],DateTimeOffset.UtcNow,0,TimeSpan.Zero);
        var result=await store.PutAsync(new(new string('a',64)),expired,default);
        Assert.Equal(CacheWriteKind.Bypass,result.Kind);
        Assert.Equal(0,store.Writes);
    }
    [Fact]
    public async Task StoreDeadlineKeepsReadUnavailableAndWriteBypassDistinctWithoutRetry()
    {
        using var settings=new CacheEligibilityTests.SettingsFixture();var executor=new StalledExecutor();
        using var store=new CachePipelineFixture.ObservedStore(new RedisResponseCacheStore(executor,settings.Settings));
        var key=new CacheLookupKey(new string('b',64));
        Assert.Equal(CacheReadKind.Unavailable,(await store.GetAsync(key,default)).Kind);
        var entry=new CacheEntry(new Dictionary<string,string[]>(),[1],DateTimeOffset.UtcNow,0,TimeSpan.FromSeconds(60));
        Assert.Equal(CacheWriteKind.Bypass,(await store.PutAsync(key,entry,default)).Kind);
        Assert.Equal(0,store.Writes);Assert.Equal(1,store.WriteAttempts);Assert.Equal(2,executor.Calls);
        Assert.Collection(store.Operations,
            read=>{Assert.Equal("read",read.Operation);Assert.Equal("Unavailable",read.Outcome);},
            write=>{Assert.Equal("write",write.Operation);Assert.Equal("Bypass",write.Outcome);});
    }
    private sealed class StalledExecutor:IResponseCacheExecutor
    {
        internal int Calls;
        public Task<string[]> ExecuteAsync(string operation,string prefix,CacheLookupKey key,string? payload,long maxBytes,int maxEntries,int ttlMs,CancellationToken ct)
        {Interlocked.Increment(ref Calls);return new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously).Task;}
    }
}

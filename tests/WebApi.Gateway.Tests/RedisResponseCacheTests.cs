using StackExchange.Redis;
using WebApi.Gateway.Policies;
using Xunit;
namespace WebApi.Gateway.Tests;
[CollectionDefinition("Response cache Redis", DisableParallelization = true)]
public sealed class ResponseCacheRedisCollection { }

// Isolate exact-budget positive controls from unrelated Redis/host test load.
// The two clients and the 2100 competing writes below still run concurrently.
[Collection("Response cache Redis")]
public sealed class RedisResponseCacheTests
{
    private static CacheLookupKey Key(int i) => new(i.ToString("x64"));
    private static CacheEntry Entry(int bytes = 100) => new(new Dictionary<string,string[]> { ["Content-Type"] = ["application/json"], ["Cache-Control"] = ["public,max-age=60"] }, new byte[bytes], DateTimeOffset.UtcNow, 0, TimeSpan.FromSeconds(60));
    private static string Wire(string prefix,string suffix) => prefix + ":{cache}:" + suffix;
    private static async Task Cleanup(ConnectionMultiplexer mux,string prefix)
    { var server = mux.GetServer(mux.GetEndPoints().Single()); var keys = server.Keys(pattern: prefix + ":*").ToArray(); if (keys.Length > 0) await mux.GetDatabase().KeyDeleteAsync(keys); }
    private static async Task WarmConnection(IResponseCacheStore store)
    {
        // The production decision budget also covers lazy connection startup.
        // Establish readiness with reads before asserting a write must be stored;
        // never retry the write or relax the runtime timeout for these assertions.
        var deadline=DateTime.UtcNow.AddSeconds(10);
        do { if((await store.GetAsync(Key(0),default)).Kind==CacheReadKind.Miss)return;await Task.Delay(25); } while(DateTime.UtcNow<deadline);
        throw new TimeoutException("Redis positive-control connection did not become available.");
    }
    [Fact]
    public async Task TwoNodesReadSameCompleteEntryAndRejectCorruptPayload()
    {
        using var f = new CacheEligibilityTests.SettingsFixture(); using var mux = await ConnectionMultiplexer.ConnectAsync("redis:6379"); using var a = new RedisResponseCacheStore(f.Settings); using var b = new RedisResponseCacheStore(f.Settings);
        try { await WarmConnection(a); await WarmConnection(b); Assert.Equal(CacheReadKind.Miss, (await a.GetAsync(Key(1), default)).Kind); Assert.Equal(CacheWriteKind.Stored, (await a.PutAsync(Key(1), Entry(), default)).Kind); var result = await b.GetAsync(Key(1), default); Assert.Equal(CacheReadKind.Hit, result.Kind); Assert.Equal(new byte[100], result.Entry!.Body); await mux.GetDatabase().StringSetAsync(Wire(f.Prefix, "entry:" + Key(1).Opaque), "corrupt-json"); Assert.Equal(CacheReadKind.Unavailable, (await a.GetAsync(Key(1), default)).Kind); } finally { await Cleanup(mux, f.Prefix); }
    }
    [Theory] [InlineData(100)] [InlineData(40000)]
    public async Task ConcurrentWritesCannotExceed64MiBOr2000Entries(int bodyBytes)
    {
        using var f = new CacheEligibilityTests.SettingsFixture(); using var mux = await ConnectionMultiplexer.ConnectAsync("redis:6379"); using var a = new RedisResponseCacheStore(f.Settings); using var b = new RedisResponseCacheStore(f.Settings);
        try
        {
            await WarmConnection(a); await WarmConnection(b);
            Assert.Equal(CacheWriteKind.Stored,(await a.PutAsync(Key(0),Entry(bodyBytes),default)).Kind);
            var results = await Task.WhenAll(Enumerable.Range(1, 2100).Select(i => (i % 2 == 0 ? a : b).PutAsync(Key(i), Entry(bodyBytes), default).AsTask())); Assert.All(results,r=>Assert.Contains(r.Kind,new[]{CacheWriteKind.Stored,CacheWriteKind.Bypass}));
            var stats = (RedisResult[])(await mux.GetDatabase().ScriptEvaluateAsync("local sum=0; local rows=redis.call('HGETALL',KEYS[2]); for i=2,#rows,2 do sum=sum+tonumber(rows[i]) end; return {tonumber(redis.call('GET',KEYS[1]) or '0'),redis.call('HLEN',KEYS[2]),redis.call('ZCARD',KEYS[3]),sum}", new RedisKey[] { Wire(f.Prefix,"bytes"),Wire(f.Prefix,"sizes"),Wire(f.Prefix,"expiry") }))!;
            Assert.InRange((long)stats[0], 1, 64L * 1024 * 1024); Assert.InRange((long)stats[1], 1, 2000); Assert.Equal((long)stats[1], (long)stats[2]); Assert.Equal((long)stats[0], (long)stats[3]);
        } finally { await Cleanup(mux, f.Prefix); }
    }
    private sealed class Stalled : IResponseCacheExecutor
    {
        internal int Calls;
        public Task<string[]> ExecuteAsync(string operation,string prefix,CacheLookupKey key,string? payload,long maxBytes,int maxEntries,int ttlMs,CancellationToken ct)
        { Interlocked.Increment(ref Calls); return new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously).Task; }
    }
    [Fact]
    public async Task TimeoutIsNotRetriedAndCorruptAccountingBypasses()
    {
        using var f = new CacheEligibilityTests.SettingsFixture(); var executor = new Stalled(); using var stalled = new RedisResponseCacheStore(executor, f.Settings);
        Assert.Equal(CacheWriteKind.Bypass, (await stalled.PutAsync(Key(1), Entry(), default)).Kind); Assert.Equal(1, executor.Calls);
        using var mux = await ConnectionMultiplexer.ConnectAsync("redis:6379"); using var store = new RedisResponseCacheStore(f.Settings);
        try { await WarmConnection(store); Assert.Equal(CacheWriteKind.Stored, (await store.PutAsync(Key(2), Entry(), default)).Kind); await mux.GetDatabase().StringSetAsync(Wire(f.Prefix, "bytes"), 1); Assert.Equal(CacheWriteKind.Bypass, (await store.PutAsync(Key(3), Entry(), default)).Kind); Assert.Equal(1, await mux.GetDatabase().HashLengthAsync(Wire(f.Prefix, "sizes"))); } finally { await Cleanup(mux, f.Prefix); }
    }
    [Fact]
    public async Task SweepNeverProcessesOver128()
    {
        using var f = new CacheEligibilityTests.SettingsFixture(); using var mux = await ConnectionMultiplexer.ConnectAsync("redis:6379"); using var store = new RedisResponseCacheStore(f.Settings); var db = mux.GetDatabase();
        try { await WarmConnection(store); for (var i = 1; i <= 200; i++) { await db.HashSetAsync(Wire(f.Prefix, "sizes"), Key(i).Opaque, 100); await db.SortedSetAddAsync(Wire(f.Prefix, "expiry"), Key(i).Opaque, 1); } await db.StringSetAsync(Wire(f.Prefix, "bytes"), 20000); await db.StringSetAsync(Wire(f.Prefix, "seal"), "v1"); Assert.Equal(CacheWriteKind.Stored, (await store.PutAsync(Key(201), Entry(), default)).Kind); Assert.Equal(73, await db.HashLengthAsync(Wire(f.Prefix, "sizes"))); Assert.Equal(73, await db.SortedSetLengthAsync(Wire(f.Prefix, "expiry"))); } finally { await Cleanup(mux, f.Prefix); }
    }
    [Fact]
    public async Task MissingQuotaMetadataWithLivePayloadBlocksNewWrites()
    {
        using var f = new CacheEligibilityTests.SettingsFixture(); using var mux = await ConnectionMultiplexer.ConnectAsync("redis:6379"); using var store = new RedisResponseCacheStore(f.Settings);
        try { await WarmConnection(store); Assert.Equal(CacheWriteKind.Stored, (await store.PutAsync(Key(1), Entry(), default)).Kind); await mux.GetDatabase().KeyDeleteAsync(new RedisKey[] { Wire(f.Prefix,"bytes"),Wire(f.Prefix,"sizes"),Wire(f.Prefix,"expiry") }); Assert.Equal(CacheWriteKind.Bypass, (await store.PutAsync(Key(2), Entry(), default)).Kind); } finally { await Cleanup(mux,f.Prefix); }
    }
    [Fact]
    public async Task ExpiredReadSweepPreservesAccountingExpiry()
    {
        using var f = new CacheEligibilityTests.SettingsFixture(); using var mux = await ConnectionMultiplexer.ConnectAsync("redis:6379"); using var store = new RedisResponseCacheStore(f.Settings); var db = mux.GetDatabase();
        try { await WarmConnection(store); await db.HashSetAsync(Wire(f.Prefix,"sizes"),Key(1).Opaque,100); await db.SortedSetAddAsync(Wire(f.Prefix,"expiry"),Key(1).Opaque,1); await db.StringSetAsync(Wire(f.Prefix,"bytes"),100); await db.StringSetAsync(Wire(f.Prefix,"seal"),"v1"); foreach (var name in new[] { "sizes","expiry","bytes","seal" }) await db.KeyExpireAsync(Wire(f.Prefix,name),TimeSpan.FromSeconds(30)); Assert.Equal(CacheReadKind.Miss,(await store.GetAsync(Key(1),default)).Kind); var ttl = await db.KeyTimeToLiveAsync(Wire(f.Prefix,"bytes")); Assert.NotNull(ttl); Assert.InRange(ttl.Value.TotalSeconds,1,30); } finally { await Cleanup(mux,f.Prefix); }
    }
    [Fact]
    public async Task EqualCountsWithDifferentIndexMembersRefuseBeforeAnyMutation()
    {
        using var f=new CacheEligibilityTests.SettingsFixture();using var mux=await ConnectionMultiplexer.ConnectAsync("redis:6379");using var store=new RedisResponseCacheStore(f.Settings);var db=mux.GetDatabase();
        try{
            await WarmConnection(store);
            Assert.Equal(CacheWriteKind.Stored,(await store.PutAsync(Key(1),Entry(),default)).Kind);
            var score=await db.SortedSetScoreAsync(Wire(f.Prefix,"expiry"),Key(1).Opaque);await db.SortedSetRemoveAsync(Wire(f.Prefix,"expiry"),Key(1).Opaque);await db.SortedSetAddAsync(Wire(f.Prefix,"expiry"),Key(2).Opaque,score!.Value);
            var before=await db.StringGetAsync(Wire(f.Prefix,"bytes"));
            Assert.Equal(CacheWriteKind.Bypass,(await store.PutAsync(Key(3),Entry(),default)).Kind);
            Assert.Equal(CacheReadKind.Unavailable,(await store.GetAsync(Key(1),default)).Kind);
            Assert.Equal(before,await db.StringGetAsync(Wire(f.Prefix,"bytes")));Assert.Equal(1,await db.HashLengthAsync(Wire(f.Prefix,"sizes")));Assert.Equal(1,await db.SortedSetLengthAsync(Wire(f.Prefix,"expiry")));Assert.False(await db.KeyExistsAsync(Wire(f.Prefix,"entry:"+Key(3).Opaque)));
        }finally{await Cleanup(mux,f.Prefix);}
    }

}

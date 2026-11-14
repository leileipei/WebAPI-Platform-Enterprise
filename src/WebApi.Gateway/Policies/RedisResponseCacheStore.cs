using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;
namespace WebApi.Gateway.Policies;
public sealed class RedisResponseCacheStore : IResponseCacheStore, IDisposable
{
    private readonly IResponseCacheExecutor executor;
    private readonly GatewayCacheSettings settings;
    private readonly IDisposable? owned;
    private readonly IResponseCacheDiagnosticSink? diagnostics;
    public RedisResponseCacheStore(GatewayCacheSettings settings,IResponseCacheDiagnosticSink? diagnostics=null)
    { this.settings = settings; this.diagnostics=diagnostics;var redis = new RedisExecutor(settings.RedisConnection); executor = redis; owned = redis; }
    public RedisResponseCacheStore(IResponseCacheExecutor executor, GatewayCacheSettings settings,IResponseCacheDiagnosticSink? diagnostics=null)
    { this.executor = executor; this.settings = settings;this.diagnostics=diagnostics; }
    public async ValueTask<CacheReadResult> GetAsync(CacheLookupKey key, CancellationToken ct)
    {
        var result = await Execute("get", key, null, 0, ct);
        if (result is ["miss"]) return new(CacheReadKind.Miss);
        if (result is not ["hit", var payload] || Encoding.UTF8.GetByteCount(payload) > settings.MaxEntryBytes) return new(CacheReadKind.Unavailable);
        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(payload, new JsonSerializerOptions { MaxDepth = 8 });
            if (entry is null || !Valid(entry)) return new(CacheReadKind.Unavailable);
            return DateTimeOffset.UtcNow - entry.ResponseAt < entry.FreshFor ? new(CacheReadKind.Hit, entry) : new(CacheReadKind.Miss);
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or OverflowException) { return new(CacheReadKind.Unavailable); }
    }
    public async ValueTask<CacheWriteResult> PutAsync(CacheLookupKey key, CacheEntry entry, CancellationToken ct)
    {
        if (!Valid(entry)) return new(CacheWriteKind.Bypass);
        var remaining = entry.FreshFor - (DateTimeOffset.UtcNow - entry.ResponseAt);
        if (remaining <= TimeSpan.Zero) return new(CacheWriteKind.Bypass);
        var payload = JsonSerializer.Serialize(entry);
        if (Encoding.UTF8.GetByteCount(payload) > settings.MaxEntryBytes) return new(CacheWriteKind.Bypass);
        var result = await Execute("put", key, payload, Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds), 1, 3600000), ct);
        return new(result is ["stored"] ? CacheWriteKind.Stored : CacheWriteKind.Bypass);
    }
    private bool Valid(CacheEntry entry)
    {
        if (entry.Body is null || entry.Headers is null || entry.Headers.Count > 9 || entry.Body.Length > settings.MaxEntryBytes
            || !double.IsFinite(entry.InitialAge) || entry.InitialAge < 0 || entry.FreshFor <= TimeSpan.Zero || entry.FreshFor > TimeSpan.FromHours(1)
            || entry.ResponseAt > DateTimeOffset.UtcNow.AddSeconds(1)) return false;
        long bytes = entry.Body.Length;
        foreach (var header in entry.Headers)
        {
            if (!CacheEligibility.StoredHeaderNames.Contains(header.Key) || header.Value is null || header.Value.Length > 8) return false;
            foreach (var value in header.Value)
            { if (value is null || value.Contains('\r') || value.Contains('\n')) return false; bytes += Encoding.UTF8.GetByteCount(value) + Encoding.UTF8.GetByteCount(header.Key); if (bytes > settings.MaxEntryBytes) return false; }
        }
        return true;
    }
    private async Task<string[]?> Execute(string operation, CacheLookupKey key, string? payload, int ttl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (key.Opaque.Length != 64 || key.Opaque.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(settings.DecisionTimeoutMs);
        var observation=diagnostics is null?null:new ResponseCacheDiagnosticOperation(diagnostics,operation,ct,timeout.Token);
        try
        {
            var pending=executor is RedisExecutor redis && observation is not null
                ?redis.ExecuteObservedAsync(operation,settings.KeyPrefix,key,payload,settings.MaxTotalBytes,settings.MaxEntries,ttl,timeout.Token,observation)
                :executor.ExecuteAsync(operation,settings.KeyPrefix,key,payload,settings.MaxTotalBytes,settings.MaxEntries,ttl,timeout.Token);
            var result=await pending.WaitAsync(timeout.Token);observation?.Finish();return result;
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested) { observation?.Finish(error);return null; }
        catch (Exception error) when (error is RedisException or TimeoutException or SocketException or InvalidOperationException or IOException) { observation?.Finish(error);return null; }
        catch (Exception error) {observation?.Finish(error);throw;}
    }
    public void Dispose() => owned?.Dispose();
    private sealed class RedisExecutor(string connection) : IResponseCacheExecutor, IDisposable
    {
        private readonly Lazy<Task<ConnectionMultiplexer>> mux = new(() => ConnectionMultiplexer.ConnectAsync(connection), LazyThreadSafetyMode.ExecutionAndPublication);
        public Task<string[]> ExecuteAsync(string operation,string prefix,CacheLookupKey key,string? payload,long maxBytes,int maxEntries,int ttlMs,CancellationToken ct)
            =>ExecuteObservedAsync(operation,prefix,key,payload,maxBytes,maxEntries,ttlMs,ct,null);
        internal async Task<string[]> ExecuteObservedAsync(string operation,string prefix,CacheLookupKey key,string? payload,long maxBytes,int maxEntries,int ttlMs,CancellationToken ct,ResponseCacheDiagnosticOperation? observation)
        {
            var connected=observation is null?await mux.Value.WaitAsync(ct):await observation.Observe(ResponseCachePhase.Connect,()=>mux.Value.WaitAsync(ct));
            ct.ThrowIfCancellationRequested();var root=prefix+":{cache}:";
            Task<RedisResult> Command()=>connected.GetDatabase().ScriptEvaluateAsync(ResponseCacheScripts.Text,
                new RedisKey[] { root + "bytes", root + "sizes", root + "expiry", root + "entry:" + key.Opaque, root + "seal" },
                new RedisValue[] { operation, key.Opaque, maxBytes, maxEntries, ttlMs, root + "entry:", payload ?? "" });
            var result=observation is null?await Command():await observation.Observe(ResponseCachePhase.Command,Command);
            return ((RedisResult[])result!).Select(v => (string)v!).ToArray();
        }
        public void Dispose() { if (mux.IsValueCreated) _ = mux.Value.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default); }
    }
}

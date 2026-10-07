namespace WebApi.Gateway.Policies;
public sealed record CacheEntry(IReadOnlyDictionary<string,string[]> Headers, byte[] Body, DateTimeOffset ResponseAt, double InitialAge, TimeSpan FreshFor);
public enum CacheReadKind { Hit, Miss, Unavailable }
public enum CacheWriteKind { Stored, Bypass }
public sealed record CacheReadResult(CacheReadKind Kind, CacheEntry? Entry = null);
public sealed record CacheWriteResult(CacheWriteKind Kind);
public interface IResponseCacheStore
{
    ValueTask<CacheReadResult> GetAsync(CacheLookupKey key,CancellationToken ct);
    ValueTask<CacheWriteResult> PutAsync(CacheLookupKey key,CacheEntry entry,CancellationToken ct);
}
public interface IResponseCacheExecutor
{
    Task<string[]> ExecuteAsync(string operation,string prefix,CacheLookupKey key,string? payload,long maxBytes,int maxEntries,int ttlMs,CancellationToken ct);
}

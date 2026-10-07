using System.Text.Json;
using WebApi.Contracts.Policies;
using static WebApi.Domain.Policies.PolicyConfigurationValidator;

namespace WebApi.Domain.Policies;

internal static class CachePolicyConfiguration
{
    internal static CacheConfiguration Parse(JsonElement root)
    {
        Fields(Names(root), "ttlSeconds", "maxEntryBytes", "varyHeaders", "identityPartition", "redisFailureMode");
        var ttl = Integer(root, "ttlSeconds", 1, 3600);
        var bytes = Integer(root, "maxEntryBytes", 1024, 1048576);
        var vary = StringArray(root, "varyHeaders", 0, 8, 32768, StringComparer.OrdinalIgnoreCase);
        foreach (var header in vary)
        {
            if (!header.All(IsTokenChar) || header.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                || header.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || header.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase)
                || header.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("X-WebApi-", StringComparison.OrdinalIgnoreCase)) throw Invalid();
        }
        var partition = Text(root, "identityPartition"); var failure = Text(root, "redisFailureMode");
        if (partition != "VerifiedIdentity" || failure != "Bypass") throw Invalid();
        return new(ttl, bytes, vary, partition, failure);
    }
    private static bool IsTokenChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || "!#$%&'*+-.^_`|~".Contains(c);
}

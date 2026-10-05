using System.Text;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;

namespace WebApi.Domain.Policies;

public static class PolicyConfigurationValidator
{
    private static ApiException Invalid() => new(422, "invalid_policy_config", "策略配置字段、类型或范围不合法。");

    public static string Normalize(string type, string config)
    {
        if (config is null || Encoding.UTF8.GetByteCount(config) > 32768) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(config, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject()) if (!names.Add(field.Name)) throw Invalid();
            object normalized = type switch
            {
                "authentication" => Authentication(root, names),
                "timeout" => Timeout(root, names),
                "rate_limit" => Rate(root, names),
                "circuit_breaker" => Circuit(root, names),
                _ => throw Invalid()
            };
            return Encoding.UTF8.GetString(CanonicalJson.Serialize(normalized));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw Invalid();
        }
    }

    public static RateLimitConfiguration ParseRate(string config) =>
        JsonSerializer.Deserialize<RateLimitConfiguration>(Normalize("rate_limit", config), CanonicalJson.Options)!;
    public static CircuitBreakerConfiguration ParseCircuit(string config) =>
        JsonSerializer.Deserialize<CircuitBreakerConfiguration>(Normalize("circuit_breaker", config), CanonicalJson.Options)!;

    private static void Fields(HashSet<string> actual, params string[] expected)
    {
        if (!actual.SetEquals(expected)) throw Invalid();
    }
    private static string Text(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) throw Invalid();
        return value.GetString()!;
    }
    private static int Integer(JsonElement root, string key, int min, int max)
    {
        if (!root.TryGetProperty(key, out var value) || !value.TryGetInt32(out var number) || number < min || number > max) throw Invalid();
        return number;
    }
    private static bool Boolean(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        return value.GetBoolean();
    }
    private static object Authentication(JsonElement root, HashSet<string> names)
    {
        Fields(names, "mode"); var mode = Text(root, "mode");
        if (mode is not ("ApiKey" or "Anonymous")) throw Invalid();
        return new { mode };
    }
    private static object Timeout(JsonElement root, HashSet<string> names)
    {
        Fields(names, "timeoutMs"); return new { timeoutMs = Integer(root, "timeoutMs", 1, 300000) };
    }
    private static RateLimitConfiguration Rate(JsonElement root, HashSet<string> names)
    {
        Fields(names, "algorithm", "keyBy", "refillTokens", "windowMs", "burst", "redisFailureMode");
        var algorithm = Text(root, "algorithm"); var keyBy = Text(root, "keyBy"); var failure = Text(root, "redisFailureMode");
        if (algorithm != "TokenBucket" || keyBy is not ("Route" or "ApplicationRoute") || failure is not ("Reject" or "Allow")) throw Invalid();
        var refill = Integer(root, "refillTokens", 1, 1000000); var window = Integer(root, "windowMs", 10, 600000); var burst = Integer(root, "burst", 1, 1000000);
        if (burst < refill || (long)burst * window > (long)refill * 3600000) throw Invalid();
        return new(algorithm, keyBy, refill, window, burst, failure);
    }
    private static CircuitBreakerConfiguration Circuit(JsonElement root, HashSet<string> names)
    {
        Fields(names, "samplingWindowMs", "minimumRequests", "failureRatio", "openDurationMs", "halfOpenMaxRequests", "halfOpenSuccesses", "failureStatusCodes", "countTimeouts", "countConnectionFailures");
        var window = Integer(root, "samplingWindowMs", 1000, 300000);
        if (window % 1000 != 0 || !root.GetProperty("failureRatio").TryGetDouble(out var ratio) || !double.IsFinite(ratio) || ratio <= 0 || ratio > 1) throw Invalid();
        var statuses = root.GetProperty("failureStatusCodes");
        if (statuses.ValueKind != JsonValueKind.Array || statuses.GetArrayLength() > 100) throw Invalid();
        var codes = new HashSet<int>();
        foreach (var value in statuses.EnumerateArray()) if (!value.TryGetInt32(out var code) || code is < 500 or > 599 || !codes.Add(code)) throw Invalid();
        var timeout = Boolean(root, "countTimeouts"); var connect = Boolean(root, "countConnectionFailures");
        if (codes.Count == 0 && !timeout && !connect) throw Invalid();
        return new(window, Integer(root, "minimumRequests", 1, 1000000), ratio,
            Integer(root, "openDurationMs", 1000, 300000), Integer(root, "halfOpenMaxRequests", 1, 10),
            Integer(root, "halfOpenSuccesses", 1, 100), Array.AsReadOnly(codes.Order().ToArray()), timeout, connect);
    }
}

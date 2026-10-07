using System.Text.Json;
using WebApi.Contracts.Policies;
using static WebApi.Domain.Policies.PolicyConfigurationValidator;

namespace WebApi.Domain.Policies;

internal static class RetryPolicyConfiguration
{
    internal static RetryConfiguration Parse(JsonElement root)
    {
        Fields(Names(root), "maxAttempts", "perAttemptTimeoutMs", "baseDelayMs", "maxDelayMs", "jitterPercent", "retryStatusCodes", "retryConnectionFailures");
        var attempts = Integer(root, "maxAttempts", 1, 3);
        var perAttempt = Integer(root, "perAttemptTimeoutMs", 10, 300000);
        var baseDelay = Integer(root, "baseDelayMs", 0, 1000);
        var maxDelay = Integer(root, "maxDelayMs", 0, 5000);
        var jitter = Integer(root, "jitterPercent", 0, 100);
        if (maxDelay < baseDelay) throw Invalid();
        var source = root.GetProperty("retryStatusCodes");
        if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() > 3) throw Invalid();
        var codes = new HashSet<int>();
        foreach (var value in source.EnumerateArray())
            if (!value.TryGetInt32(out var code) || code is not (502 or 503 or 504) || !codes.Add(code)) throw Invalid();
        var connectionFailures = Boolean(root, "retryConnectionFailures");
        if (attempts != 1 && codes.Count == 0 && !connectionFailures) throw Invalid();
        return new(attempts, perAttempt, baseDelay, maxDelay, jitter, Array.AsReadOnly(codes.Order().ToArray()), connectionFailures);
    }
}

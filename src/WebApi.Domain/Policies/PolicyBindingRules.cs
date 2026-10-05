using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;

namespace WebApi.Domain.Policies;

public static class PolicyBindingRules
{
    public static EffectiveRoutePolicies Validate(IReadOnlyList<PolicyBindingConfiguration> bindings, bool defaultRequireApiKey)
    {
        if (bindings is null || bindings.Count > 4 || bindings.Select(b => b.Type).Distinct().Count() != bindings.Count || bindings.Select(b => b.PolicyId).Distinct().Count() != bindings.Count)
            throw new ApiException(422, "invalid_policy_binding", "同一路由每种类型只能绑定一个策略。");
        var requireKey = defaultRequireApiKey; int? timeout = null; RateLimitConfiguration? rate = null; CircuitBreakerConfiguration? circuit = null;
        foreach (var binding in bindings)
        {
            if (binding.PolicyId == Guid.Empty || binding.Priority is < 0 or > 1000) throw new ApiException(422, "invalid_policy_binding", "策略身份或优先级不合法。");
            var source = PolicyConfigurationValidator.Normalize(binding.Type, binding.Config);
            if (!binding.Enabled) continue;
            using var config = JsonDocument.Parse(source);
            switch (binding.Type)
            {
                case "authentication": requireKey = config.RootElement.GetProperty("mode").GetString() == "ApiKey"; break;
                case "timeout": timeout = config.RootElement.GetProperty("timeoutMs").GetInt32(); break;
                case "rate_limit": rate = JsonSerializer.Deserialize<RateLimitConfiguration>(source, CanonicalJson.Options); break;
                case "circuit_breaker": circuit = JsonSerializer.Deserialize<CircuitBreakerConfiguration>(source, CanonicalJson.Options); break;
            }
        }
        if (!requireKey && rate?.KeyBy == "ApplicationRoute") throw new ApiException(422, "anonymous_application_rate_limit", "匿名路由不能按应用限流，请选择Route维度。");
        return new(requireKey, timeout, rate, circuit);
    }
}

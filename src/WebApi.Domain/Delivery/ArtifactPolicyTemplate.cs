using System.Text;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Domain.Policies;

namespace WebApi.Domain.Delivery;

public static class ArtifactPolicyTemplates
{
    private static ApiException Invalid() => new(422, "invalid_artifact_policy", "制品策略模板或目标环境参数不合法。");

    public static ArtifactPolicyTemplate Split(string type, string normalizedConfig)
    {
        using var doc = JsonDocument.Parse(PolicyConfigurationValidator.Normalize(type, normalizedConfig));
        var fields = EnvironmentFields(type, doc.RootElement);
        var frozen = doc.RootElement.EnumerateObject().Where(p => !fields.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return Normalize(new(type, Encoding.UTF8.GetString(CanonicalJson.Serialize(frozen)), fields));
    }

    public static string Resolve(ArtifactPolicyTemplate template, JsonElement targetParameters)
    {
        var checkedTemplate = Normalize(template);
        if (targetParameters.ValueKind != JsonValueKind.Object) throw Invalid();
        var properties = targetParameters.EnumerateObject().ToArray();
        if (properties.Length != checkedTemplate.EnvironmentFields.Count ||
            !properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(checkedTemplate.EnvironmentFields)) throw Invalid();
        using var frozen = JsonDocument.Parse(checkedTemplate.FrozenConfig);
        var merged = frozen.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        foreach (var p in properties) merged.Add(p.Name, p.Value.Clone());
        var resolved = PolicyConfigurationValidator.Normalize(template.Type, Encoding.UTF8.GetString(CanonicalJson.Serialize(merged)));
        var roundTrip = Split(template.Type, resolved);
        if (roundTrip.FrozenConfig != checkedTemplate.FrozenConfig) throw Invalid();
        return resolved;
    }

    public static ArtifactPolicyTemplate Normalize(ArtifactPolicyTemplate template)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(template.FrozenConfig) > 32768) throw Invalid();
            using var doc = JsonDocument.Parse(template.FrozenConfig, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var env = EnvironmentFields(template.Type, doc.RootElement);
            if (template.EnvironmentFields.Count != env.Length || !template.EnvironmentFields.ToHashSet(StringComparer.Ordinal).SetEquals(env)) throw Invalid();
            var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            string[]? frozenNames = template.Type switch
            {
                "timeout" => [],
                "rate_limit" => ["algorithm", "keyBy", "redisFailureMode"],
                "authentication" when env.Length > 0 => ["mode", "allowedAlgorithms", "allowedTokenTypes", "clockSkewSeconds", "maxTokenLifetimeSeconds", "applicationClaim", "forwardBearer"],
                _ => null
            };
            if (frozenNames is not null && (names.Length != frozenNames.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(frozenNames))) throw Invalid();
            var values = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            if (template.Type == "authentication" && env.Length > 0)
                foreach (var name in new[] { "allowedAlgorithms", "allowedTokenTypes" })
                    values[name] = JsonSerializer.SerializeToElement(values[name].EnumerateArray().Select(v => v.GetString()).Order(StringComparer.Ordinal).ToArray());
            var json = env.Length == 0 ? PolicyConfigurationValidator.Normalize(template.Type, template.FrozenConfig) : Encoding.UTF8.GetString(CanonicalJson.Serialize(values));
            return template with { FrozenConfig = json, EnvironmentFields = env };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { throw Invalid(); }
    }

    private static string[] EnvironmentFields(string type, JsonElement config) => type switch
    {
        "timeout" => ["timeoutMs"],
        "rate_limit" => ["burst", "refillTokens", "windowMs"],
        "authentication" when config.GetProperty("mode").GetString() == "JWT" => ["applicationMappings", "audiences", "issuer", "jwks"],
        "authentication" or "circuit_breaker" or "retry" or "cache" => [],
        _ => throw Invalid()
    };
}

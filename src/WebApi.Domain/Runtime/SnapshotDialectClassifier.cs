using System.Text.Json;

namespace WebApi.Domain.Runtime;

public enum SnapshotDialect
{
    Invalid,
    Legacy,
    Network23,
    Governance23,
    Governance24
}

/// <summary>Classifies wire shape only; it does not establish an environment's security authority.</summary>
public static class SnapshotDialectClassifier
{
    private static readonly string[] GovernanceFields =
    [
        "requiredSecurityGeneration",
        "requiredSecurityEpoch",
        "requireEnvironmentCredentials",
        "minimumSnapshotSchema"
    ];

    public static SnapshotDialect Classify(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 16 * 1024 * 1024) return SnapshotDialect.Invalid;
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray());
            return Classify(document.RootElement);
        }
        catch (JsonException)
        {
            return SnapshotDialect.Invalid;
        }
    }

    internal static SnapshotDialect Classify(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return SnapshotDialect.Invalid;

        string? schema = null;
        var schemaCount = 0;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Name != "schemaVersion" || ++schemaCount != 1 || property.Value.ValueKind != JsonValueKind.String)
                    return SnapshotDialect.Invalid;
                schema = property.Value.GetString();
            }

            var field = GovernanceFields.FirstOrDefault(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
            if (field is not null && (property.Name != field || !present.Add(field))) return SnapshotDialect.Invalid;
        }

        if (schemaCount != 1) return SnapshotDialect.Invalid;
        if (schema is "2.0" or "2.1" or "2.2")
            return present.Count == 0 && !HasLegacyGovernanceFields(root) ? SnapshotDialect.Legacy : SnapshotDialect.Invalid;
        if (schema == "2.3")
            return present.Count == 0 ? (!HasLegacyGovernanceFields(root) ? SnapshotDialect.Network23 : SnapshotDialect.Invalid) :
                present.Count == GovernanceFields.Length ? SnapshotDialect.Governance23 : SnapshotDialect.Invalid;
        if (schema == "2.4" && present.Count == GovernanceFields.Length) return SnapshotDialect.Governance24;
        return SnapshotDialect.Invalid;
    }

    private static bool HasLegacyGovernanceFields(JsonElement root)
    {
        if (!TryArray(root, "routes", out var routes) || !TryArray(root, "applications", out var applications)) return true;
        foreach (var route in routes.EnumerateArray())
            if (HasAny(route, "authorizationRevision", "environmentId")) return true;

        foreach (var app in applications.EnumerateArray())
        {
            if (HasAny(app, "environmentId") || !TryArray(app, "credentials", out var credentials) ||
                !TryArray(app, "permissions", out var permissions)) return true;
            foreach (var credential in credentials.EnumerateArray())
                if (HasAny(credential, "bindingMode", "environmentId")) return true;
            foreach (var permission in permissions.EnumerateArray())
                if (HasAny(permission, "mode", "routeMethods", "environmentId")) return true;
        }
        return false;
    }

    private static bool HasAny(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return true;
        return value.EnumerateObject().Any(property => names.Any(name => name.Equals(property.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryArray(JsonElement value, string name, out JsonElement array)
    {
        array = default;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var matches = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            array = property.Value;
            matches++;
        }
        return matches == 1 && array.ValueKind == JsonValueKind.Array;
    }
}

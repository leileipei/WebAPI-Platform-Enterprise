using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Gateway;

public static class SecurityProtocolCapabilities
{
    private const string Ledger = "security-ledger-v1";
    private const string InstanceKey = "securityCapabilityInstanceId";
    private const string CapabilitiesKey = "securityCapabilities";

    public static bool SupportsLedger(GatewayNode node)
    {
        try
        {
            if (!Guid.TryParse(node.InstanceId, out var id) || id == Guid.Empty) return false;
            using var document = JsonDocument.Parse(node.Metadata ?? "{}");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasUnambiguousKeys(root)) return false;
            if (!root.TryGetProperty(InstanceKey, out var instance) || instance.ValueKind != JsonValueKind.String || instance.GetString() != node.InstanceId) return false;
            if (!root.TryGetProperty(CapabilitiesKey, out var capabilities) || capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() > 4) return false;
            var found = false;
            foreach (var item in capabilities.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() != Ledger) return false;
                found = true;
            }
            return found;
        }
        catch (JsonException) { return false; }
    }

    public static string Merge(string? metadata, Guid instance, IReadOnlyList<string>? capabilities)
    {
        var values = capabilities ?? [];
        if (instance == Guid.Empty) throw new ApiException(422, "invalid_node_instance", "节点实例不能为空。");
        if (values.Count > 4 || values.Any(value => value != Ledger)) throw new ApiException(422, "invalid_security_capabilities", "节点安全协议能力不合法。");
        try
        {
            using var parsed = JsonDocument.Parse(metadata ?? "{}");
            if (parsed.RootElement.ValueKind != JsonValueKind.Object || !HasUnambiguousKeys(parsed.RootElement)) throw new JsonException();
            var root = JsonNode.Parse(metadata ?? "{}") as JsonObject ?? throw new JsonException();
            root[InstanceKey] = instance.ToString();
            root[CapabilitiesKey] = JsonSerializer.SerializeToNode(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            return root.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new ApiException(422, "invalid_node_metadata", "节点元数据不可读。");
        }
    }

    public static string? ClearReceipt(string? metadata)
    {
        if (metadata is null) return null;
        try
        {
            var root = JsonNode.Parse(metadata) as JsonObject ?? throw new JsonException();
            root.Remove("securityApplication");
            return root.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new ApiException(422, "invalid_node_metadata", "节点元数据不可读。");
        }
    }

    private static bool HasUnambiguousKeys(JsonElement root)
    {
        var instanceCount = 0;
        var capabilitiesCount = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(InstanceKey, StringComparison.OrdinalIgnoreCase))
            {
                if (property.Name != InstanceKey || ++instanceCount > 1) return false;
            }
            if (property.Name.Equals(CapabilitiesKey, StringComparison.OrdinalIgnoreCase))
            {
                if (property.Name != CapabilitiesKey || ++capabilitiesCount > 1) return false;
            }
        }
        return true;
    }
}

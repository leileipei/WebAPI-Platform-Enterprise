using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Gateway;

internal static class NodeDiagnosticReport
{
    private const string Key = "processDiagnostics";

    internal static NodeDiagnosticsView? Read(GatewayNode node)
    {
        if (node.Metadata == null) return null;
        try
        {
            using var data = JsonDocument.Parse(node.Metadata);
            if (!data.RootElement.TryGetProperty(Key, out var field)) return null;
            var report = field.Deserialize<NodeDiagnosticsView>(CanonicalJson.Options);
            return report?.InstanceId.ToString() == node.InstanceId ? report : null;
        }
        catch(JsonException) { return null; }
    }

    internal static string? Clear(string? metadata)
    {
        if (metadata == null) return null;
        var root = JsonNode.Parse(metadata)!.AsObject();
        root.Remove(Key);
        return root.ToJsonString();
    }

    internal static void Update(GatewayNode node, NodeHeartbeat heartbeat)
    {
        if (heartbeat.Diagnostics is not { } report) return;
        if (report.CpuPercent is < 0 or > 100 || report.WorkingSetBytes < 0 || report.LastKnownGoodVersion is <= 0)
            throw new ApiException(422, "invalid_diagnostics", "节点资源采样值无效。");
        var root = node.Metadata == null ? new JsonObject() : JsonNode.Parse(node.Metadata)!.AsObject();
        root[Key] = JsonSerializer.SerializeToNode(new NodeDiagnosticsView(heartbeat.InstanceId, DateTimeOffset.UtcNow,
            report.CpuPercent, report.WorkingSetBytes, report.LastKnownGoodVersion), CanonicalJson.Options);
        node.Metadata = root.ToJsonString();
    }
}

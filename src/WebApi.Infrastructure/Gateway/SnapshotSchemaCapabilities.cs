using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Releases;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Gateway;
public static class SnapshotSchemaCapabilities
{
    private static string[] Normalize(IReadOnlyList<string>? schemas)
    {if(schemas is null) return ["2.0"];if(schemas.Count is <1 or >4||schemas.Any(s=>s is not ("2.0" or "2.1" or "2.2"))) throw new ApiException(422,"invalid_snapshot_capabilities","节点协议能力不合法。");return schemas.Distinct().Order(StringComparer.Ordinal).ToArray();}
    public static IReadOnlyList<string> Read(GatewayNode node)
    {
        try {
            if(node.Metadata is null) return ["2.0"];using var document=JsonDocument.Parse(node.Metadata);var root=document.RootElement;
            if(!root.TryGetProperty("snapshotSchemaInstanceId",out var instance)||instance.GetString()!=node.InstanceId||!root.TryGetProperty("supportedSnapshotSchemas",out var schemas)||schemas.ValueKind!=JsonValueKind.Array) return ["2.0"];
            return Normalize(schemas.EnumerateArray().Select(s=>s.GetString()!).ToArray());
        }catch(Exception ex) when(ex is JsonException or InvalidOperationException or ApiException) {return ["2.0"];}
    }
    public static string Merge(string? metadata,Guid instanceId,IReadOnlyList<string>? schemas)
    {
        var normalized=Normalize(schemas);if(instanceId==Guid.Empty) throw new ApiException(422,"invalid_node_instance","节点实例不能为空。");
        JsonObject root;try {root=metadata is null?new():JsonNode.Parse(metadata) as JsonObject??throw new JsonException();}catch(JsonException) {throw new ApiException(422,"invalid_node_metadata","节点元数据不可读。");}
        root["snapshotSchemaInstanceId"]=instanceId.ToString();root["supportedSnapshotSchemas"]=JsonSerializer.SerializeToNode(normalized);return root.ToJsonString();
    }
    public static async Task RequireOnline22Async(WebApiDbContext db,PublishSettings settings,Guid environmentId,CancellationToken ct)
    {
        var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==environmentId&&n.Enabled).OrderBy(n=>n.Id).ToArrayAsync(ct);
        var threshold=DateTimeOffset.UtcNow.AddSeconds(-settings.HeartbeatGraceSeconds);
        if(nodes.Length<settings.MinimumNodes||nodes.Any(n=>n.IdentityHash.Length!=64||!Guid.TryParse(n.InstanceId,out _)||n.LastHeartbeatAt is null||n.LastHeartbeatAt<threshold))
            throw new ApiException(409,"gateway_cohort_unavailable","至少需要两个有效注册节点，且全部已启用节点必须在线；不会缩减确认目标。");
        RequireSupported(nodes,"2.2");
    }
    public static void RequireSupported(IReadOnlyList<GatewayNode> nodes,string schema)
    {if(schema is not ("2.0" or "2.1" or "2.2")||nodes.Any(n=>!Read(n).Contains(schema))) throw new ApiException(409,"gateway_schema_unsupported","全部已启用目标节点必须声明支持目标快照协议。");}
}

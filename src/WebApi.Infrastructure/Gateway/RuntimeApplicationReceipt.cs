using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Gateway;

// Current-instance evidence is separate from immutable release-target acknowledgements.
public static class RuntimeApplicationReceipt
{
    private static JsonObject Read(string? metadata)
    {
        try {return metadata is null?new():JsonNode.Parse(metadata) as JsonObject??throw new JsonException();}
        catch(JsonException) {throw new ApiException(422,"invalid_node_metadata","节点元数据不可读。");}
    }
    public static string? Clear(string? metadata)
    {if(metadata is null) return null;var root=Read(metadata);root.Remove("runtimeApplication");return root.ToJsonString();}
    internal static bool Matches(GatewayNode node, SnapshotEnvelope envelope)
    {
        if (node.Metadata is null) return false;
        try
        {
            using var metadata = JsonDocument.Parse(node.Metadata);
            return metadata.RootElement.TryGetProperty("runtimeApplication", out var receipt)
                && receipt.GetProperty("schemaVersion").GetInt32() == 1
                && receipt.GetProperty("instanceId").GetString() == node.InstanceId
                && receipt.GetProperty("releaseId").GetString() == envelope.ReleaseId.ToString()
                && receipt.GetProperty("configVersion").GetInt64() == envelope.ConfigVersion
                && receipt.GetProperty("deploymentSequence").GetInt64() == envelope.DeploymentSequence
                && receipt.GetProperty("payloadHash").GetString() == envelope.PayloadHash;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return false; }
    }

    public static bool Record(GatewayNode node,NodeAck ack)
    {
        var root=Read(node.Metadata);var receipt=new JsonObject {
            ["schemaVersion"]=1,["instanceId"]=ack.InstanceId.ToString(),["releaseId"]=ack.ReleaseId.ToString(),
            ["configVersion"]=ack.ConfigVersion,["deploymentSequence"]=ack.DeploymentSequence,["payloadHash"]=ack.PayloadHash
        };
        var existing=root["runtimeApplication"] as JsonObject;
        var duplicate=existing is not null&&receipt.All(p=>JsonNode.DeepEquals(p.Value,existing[p.Key]));
        if(!duplicate) {receipt["appliedAt"]=ack.AppliedAt.ToString("O");root["runtimeApplication"]=receipt;node.Metadata=root.ToJsonString();}
        return duplicate;
    }
}

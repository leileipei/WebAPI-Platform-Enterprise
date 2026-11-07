using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Gateway;
public sealed record CurrentSecurityReceipt(Guid NodeId,Guid EnvironmentId,Guid InstanceId,Guid SecurityGeneration,long Epoch,string CheckpointHash,DateTimeOffset AppliedAt,string? HeadHash=null,DateTimeOffset? FreshUntil=null,DateTimeOffset? ReceivedAt=null,Guid? LeaseId=null);
public static class SecurityApplicationReceipt
{
 private static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web);
 public static bool MatchesAccepted(CurrentSecurityReceipt receipt,GatewaySecurityCurrentReceipt accepted)
  =>accepted.NodeId==receipt.NodeId&&accepted.EnvironmentId==receipt.EnvironmentId&&accepted.InstanceId==receipt.InstanceId&&accepted.SecurityGeneration==receipt.SecurityGeneration&&accepted.Epoch==receipt.Epoch&&accepted.CheckpointHash==receipt.CheckpointHash&&accepted.HeadHash==receipt.HeadHash&&accepted.LeaseId==receipt.LeaseId&&accepted.FreshUntil==receipt.FreshUntil&&accepted.ReceivedAt==receipt.ReceivedAt;
 public static CurrentSecurityReceipt? Read(GatewayNode node)
 {
  try
  {
   using var document=JsonDocument.Parse(node.Metadata??"{}");
   if(!HasUnambiguousSecurityRoot(document.RootElement)||!document.RootElement.TryGetProperty("securityApplication",out var value)||!HasUniqueReceiptKeys(value))return null;
   var receipt=value.Deserialize<CurrentSecurityReceipt>(Options);
   return receipt is not null&&receipt.NodeId==node.Id&&receipt.EnvironmentId==node.EnvironmentId&&receipt.InstanceId!=Guid.Empty&&receipt.InstanceId.ToString()==node.InstanceId&&receipt.SecurityGeneration!=Guid.Empty&&receipt.Epoch>=0&&SecuritySignedEncoding.HexHash(receipt.CheckpointHash)&&receipt.AppliedAt.Offset==TimeSpan.Zero?receipt:null;
  }catch(Exception error)when(error is JsonException or InvalidOperationException){return null;}
 }
 public static string Merge(string? metadata,CurrentSecurityReceipt receipt)
 {var root=ParseWritableMetadata(metadata);root["securityApplication"]=JsonSerializer.SerializeToNode(receipt,Options);return root.ToJsonString();}
 public static string Clear(string? metadata)
 {var root=ParseWritableMetadata(metadata);root.Remove("securityApplication");return root.ToJsonString();}
 private static JsonObject ParseWritableMetadata(string? metadata)
 {
  using var document=JsonDocument.Parse(metadata??"{}");
  if(!HasUnambiguousSecurityRoot(document.RootElement)||document.RootElement.TryGetProperty("securityApplication",out var existing)&&existing.ValueKind==JsonValueKind.Object&&!HasUniqueReceiptKeys(existing))throw new InvalidDataException("Ambiguous node security metadata.");
  return JsonNode.Parse(metadata??"{}") as JsonObject??throw new InvalidDataException("Invalid node metadata.");
 }
 private static bool HasUnambiguousSecurityRoot(JsonElement root)
 {
  if(root.ValueKind!=JsonValueKind.Object)return false;
  var count=0;
  foreach(var property in root.EnumerateObject())
   if(property.Name.Equals("securityApplication",StringComparison.OrdinalIgnoreCase)&&(property.Name!="securityApplication"||++count>1))return false;
  return true;
 }
 private static bool HasUniqueReceiptKeys(JsonElement value)
 {
  if(value.ValueKind!=JsonValueKind.Object)return false;
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  return value.EnumerateObject().All(property=>seen.Add(property.Name));
 }
}

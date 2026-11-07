using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
namespace WebApi.Domain.CredentialSecurity;
public readonly record struct CheckpointBytes(ReadOnlyMemory<byte> Value);
public readonly record struct NodeLeaseBytes(ReadOnlyMemory<byte> Value);
public sealed record TrustedSigningKeys(IReadOnlyDictionary<string,string> SubjectPublicKeys);
public sealed class VerifiedCheckpoint
{
 internal VerifiedCheckpoint(SecurityCheckpointPayload payload,string hash,byte[] bytes){Payload=payload;Hash=hash;Bytes=bytes;}
 public SecurityCheckpointPayload Payload {get;} public string Hash {get;} public ReadOnlyMemory<byte> Bytes {get;}
}
public sealed class VerifiedNodeLease
{
 internal VerifiedNodeLease(SecurityNodeLeasePayload payload,byte[] bytes){Payload=payload;Bytes=bytes;}
 public SecurityNodeLeasePayload Payload {get;} public ReadOnlyMemory<byte> Bytes {get;}
}
public static class SecuritySignedEncoding
{
 public const string Protocol="security-ledger-v1",Audience="webapi.gateway.security",CheckpointDomain="webapi.security.checkpoint.v1",LeaseDomain="webapi.security.node-lease.v1";
 public static byte[] Unsigned<T>(string domain,T payload)=>CanonicalJson.Serialize(new {domain,payload});
 public static string CheckpointHash(SecurityCheckpointPayload payload)=>Convert.ToHexStringLower(SHA256.HashData(Unsigned(CheckpointDomain,payload)));
 // The verifier treats absent and empty challenge collections identically;
 // normalize that representation before recording the signed lease claims.
 public static string LeaseHash(SecurityNodeLeasePayload payload)=>Convert.ToHexStringLower(SHA256.HashData(Unsigned(LeaseDomain,payload with {AckChallenges=payload.AckChallenges??Array.Empty<SecurityAckChallenge>()})));
 public static bool HexHash(string? hash)=>hash?.Length==64&&hash.All(x=>x is >= '0' and <= '9' or >= 'a' and <= 'f');
 public static bool KeyId(string? id)=>id?.Length is >=1 and <=64&&id.All(x=>char.IsAsciiLetterOrDigit(x)||x is '_' or '-' or '.');
 public static void RequireP256(ECDsa key){if(key.KeySize!=256||key.ExportParameters(false).Curve.Oid.Value!=ECCurve.NamedCurves.nistP256.Oid.Value)throw new InvalidDataException("security_signing_curve_invalid");}
}
public static class SecurityCheckpointVerifier
{
 private static readonly JsonSerializerOptions Strict=new(CanonicalJson.Options){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
 private static T Read<T>(ReadOnlyMemory<byte> bytes)
 {
  if(bytes.Length is <2 or >32*1024*1024)throw new InvalidDataException("security_material_size_invalid");
  try{using var document=JsonDocument.Parse(bytes);UniqueProperties(document.RootElement);return JsonSerializer.Deserialize<T>(bytes.Span,Strict)??throw new InvalidDataException("security_material_empty");}
  catch(JsonException e){throw new InvalidDataException("security_material_json_invalid",e);}
 }
 private static void UniqueProperties(JsonElement element)
 {
  if(element.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in element.EnumerateObject()){if(!names.Add(p.Name))throw new InvalidDataException("security_material_duplicate_field");UniqueProperties(p.Value);}}
  else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())UniqueProperties(item);
 }
 private static void Verify<T>(string domain,T payload,string signature,string keyId,TrustedSigningKeys keys)
 {
  try
  {
   if(!SecuritySignedEncoding.KeyId(keyId)||!keys.SubjectPublicKeys.TryGetValue(keyId,out var publicKey))throw new InvalidDataException("security_signing_key_unknown");
   using var key=ECDsa.Create();var encoded=Convert.FromBase64String(publicKey);key.ImportSubjectPublicKeyInfo(encoded,out var read);if(read!=encoded.Length)throw new InvalidDataException("security_signing_key_invalid");SecuritySignedEncoding.RequireP256(key);
   var raw=Convert.FromBase64String(signature);if(raw.Length!=64||!key.VerifyData(SecuritySignedEncoding.Unsigned(domain,payload),raw,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))throw new InvalidDataException("security_signature_invalid");
  }
  catch(Exception e)when(e is CryptographicException or FormatException or ArgumentException){throw new InvalidDataException("security_signature_invalid",e);}
 }
 private static bool UtcWindow(DateTimeOffset issued,DateTimeOffset until,int seconds)=>issued> DateTimeOffset.MinValue&&issued.Offset==TimeSpan.Zero&&until.Offset==TimeSpan.Zero&&until>issued&&until-issued<=TimeSpan.FromSeconds(seconds);
 public static VerifiedCheckpoint VerifyCheckpoint(CheckpointBytes bytes,TrustedSigningKeys keys,Guid expectedEnv)
 {
  var signed=Read<SignedSecurityCheckpoint>(bytes.Value);var p=signed.Payload??throw new InvalidDataException("security_checkpoint_empty");Verify(SecuritySignedEncoding.CheckpointDomain,p,signed.Signature,p.KeyId,keys);
  if(expectedEnv==Guid.Empty||p.EnvironmentId!=expectedEnv||p.SecurityGeneration==Guid.Empty||p.Protocol!=SecuritySignedEncoding.Protocol||p.Epoch<0||!SecuritySignedEncoding.HexHash(p.HeadHash)||p.Revocations is null||p.MinimumSnapshotSchema is not ("2.0" or "2.1" or "2.2" or "2.3" or "2.4")||!UtcWindow(p.IssuedAt,p.FreshUntil,32)||p.Full&&p.BaseEpoch!=null||!p.Full&&(p.BaseEpoch is null||p.BaseEpoch<0||p.BaseEpoch>=p.Epoch))throw new InvalidDataException("security_checkpoint_invalid");
  var ids=new HashSet<Guid>();foreach(var r in p.Revocations)if(r is null||r.CredentialId==Guid.Empty||r.OperationId==Guid.Empty||r.Epoch<=0||r.Epoch>p.Epoch||!SecuritySignedEncoding.HexHash(r.EventHash)||r.RevokedAt==DateTimeOffset.MinValue||r.RevokedAt.Offset!=TimeSpan.Zero||r.RevokedAt>p.IssuedAt.AddSeconds(2)||!ids.Add(r.CredentialId))throw new InvalidDataException("security_checkpoint_tombstone_invalid");
  p=p with {Revocations=p.Revocations.ToImmutableArray()};return new(p,SecuritySignedEncoding.CheckpointHash(p),bytes.Value.ToArray());
 }
 public static VerifiedNodeLease VerifyNodeLease(NodeLeaseBytes bytes,TrustedSigningKeys keys,Guid expectedEnv,Guid expectedNode,Guid expectedInstance)
 {
  var signed=Read<SignedSecurityNodeLease>(bytes.Value);var p=signed.Payload??throw new InvalidDataException("security_node_lease_empty");Verify(SecuritySignedEncoding.LeaseDomain,p,signed.Signature,p.KeyId,keys);
  if(expectedEnv==Guid.Empty||expectedNode==Guid.Empty||expectedInstance==Guid.Empty||p.EnvironmentId!=expectedEnv||p.NodeId!=expectedNode||p.InstanceId!=expectedInstance||p.LeaseId==Guid.Empty||p.SecurityGeneration==Guid.Empty||p.Epoch<0||p.Protocol!=SecuritySignedEncoding.Protocol||p.Audience!=SecuritySignedEncoding.Audience||!SecuritySignedEncoding.HexHash(p.CheckpointHash)||p.LeaseSeconds is <5 or >30||p.MaximumClockSkewSeconds is <0 or >2||!UtcWindow(p.IssuedAt,p.FreshUntil,p.LeaseSeconds))throw new InvalidDataException("security_node_lease_invalid");
  var challenges=p.AckChallenges??[];var operations=new HashSet<Guid>();if(challenges.Count>4096||challenges.Any(x=>x is null||x.OperationId==Guid.Empty||x.ChallengeNonce==Guid.Empty||!((x.EventType is null&&x.CredentialId is Guid credential&&credential!=Guid.Empty)||(x.EventType=="EnvironmentPolicyChanged"&&x.CredentialId is null))||x.RequiredEpoch<=0||x.RequiredEpoch>p.Epoch||!operations.Add(x.OperationId)))throw new InvalidDataException("security_ack_challenge_invalid");
  p=p with{AckChallenges=challenges.ToImmutableArray()};return new(p,bytes.Value.ToArray());
 }
 // A persisted lease may belong to the previous process instance. Its
 // identifiers are untrusted until the normal signature and scope checks
 // below complete; the environment is always supplied independently.
 public static VerifiedNodeLease VerifyPersistedNodeLease(NodeLeaseBytes bytes,TrustedSigningKeys keys,Guid expectedEnv)
 {
  var signed=Read<SignedSecurityNodeLease>(bytes.Value);var payload=signed.Payload??throw new InvalidDataException("security_node_lease_empty");
  return VerifyNodeLease(bytes,keys,expectedEnv,payload.NodeId,payload.InstanceId);
 }
 public static WebApi.Contracts.Applications.UsageBatchReceiptPayload VerifyUsageReceipt(WebApi.Contracts.Applications.UsageBatchReceipt receipt,TrustedSigningKeys keys,Guid environment,Guid node,string batchHash)
 {
  var p=receipt.Payload??throw new InvalidDataException("usage_receipt_empty");Verify("webapi.credential-usage.receipt.v1",p,receipt.Signature,p.KeyId,keys);
  if(p.EnvironmentId!=environment||p.NodeId!=node||p.BatchHash!=batchHash||p.Buckets is null||p.GapIds is null||p.Buckets.Count>1000||p.GapIds.Count>100||p.ReceivedAt.Offset!=TimeSpan.Zero||p.Buckets.Any(x=>x is null||x.Result is not("Accepted"or"Replayed"or"Ignored")))throw new InvalidDataException("usage_receipt_mismatch");return p;
 }

}

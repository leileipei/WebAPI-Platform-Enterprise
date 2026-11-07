using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
namespace WebApi.Infrastructure.CredentialSecurity;
// The cryptographic primitive signs caller-validated authority data. No endpoint accepts arbitrary signing inputs.
public sealed class SecurityCheckpointSigner(string keyId,string privateKeyPem)
{
 private string Sign<T>(string domain,T payload,string requestedKey)
 {
  if(requestedKey!=keyId||!SecuritySignedEncoding.KeyId(keyId))throw new InvalidDataException("security_signing_key_invalid");
  using var key=ECDsa.Create();key.ImportFromPem(privateKeyPem);SecuritySignedEncoding.RequireP256(key);return Convert.ToBase64String(key.SignData(SecuritySignedEncoding.Unsigned(domain,payload),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
 }
 public byte[] SignCheckpoint(SecurityCheckpointPayload payload)=>CanonicalJson.Serialize(new SignedSecurityCheckpoint(payload,Sign(SecuritySignedEncoding.CheckpointDomain,payload,payload.KeyId)));
 public byte[] SignNodeLease(SecurityNodeLeasePayload payload)=>CanonicalJson.Serialize(new SignedSecurityNodeLease(payload,Sign(SecuritySignedEncoding.LeaseDomain,payload,payload.KeyId)));
 public WebApi.Contracts.Applications.UsageBatchReceipt SignUsageReceipt(WebApi.Contracts.Applications.UsageBatchReceiptPayload payload)=>new(payload,Sign("webapi.credential-usage.receipt.v1",payload,payload.KeyId));
 public string ExportPublicKey(){using var key=ECDsa.Create();key.ImportFromPem(privateKeyPem);SecuritySignedEncoding.RequireP256(key);return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());}
 public override string ToString()=>"SecurityCheckpointSigner("+keyId+")";
}

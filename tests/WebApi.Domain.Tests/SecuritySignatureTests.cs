using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class SecuritySignatureTests
{
 private readonly Guid env=Guid.NewGuid(),generation=Guid.NewGuid(),node=Guid.NewGuid(),instance=Guid.NewGuid();
 private static (SecurityCheckpointSigner Signer,TrustedSigningKeys Keys) Keys(string id="test"){using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);return(new(id,key.ExportPkcs8PrivateKeyPem()),new(new Dictionary<string,string>{{id,Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}}));}
 private SecurityCheckpointPayload Checkpoint(string key="test")=>new("security-ledger-v1",env,generation,1,new string('a',64),[new(Guid.NewGuid(),Guid.NewGuid(),1,DateTimeOffset.UtcNow,new string('a',64))],false,"2.0",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddSeconds(30),key);
 [Fact] public void SignedNullTombstoneIsRejectedAsInvalidMaterial()
 {
  var (signer,keys)=Keys();var bytes=signer.SignCheckpoint(Checkpoint() with{Revocations=[null!]});
  Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyCheckpoint(new(bytes),keys,env));
 }
 [Fact] public void TrustedSignatureBindsEveryCheckpointField()
 {
  var (signer,keys)=Keys();var payload=Checkpoint();var bytes=signer.SignCheckpoint(payload);var verified=SecurityCheckpointVerifier.VerifyCheckpoint(new(bytes),keys,env);Assert.Equal(payload.EnvironmentId,verified.Payload.EnvironmentId);Assert.Equal(payload.Revocations[0],Assert.Single(verified.Payload.Revocations));Assert.Equal(64,verified.Hash.Length);
  var wire=JsonSerializer.Deserialize<SignedSecurityCheckpoint>(bytes,CanonicalJson.Options)!;var modified=CanonicalJson.Serialize(wire with {Payload=payload with {Epoch=2}});Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyCheckpoint(new(modified),keys,env));
 }
 [Fact] public void WrongEnvironmentUnknownKeyAndCorruptSignatureAreRejected()
 {
  var (signer,keys)=Keys();var bytes=signer.SignCheckpoint(Checkpoint());Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyCheckpoint(new(bytes),keys,Guid.NewGuid()));Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyCheckpoint(new(bytes),new(new Dictionary<string,string>()),env));
  var wire=JsonSerializer.Deserialize<SignedSecurityCheckpoint>(bytes,CanonicalJson.Options)!;Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyCheckpoint(new(CanonicalJson.Serialize(wire with {Signature=Convert.ToBase64String(new byte[64])})),keys,env));
 }
 [Fact] public void NodeLeaseCannotBeReusedForAnotherNodeInstanceOrAudience()
 {
  var (signer,keys)=Keys();var now=DateTimeOffset.UtcNow;var payload=new SecurityNodeLeasePayload(Guid.NewGuid(),"security-ledger-v1",env,generation,1,new string('b',64),node,instance,"webapi.gateway.security",now,now.AddSeconds(30),"test");var bytes=signer.SignNodeLease(payload);Assert.Equal(node,SecurityCheckpointVerifier.VerifyNodeLease(new(bytes),keys,env,node,instance).Payload.NodeId);
  Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyNodeLease(new(bytes),keys,env,Guid.NewGuid(),instance));Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyNodeLease(new(bytes),keys,env,node,Guid.NewGuid()));Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyNodeLease(new(signer.SignNodeLease(payload with {Audience="another-service"})),keys,env,node,instance));
 }
 [Fact] public void LegacyChallengeWireStaysCompatibleWhilePolicyChallengeIsExplicit()
 {
  var(signer,keys)=Keys();var now=DateTimeOffset.UtcNow;var challenge=new SecurityAckChallenge(Guid.NewGuid(),Guid.NewGuid(),1,Guid.NewGuid());var lease=new SecurityNodeLeasePayload(Guid.NewGuid(),"security-ledger-v1",env,generation,1,new string('b',64),node,instance,"webapi.gateway.security",now,now.AddSeconds(30),"test",AckChallenges:[challenge]);
  var bytes=signer.SignNodeLease(lease);Assert.DoesNotContain("eventType",System.Text.Encoding.UTF8.GetString(bytes));Assert.Equal(challenge,Assert.Single(SecurityCheckpointVerifier.VerifyNodeLease(new(bytes),keys,env,node,instance).Payload.AckChallenges!));
  var policy=challenge with{CredentialId=null,EventType="EnvironmentPolicyChanged"};Assert.Equal(policy,Assert.Single(SecurityCheckpointVerifier.VerifyNodeLease(new(signer.SignNodeLease(lease with{AckChallenges=[policy]})),keys,env,node,instance).Payload.AckChallenges!));
  Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyNodeLease(new(signer.SignNodeLease(lease with{AckChallenges=[challenge with{CredentialId=null}]})),keys,env,node,instance));
 }
 [Fact] public void TrustedKeyRotationAllowsBothExplicitPublicKeys()
 {
  var first=Keys("first");var next=Keys("next");var trusted=new TrustedSigningKeys(first.Keys.SubjectPublicKeys.Concat(next.Keys.SubjectPublicKeys).ToDictionary(x=>x.Key,x=>x.Value));Assert.Equal("first",SecurityCheckpointVerifier.VerifyCheckpoint(new(first.Signer.SignCheckpoint(Checkpoint("first"))),trusted,env).Payload.KeyId);Assert.Equal("next",SecurityCheckpointVerifier.VerifyCheckpoint(new(next.Signer.SignCheckpoint(Checkpoint("next"))),trusted,env).Payload.KeyId);
 }
 [Fact] public void SignedMalformedBoundsAndCrossProtocolAreStillRejected()
 {
  var (signer,keys)=Keys();var payload=Checkpoint();foreach(var invalid in new[]{payload with {Epoch=-1},payload with {SecurityGeneration=Guid.Empty},payload with {Protocol="other"},payload with {FreshUntil=payload.IssuedAt},payload with {Revocations=[payload.Revocations[0],payload.Revocations[0]]}})Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyCheckpoint(new(signer.SignCheckpoint(invalid)),keys,env));
 }
 [Theory][InlineData("2.3")][InlineData("2.4")]
 public void SignedGovernanceCheckpointSupportsBothSecurityFloors(string minimum)
 {
  var(signer,keys)=Keys();var payload=Checkpoint() with{MinimumSnapshotSchema=minimum};
  Assert.Equal(minimum,SecurityCheckpointVerifier.VerifyCheckpoint(new(signer.SignCheckpoint(payload)),keys,env).Payload.MinimumSnapshotSchema);
 }
 [Fact] public void DuplicateSecurityFieldsCannotHideBehindDifferentCasing()
 {
  var(signer,keys)=Keys();var json=System.Text.Encoding.UTF8.GetString(signer.SignCheckpoint(Checkpoint()));
  Assert.Contains("\"payload\":",json);
  var duplicate=json.Replace("\"payload\":","\"payload\":null,\"Payload\":",StringComparison.Ordinal);
  Assert.Throws<InvalidDataException>(()=>SecurityCheckpointVerifier.VerifyCheckpoint(new(System.Text.Encoding.UTF8.GetBytes(duplicate)),keys,env));
 }
}

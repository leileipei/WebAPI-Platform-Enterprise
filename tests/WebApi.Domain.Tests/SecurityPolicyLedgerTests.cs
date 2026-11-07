using WebApi.Contracts.Common;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class SecurityPolicyLedgerTests
{
 private static SecurityLedgerMaterial Material(params (bool Require,string Schema)[] policies){
  var env=Guid.NewGuid();var org=Guid.NewGuid();var project=Guid.NewGuid();var gen=Guid.NewGuid();var head=SecurityEventHash.Genesis;var events=new List<SecurityEvent>();
  foreach(var policy in policies){var e=new SecurityEvent{EnvironmentId=env,OrganizationId=org,ProjectId=project,SecurityGeneration=gen,Epoch=events.Count+1,OperationId=Guid.NewGuid(),EventType="EnvironmentPolicyChanged",PolicyJson=System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(new{requireEnvironmentCredentials=policy.Require,minimumSnapshotSchema=policy.Schema})),OccurredAt=DateTimeOffset.UtcNow,PreviousHash=head};e.EventHash=SecurityEventHash.Compute(e);events.Add(e);head=e.EventHash;}
  return new(env,org,project,gen,events.Count,head,policies.LastOrDefault().Require,policies.LastOrDefault().Schema??"2.0",events,[]);
 }
 [Fact] public void ValidPolicyEventsDoNotInventTombstones()=>SecurityLedgerReader.Validate(Material((false,"2.3"),(true,"2.3")));
 [Fact] public void PolicyHashIgnoresJsonbWhitespaceAndOrdering(){var m=Material((true,"2.3"));var e=m.Events[0];var hash=e.EventHash;e.PolicyJson="{\"requireEnvironmentCredentials\": true, \"minimumSnapshotSchema\": \"2.3\"}";Assert.Equal(hash,SecurityEventHash.Compute(e));}
 [Theory][InlineData(false,"2.2")][InlineData(false,"2.3")]public void CutoverCannotBeWeakened(bool require,string schema)=>Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(Material((true,"2.3"),(require,schema))));
 [Fact] public void AuthorityMustEqualLastPolicyEvent(){var m=Material((true,"2.3"));Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(m with {RequireEnvironmentCredentials=false}));Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(m with {MinimumSnapshotSchema="2.2"}));}
 [Fact] public void PolicyFloorCannotBeSetWithoutAnEvent()=>Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(Material() with {MinimumSnapshotSchema="2.3"}));
 [Fact] public void UnknownPolicyFieldIsRejected(){var m=Material((true,"2.3"));var e=m.Events[0];e.PolicyJson="{\"requireEnvironmentCredentials\":true,\"minimumSnapshotSchema\":\"2.3\",\"extra\":1}";e.EventHash=SecurityEventHash.Compute(e);Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(m with {HeadHash=e.EventHash}));}
 [Fact]public void InitiallyEnvironmentBoundPolicyIsPreservedBeforeLaterSchemaChange()
 {var m=Material((true,"2.3")) with {InitialRequireEnvironmentCredentials=true};Assert.True(SecurityPolicyRules.AtEpoch(m,0).RequireEnvironmentCredentials);}
 [Fact]public void FalseGenesisIsNotInferredFromCurrentCutoverTrue()
 {var m=Material((true,"2.3")) with {InitialRequireEnvironmentCredentials=false};Assert.False(SecurityPolicyRules.AtEpoch(m,0).RequireEnvironmentCredentials);}
 [Fact]public void UnknownHistoricalGenesisFailsClosed()
 {var e=Assert.Throws<ApiException>(()=>SecurityPolicyRules.AtEpoch(Material((true,"2.3")),0));Assert.Equal(503,e.Status);Assert.Equal("security_initial_policy_unknown",e.Code);}
 [Fact]public void InitiallyBoundPolicyCannotRegressInFirstEvent()
 {Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(Material((false,"2.3")) with {InitialRequireEnvironmentCredentials=true}));}

 [Fact] public void Governance23CanAdvanceTo24WithoutRewritingItsEarlierEvent()
 {
  var m=Material((false,"2.3"),(true,"2.4"));
  var earlierHash=m.Events[0].EventHash;
  SecurityLedgerReader.Validate(m);
  Assert.Equal(earlierHash,m.Events[1].PreviousHash);
  Assert.Equal(earlierHash,SecurityEventHash.Compute(m.Events[0]));
 }
 [Fact] public void Governance24CannotReturnToGovernance23()
  =>Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(Material((true,"2.4"),(true,"2.3"))));
 [Fact] public void GenesisHashPreservesTheCanonicalEmptyArray()
  =>Assert.Equal("4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945",SecurityEventHash.Genesis);
 [Fact] public void GenesisPolicyCannotChangeWithoutAnEvent()
  =>Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(Material() with {InitialRequireEnvironmentCredentials=true,RequireEnvironmentCredentials=false}));
 [Fact] public void RevocationEventRequiresItsExactTombstone()
 {
  var env=Guid.NewGuid();var org=Guid.NewGuid();var project=Guid.NewGuid();var generation=Guid.NewGuid();
  var credential=Guid.NewGuid();var operation=Guid.NewGuid();var at=DateTimeOffset.UtcNow;
  var e=new SecurityEvent{EnvironmentId=env,OrganizationId=org,ProjectId=project,SecurityGeneration=generation,Epoch=1,
   OperationId=operation,EventType="CredentialRevoked",CredentialId=credential,OccurredAt=at,PreviousHash=SecurityEventHash.Genesis};
  e.EventHash=SecurityEventHash.Compute(e);
  var tombstone=new CredentialRevocation{EnvironmentId=env,OrganizationId=org,ProjectId=project,SecurityGeneration=generation,
   Epoch=1,OperationId=operation,CredentialId=credential,ApplicationId=Guid.NewGuid(),RevokedAt=at};
  var m=new SecurityLedgerMaterial(env,org,project,generation,1,e.EventHash,false,"2.0",[e],[tombstone],false);
  SecurityLedgerReader.Validate(m);
  Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(m with {Revocations=[]}));
  tombstone.OperationId=Guid.NewGuid();
  Assert.Throws<InvalidOperationException>(()=>SecurityLedgerReader.Validate(m));
 }

}

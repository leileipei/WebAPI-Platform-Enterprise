using WebApi.Contracts.Security;
using WebApi.Contracts.Runtime;
using WebApi.Domain.CredentialSecurity;
using WebApi.Gateway.Security;
using WebApi.Gateway.Tests.Support;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class SecurityStateTests
{
 [Theory][InlineData(true)][InlineData(false)] public async Task HigherEpochCannotWeakenSignedCutover(bool lowerSchema)
 {
  using var f=new SecurityFixture();var strict=f.Pair(1,[],minimumSchema:"2.3",requireEnvironmentCredentials:true);Assert.True((await f.State.ApplyAsync(strict.Checkpoint,strict.Lease)).Applied);
  var weakened=f.Pair(2,[],minimumSchema:lowerSchema?"2.2":"2.3",requireEnvironmentCredentials:lowerSchema);
  Assert.False((await f.State.ApplyAsync(weakened.Checkpoint,weakened.Lease)).Applied);Assert.False(f.State.IsReady);
  Assert.False(f.State.CredentialEnvironmentAllowed(new(Guid.NewGuid(),"ak_test",new string('a',64),"Active",f.Clock.GetUtcNow().AddMinutes(-1),f.Clock.GetUtcNow().AddHours(1),null,"LegacyGlobal")));
 }
 [Fact] public async Task CutoverRejectsOldRollbackAndUnprovenSnapshotTuple()
 {
  using var f=new SecurityFixture();var pair=f.Pair(2,[],minimumSchema:"2.3");Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  var old=new RuntimeSnapshot("2.2",f.Env,1,f.Clock.GetUtcNow(),[],[],[],[]);Assert.False(f.State.CanUseSnapshot(old));
  var valid=old with {SchemaVersion="2.3",RequiredSecurityGeneration=f.Generation,RequiredSecurityEpoch=2,RequireEnvironmentCredentials=false,MinimumSnapshotSchema="2.3"};Assert.True(f.State.CanUseSnapshot(valid));Assert.False(f.State.CanUseSnapshot(valid with {RequiredSecurityGeneration=Guid.NewGuid()}));Assert.False(f.State.CanUseSnapshot(valid with {RequiredSecurityEpoch=3}));
  Assert.False(f.State.CanUseSnapshot(valid with {Applications=[new(Guid.NewGuid(),"Active",[],[new(Guid.NewGuid(),f.Clock.GetUtcNow().AddMinutes(-1),null,"LegacyApi",[],f.Env)],f.Env)]}));
 }
 [Fact] public async Task RejectOldEpochAndConflictingHash()
 {
  using var f=new SecurityFixture();var a=Guid.NewGuid();var b=Guid.NewGuid();var first=f.Pair(1,[a]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);var next=f.Pair(2,[a,b]);Assert.True((await f.State.ApplyAsync(next.Checkpoint,next.Lease)).Applied);
  Assert.False((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);var conflict=f.Pair(2,[a,b],head:new string('c',64));Assert.False((await f.State.ApplyAsync(conflict.Checkpoint,conflict.Lease)).Applied);Assert.Equal(2,f.State.CurrentEpoch);Assert.Equal(SecurityAdmissionDecision.KnownRevoked,f.State.CanAdmit(b,f.Clock.GetUtcNow()));
 }
 [Fact] public async Task GapNeedsFullCheckpoint()
 {
  // A different signed full response needs a later issue time; equal-time distinct leases are ambiguous.
  using var f=new SecurityFixture();var a=Guid.NewGuid();var b=Guid.NewGuid();var first=f.Pair(1,[a]);await f.State.ApplyAsync(first.Checkpoint,first.Lease);var gap=f.Pair(3,[b],full:false,baseEpoch:1);var result=await f.State.ApplyAsync(gap.Checkpoint,gap.Lease);Assert.False(result.Applied);Assert.True(result.NeedsFullCheckpoint);Assert.False(f.State.IsReady);Assert.Equal(SecurityAdmissionDecision.KnownRevoked,f.State.CanAdmit(a,f.Clock.GetUtcNow()));Assert.Equal(SecurityAdmissionDecision.Unavailable,f.State.CanAdmit(b,f.Clock.GetUtcNow()));f.Clock.Advance(1);var full=f.Pair(3,[a,b]);Assert.True((await f.State.ApplyAsync(full.Checkpoint,full.Lease)).Applied);Assert.True(f.State.IsReady);
 }
 [Fact] public async Task OtherNodeLeaseRejected()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[Guid.NewGuid()],leaseNode:Guid.NewGuid());Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);Assert.False(f.State.IsReady);
 }
 [Fact] public async Task LeaseReplayDoesNotExtendDeadline()
 {
  using var f=new SecurityFixture();var revoked=Guid.NewGuid();var pair=f.Pair(1,[revoked]);await f.State.ApplyAsync(pair.Checkpoint,pair.Lease);f.Clock.Advance(29);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);Assert.True(f.State.IsReady);f.Clock.Advance(2);Assert.False(f.State.IsReady);Assert.Equal(SecurityAdmissionDecision.Unavailable,f.State.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));Assert.Equal(SecurityAdmissionDecision.KnownRevoked,f.State.CanAdmit(revoked,f.Clock.GetUtcNow()));
 }
 [Fact] public async Task ClockRollbackCannotExtendMonotonicLeaseBudget()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[Guid.NewGuid()]);await f.State.ApplyAsync(pair.Checkpoint,pair.Lease);f.Clock.Advance(31);f.Clock.ShiftUtc(-20);Assert.False(f.State.IsReady);Assert.Equal(SecurityAdmissionDecision.Unavailable,f.State.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);Assert.False(f.State.IsReady);
 }
 [Fact] public async Task MonotonicTimestampRollbackCannotBeClearedByLeaseReplay()
 {
  using var f=new SecurityFixture();f.Clock.Advance(10);var pair=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  f.Clock.RewindTimestamp(5);Assert.False(f.State.IsReady);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);Assert.False(f.State.IsReady);
  f.Clock.RewindTimestamp(-5);Assert.False(f.State.IsReady);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact] public async Task MidWindowTimestampAndWallClockRollbackCannotExtendTheLease()
 {
  using var f=new SecurityFixture();f.Clock.Advance(1);var pair=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  f.Clock.Advance(25);Assert.True(f.State.IsReady);f.Clock.RewindTimestamp(20);f.Clock.ShiftUtc(-20);
  Assert.False(f.State.IsReady);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact] public async Task KnownRevokedTrafficStillRecordsObservedMonotonicTime()
 {
  using var f=new SecurityFixture();f.Clock.Advance(1);var denied=Guid.NewGuid();var pair=f.Pair(1,[denied]);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  f.Clock.Advance(25);Assert.Equal(SecurityAdmissionDecision.KnownRevoked,f.State.CanAdmit(denied,f.Clock.GetUtcNow()));f.Clock.RewindTimestamp(20);f.Clock.ShiftUtc(-20);
  Assert.Equal(SecurityAdmissionDecision.Unavailable,f.State.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));Assert.False(f.State.IsReady);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Theory]
 [InlineData(false)]
 [InlineData(true)]
 public async Task WallClockRollbackOutsideAllowedSkewFailsClosed(bool elapsedBeforeRollback)
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  if(elapsedBeforeRollback){f.Clock.Advance(10);Assert.True(f.State.IsReady);}f.Clock.ShiftUtc(-3);
  Assert.False(f.State.IsReady);f.Clock.ShiftUtc(3);Assert.False(f.State.IsReady);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact] public async Task ObservedExpiryCannotBeReversedByWallClockRollback()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  f.Clock.ShiftUtc(31);Assert.False(f.State.IsReady);f.Clock.ShiftUtc(-31);Assert.False(f.State.IsReady);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact] public async Task ExpiredLeaseCannotReviveEvenWhenWallClockRollbackStaysWithinSkew()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  f.Clock.Advance(29);f.Clock.ShiftUtc(1.1);Assert.False(f.State.IsReady);f.Clock.ShiftUtc(-0.6);
  Assert.False(f.State.IsReady);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
 }
 [Fact] public async Task LongClockSamplingPauseCannotExtendMonotonicDeadline()
 {
  using var f=new SecurityFixture();var scripted=new ScriptedClock(f.Clock);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,scripted);
  var pair=f.Pair(1,[]);scripted.Set(0,2*f.Clock.TimestampFrequency,2*f.Clock.TimestampFrequency,2*f.Clock.TimestampFrequency);Assert.True((await state.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);scripted.Reset();
  f.Clock.Advance(31);f.Clock.ShiftUtc(-2);Assert.False(state.IsReady);Assert.False((await state.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
 }
 [Fact] public async Task ExcessivelyWideClockSampleCannotAuthorizeEvenBeforeNominalExpiry()
 {
  using var f=new SecurityFixture();var scripted=new ScriptedClock(f.Clock);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,scripted);
  var pair=f.Pair(1,[]);scripted.Set(0,20*f.Clock.TimestampFrequency);Assert.False((await state.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);scripted.Reset();
  f.Clock.Advance(21);f.Clock.ShiftUtc(-15);Assert.False(state.IsReady);
 }
 [Fact] public async Task RepeatedSmallWallClockRollbacksCannotResetTheDriftBaseline()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  f.Clock.Advance(1);f.Clock.ShiftUtc(-1.5);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  f.Clock.Advance(1);f.Clock.ShiftUtc(-1.5);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);Assert.False(f.State.IsReady);
 }
 [Fact] public async Task RealTwoSecondSkewClockSupportsConcurrentAdmissionsWithoutFalseRollback()
 {
  using var f=new SecurityFixture();var template=f.Pair(1,[]);var now=DateTimeOffset.UtcNow;
  var checkpointPayload=template.Checkpoint.Payload with{IssuedAt=now,FreshUntil=now.AddSeconds(30)};
  var checkpoint=SecurityCheckpointVerifier.VerifyCheckpoint(new(f.Signer.SignCheckpoint(checkpointPayload)),f.Keys,f.Env);
  var leasePayload=template.Lease.Payload with{IssuedAt=now,FreshUntil=now.AddSeconds(30),CheckpointHash=checkpoint.Hash,MaximumClockSkewSeconds=2};
  var lease=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(leasePayload)),f.Keys,f.Env,f.Node,f.Instance);
  await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,TimeProvider.System);Assert.True((await state.ApplyAsync(checkpoint,lease)).Applied);
  var workers=Enumerable.Range(0,4).Select(_=>Task.Run(()=>{for(var i=0;i<5000;i++)if(!state.IsReady)return false;return true;})).ToArray();
  Assert.All(await Task.WhenAll(workers),Assert.True);Assert.True(state.IsReady);
 }
 [Fact] public async Task ExactlyAlignedZeroSkewClockRemainsSupported()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[]);var payload=pair.Lease.Payload with{MaximumClockSkewSeconds=0};
  var lease=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(payload)),f.Keys,f.Env,f.Node,f.Instance);
  Assert.True((await f.State.ApplyAsync(pair.Checkpoint,lease)).Applied);f.Clock.Advance(1);Assert.True(f.State.IsReady);
 }
 [Theory]
 [InlineData(0,false)]
 [InlineData(2,true)]
 public async Task QuantizedWallClockRequiresSufficientDeclaredSkew(int skew,bool ready)
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[]);var payload=pair.Lease.Payload with{MaximumClockSkewSeconds=skew};
  var lease=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(payload)),f.Keys,f.Env,f.Node,f.Instance);
  var quantized=new QuantizedClock(f.Clock);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,quantized);
  Assert.True((await state.ApplyAsync(pair.Checkpoint,lease)).Applied);f.Clock.Advance(0.1);Assert.Equal(ready,state.IsReady);
 }
 [Fact] public async Task AdmissionUsesOneCoherentMonotonicSampleForBothDeadlineBounds()
 {
  using var f=new SecurityFixture();f.Clock.Advance(10);var scripted=new ScriptedClock(f.Clock);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,scripted);
  var pair=f.Pair(1,[]);Assert.True((await state.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);var received=f.Clock.GetTimestamp();
  scripted.Set(received+31*f.Clock.TimestampFrequency,received-f.Clock.TimestampFrequency);
  Assert.Equal(SecurityAdmissionDecision.Unavailable,state.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));Assert.Equal(2,scripted.Reads);scripted.Reset();
 }
 [Fact] public async Task FullCheckpointCannotRemoveKnownTombstoneOrSwitchGeneration()
 {
  using var f=new SecurityFixture();var a=Guid.NewGuid();var b=Guid.NewGuid();var first=f.Pair(1,[a]);await f.State.ApplyAsync(first.Checkpoint,first.Lease);var shrink=f.Pair(2,[b]);Assert.False((await f.State.ApplyAsync(shrink.Checkpoint,shrink.Lease)).Applied);Assert.False(f.State.IsReady);Assert.Equal(SecurityAdmissionDecision.KnownRevoked,f.State.CanAdmit(a,f.Clock.GetUtcNow()));var changed=f.Pair(2,[a,b],generation:Guid.NewGuid());Assert.False((await f.State.ApplyAsync(changed.Checkpoint,changed.Lease)).Applied);Assert.Equal(1,f.State.CurrentEpoch);
 }
 [Fact] public async Task MismatchedCheckpointLeaseAndExpiredLeaseCannotMakeReady()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[Guid.NewGuid()]);var p=pair.Lease.Payload with {CheckpointHash=new string('b',64)};var mismatched=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(p)),f.Keys,f.Env,f.Node,f.Instance);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,mismatched)).Applied);f.Clock.Advance(31);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);Assert.False(f.State.IsReady);
 }
 [Fact] public async Task OldFullReplayCannotClearObservedGap()
 {
  using var f=new SecurityFixture();var first=f.Pair(1,[Guid.NewGuid()]);await f.State.ApplyAsync(first.Checkpoint,first.Lease);var gap=f.Pair(3,[Guid.NewGuid()],full:false,baseEpoch:1);await f.State.ApplyAsync(gap.Checkpoint,gap.Lease);Assert.False((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);Assert.False(f.State.IsReady);
 }
 [Fact] public async Task OldGenerationReplayCannotClearGenerationConflict()
 {
  using var f=new SecurityFixture();var ids=new[]{Guid.NewGuid()};var first=f.Pair(1,ids);await f.State.ApplyAsync(first.Checkpoint,first.Lease);var changed=f.Pair(2,ids,generation:Guid.NewGuid());await f.State.ApplyAsync(changed.Checkpoint,changed.Lease);Assert.False((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);Assert.False(f.State.IsReady);
 }
 [Fact] public async Task SameEpochReplayCannotClearConflictingAuthority()
 {
  using var f=new SecurityFixture();var ids=new[]{Guid.NewGuid()};var first=f.Pair(1,ids);await f.State.ApplyAsync(first.Checkpoint,first.Lease);var conflict=f.Pair(1,ids,head:new string('e',64));await f.State.ApplyAsync(conflict.Checkpoint,conflict.Lease);Assert.False((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);Assert.False(f.State.IsReady);var later=f.Pair(2,ids);Assert.False((await f.State.ApplyAsync(later.Checkpoint,later.Lease)).Applied);
  // A later signature alone does not prove which conflicting historic chain is authoritative.
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 private sealed class ScriptedClock(SecurityTestClock actual):TimeProvider
 {
  private readonly Queue<long> values=new();public int Reads{get;private set;}
  public void Set(params long[] timestamps){values.Clear();foreach(var value in timestamps)values.Enqueue(value);Reads=0;}
  public void Reset()=>values.Clear();
  public override DateTimeOffset GetUtcNow()=>actual.GetUtcNow();
  public override long TimestampFrequency=>actual.TimestampFrequency;
  public override long GetTimestamp(){if(values.Count==0)return actual.GetTimestamp();Reads++;return values.Dequeue();}
 }
 private sealed class QuantizedClock(SecurityTestClock actual):TimeProvider
 {
  public override DateTimeOffset GetUtcNow(){var utc=actual.GetUtcNow();return new DateTimeOffset(utc.Ticks-utc.Ticks%TimeSpan.TicksPerSecond,TimeSpan.Zero);}
  public override long TimestampFrequency=>actual.TimestampFrequency;
  public override long GetTimestamp()=>actual.GetTimestamp();
 }
}

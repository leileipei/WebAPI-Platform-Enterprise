using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using WebApi.Gateway.Tests.Support;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class SecurityRenewalAdmissionTests
{
 [Fact]public async Task SameAuthorityRenewalKeepsOldAdmissionUntilDurablePublication()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(0,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var renewal=f.Pair(0,[]);disk.Delay=true;
  var applying=state.ApplyAsync(renewal.Checkpoint,renewal.Lease);await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
  var admitting=state.EnterAdmissionAsync();var ready=await Task.WhenAny(admitting,Task.Delay(200));var concurrent=ready==admitting;
  Assert.Equal(first.Lease.Payload.LeaseId,state.CurrentAdmissionLeaseId);
  // On RED release persistence before awaiting admission, so no fixture lock leaks.
  if(!concurrent)disk.Release.TrySetResult();using var ticket=await admitting.WaitAsync(TimeSpan.FromSeconds(5));
  if(concurrent){Assert.Equal(SecurityAdmissionDecision.Allow,state.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));disk.Release.TrySetResult();await disk.Persisted.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(first.Lease.Payload.LeaseId,state.CurrentAdmissionLeaseId);Assert.False(applying.IsCompleted);}
  ticket.Dispose();Assert.True((await applying).Applied);Assert.Equal(renewal.Lease.Payload.LeaseId,state.CurrentAdmissionLeaseId);
  Assert.True(concurrent,"A same-authority renewal blocked authenticated admission during its disk write.");
 }
 [Fact]public async Task NewRevocationRetainsExclusiveAdmissionUntilDurablePublication()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);var first=f.Pair(0,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var revoked=Guid.NewGuid();var next=f.Pair(1,[revoked]);disk.Delay=true;
  var applying=state.ApplyAsync(next.Checkpoint,next.Lease);await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var admission=state.EnterAdmissionAsync();Assert.False(admission.IsCompleted);Assert.Equal(first.Lease.Payload.LeaseId,state.CurrentAdmissionLeaseId);disk.Release.TrySetResult();Assert.True((await applying).Applied);using var ticket=await admission;Assert.Equal(SecurityAdmissionDecision.KnownRevoked,state.CanAdmit(revoked,f.Clock.GetUtcNow()));
 }
 [Theory]
 [InlineData("deny")]
 [InlineData("shorter")]
 public async Task RestrictiveSameEpochLeaseCannotAdmitDuringPersistence(string restriction)
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var next=f.Pair(1,[]);
  var payload=restriction=="deny"?next.Lease.Payload with{AllowAuthenticatedTraffic=false}:next.Lease.Payload with{FreshUntil=next.Lease.Payload.IssuedAt.AddSeconds(5)};
  var lease=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(payload)),f.Keys,f.Env,f.Node,f.Instance);disk.Delay=true;
  var applying=state.ApplyAsync(next.Checkpoint,lease);await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var admission=state.EnterAdmissionAsync();
  var blocked=!admission.IsCompleted;if(!blocked)(await admission).Dispose();disk.Release.TrySetResult();Assert.True((await applying).Applied);if(blocked)(await admission.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
  Assert.True(blocked,"A restrictive lease must hold exclusive admission until its durable publication.");
  if(restriction=="deny")Assert.Equal(SecurityAdmissionDecision.Unavailable,state.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));
 }
 [Theory]
 [InlineData(true)]
 [InlineData(false)]
 public async Task ConflictingSameTimeLeasesCannotReverseARestriction(bool initialAllow)
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[]);var initialPayload=pair.Lease.Payload with{AllowAuthenticatedTraffic=initialAllow};
  var initial=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(initialPayload)),f.Keys,f.Env,f.Node,f.Instance);
  Assert.True((await f.State.ApplyAsync(pair.Checkpoint,initial)).Applied);
  var conflictingPayload=initialPayload with{LeaseId=Guid.NewGuid(),AllowAuthenticatedTraffic=!initialAllow};
  var conflicting=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(conflictingPayload)),f.Keys,f.Env,f.Node,f.Instance);
  Assert.False((await f.State.ApplyAsync(pair.Checkpoint,conflicting)).Applied);Assert.False(f.State.IsReady);
  Assert.False((await f.State.ApplyAsync(pair.Checkpoint,initial)).Applied);Assert.False(f.State.IsReady);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task LaterSignedLeaseCanResolveSameTimeConflict()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[]);var denied=pair.Lease.Payload with{AllowAuthenticatedTraffic=false};
  var first=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(denied)),f.Keys,f.Env,f.Node,f.Instance);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,first)).Applied);
  var conflicting=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(denied with{LeaseId=Guid.NewGuid(),AllowAuthenticatedTraffic=true})),f.Keys,f.Env,f.Node,f.Instance);
  Assert.False((await f.State.ApplyAsync(pair.Checkpoint,conflicting)).Applied);f.Clock.Advance(1);var later=f.Pair(1,[]);
  Assert.True((await f.State.ApplyAsync(later.Checkpoint,later.Lease)).Applied);Assert.True(f.State.IsReady);
 }
 [Fact]public async Task ConflictingLeaseAtFailedWriteTimeCannotDiscardObservedDenial()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var next=f.Pair(1,[]);
  var denialPayload=next.Lease.Payload with{AllowAuthenticatedTraffic=false};var denial=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(denialPayload)),f.Keys,f.Env,f.Node,f.Instance);
  disk.Fail=true;Assert.False((await state.ApplyAsync(next.Checkpoint,denial)).Applied);disk.Fail=false;
  var conflicting=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(denialPayload with{LeaseId=Guid.NewGuid(),AllowAuthenticatedTraffic=true})),f.Keys,f.Env,f.Node,f.Instance);
  Assert.False((await state.ApplyAsync(next.Checkpoint,conflicting)).Applied);Assert.False(state.IsReady);Assert.False((await state.ApplyAsync(next.Checkpoint,denial)).Applied);
  await state.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Theory]
 [InlineData(2,false)]
 [InlineData(2,true)]
 [InlineData(3,false)]
 public async Task FailedWriteCannotHideObservedRevocationFromLaterFullCheckpoint(long epoch,bool changeHead)
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var existing=Guid.NewGuid();var first=f.Pair(1,[existing]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var denied=Guid.NewGuid();var observed=f.Pair(2,[existing,denied]);
  disk.Fail=true;var failed=await state.ApplyAsync(observed.Checkpoint,observed.Lease);Assert.False(failed.Applied);Assert.Equal("security_lkg_write_denied",failed.ErrorCode);disk.Fail=false;f.Clock.Advance(1);
  var omitted=f.Pair(epoch,[existing],head:changeHead?new string('c',64):null);Assert.False((await state.ApplyAsync(omitted.Checkpoint,omitted.Lease)).Applied);Assert.False(state.IsReady);
  await state.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task ExpiredSignedCheckpointCannotHideItsRevocationFromLaterCheckpoint()
 {
  using var f=new SecurityFixture();var existing=Guid.NewGuid();var first=f.Pair(1,[existing]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var denied=Guid.NewGuid();var observed=f.Pair(2,[existing,denied]);
  f.Clock.Advance(31);Assert.False((await f.State.ApplyAsync(observed.Checkpoint,observed.Lease)).Applied);var omitted=f.Pair(3,[existing]);
  Assert.False((await f.State.ApplyAsync(omitted.Checkpoint,omitted.Lease)).Applied);Assert.False(f.State.IsReady);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task FailedStrictPolicyCannotBeWeakenedByANewerCheckpoint()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);
  var strict=f.Pair(2,[],minimumSchema:"2.4",requireEnvironmentCredentials:true);disk.Fail=true;Assert.False((await state.ApplyAsync(strict.Checkpoint,strict.Lease)).Applied);disk.Fail=false;f.Clock.Advance(1);
  var weakened=f.Pair(3,[]);Assert.False((await state.ApplyAsync(weakened.Checkpoint,weakened.Lease)).Applied);Assert.False(state.IsReady);
  await state.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task PartialCheckpointCannotHideARevocationFromTheNextFullCheckpoint()
 {
  using var f=new SecurityFixture();var existing=Guid.NewGuid();var first=f.Pair(1,[existing]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);
  var denied=Guid.NewGuid();var partial=f.Pair(2,[denied],full:false,baseEpoch:1);Assert.True((await f.State.ApplyAsync(partial.Checkpoint,partial.Lease)).NeedsFullCheckpoint);f.Clock.Advance(1);
  var omitted=f.Pair(3,[existing]);Assert.False((await f.State.ApplyAsync(omitted.Checkpoint,omitted.Lease)).Applied);Assert.False(f.State.IsReady);
  f.Clock.Advance(1);var recovered=f.Pair(4,[existing,denied]);Assert.True((await f.State.ApplyAsync(recovered.Checkpoint,recovered.Lease)).Applied);
  Assert.Equal(SecurityAdmissionDecision.KnownRevoked,f.State.CanAdmit(denied,f.Clock.GetUtcNow()));
 }
 [Fact]public async Task OlderSignedRevocationCannotBeIgnoredAfterAHigherFullCheckpoint()
 {
  using var f=new SecurityFixture();var denied=Guid.NewGuid();var older=f.Pair(1,[denied]);f.Clock.Advance(1);var current=f.Pair(2,[]);
  Assert.True((await f.State.ApplyAsync(current.Checkpoint,current.Lease)).Applied);Assert.True(f.State.IsReady);
  Assert.False((await f.State.ApplyAsync(older.Checkpoint,older.Lease)).Applied);Assert.False(f.State.IsReady);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task HistoricalEpochHeadConflictCannotBeIgnoredAfterANewerCheckpoint()
 {
  using var f=new SecurityFixture();var first=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);
  var current=f.Pair(2,[]);Assert.True((await f.State.ApplyAsync(current.Checkpoint,current.Lease)).Applied);f.Clock.Advance(1);
  var contradiction=f.Pair(1,[],head:new string('c',64));Assert.False((await f.State.ApplyAsync(contradiction.Checkpoint,contradiction.Lease)).Applied);Assert.False(f.State.IsReady);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task HistoricalEpochPolicyConflictCannotBeIgnoredAfterANewerCheckpoint()
 {
  using var f=new SecurityFixture();var first=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);
  var current=f.Pair(2,[],minimumSchema:"2.4",requireEnvironmentCredentials:true);Assert.True((await f.State.ApplyAsync(current.Checkpoint,current.Lease)).Applied);f.Clock.Advance(1);
  var contradiction=f.Pair(1,[],minimumSchema:"2.3",requireEnvironmentCredentials:true);Assert.False((await f.State.ApplyAsync(contradiction.Checkpoint,contradiction.Lease)).Applied);Assert.False(f.State.IsReady);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task KnownOlderCheckpointReplayDoesNotOverrideOrBlockCurrentState()
 {
  using var f=new SecurityFixture();var first=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);
  var current=f.Pair(2,[]);Assert.True((await f.State.ApplyAsync(current.Checkpoint,current.Lease)).Applied);
  Assert.False((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);Assert.True(f.State.IsReady);Assert.Equal(2,f.State.CurrentEpoch);
 }
 [Fact]public async Task UnseenOlderHeadCannotOverrideCurrentState()
 {
  using var f=new SecurityFixture();var unseen=f.Pair(1,[]);f.Clock.Advance(1);var current=f.Pair(2,[]);Assert.True((await f.State.ApplyAsync(current.Checkpoint,current.Lease)).Applied);
  Assert.False((await f.State.ApplyAsync(unseen.Checkpoint,unseen.Lease)).Applied);Assert.True(f.State.IsReady);f.Clock.Advance(1);
  var later=f.Pair(3,[]);Assert.True((await f.State.ApplyAsync(later.Checkpoint,later.Lease)).Applied);Assert.True(f.State.IsReady);
 }
 [Fact]public async Task RepeatedUnseenOlderEpochWithConflictingHeadsRequiresRecovery()
 {
  using var f=new SecurityFixture();var current=f.Pair(3,[]);Assert.True((await f.State.ApplyAsync(current.Checkpoint,current.Lease)).Applied);f.Clock.Advance(1);
  var old=f.Pair(1,[]);Assert.False((await f.State.ApplyAsync(old.Checkpoint,old.Lease)).Applied);Assert.True(f.State.IsReady);f.Clock.Advance(1);
  var contradiction=f.Pair(1,[],head:new string('c',64));Assert.False((await f.State.ApplyAsync(contradiction.Checkpoint,contradiction.Lease)).Applied);Assert.False(f.State.IsReady);
 }
 [Fact]public async Task EvictedHistoricalHeadCannotSilentlyReenterSecurityAdmission()
 {
  using var f=new SecurityFixture();var disk=new MemoryDisk();await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);
  for(var epoch=2;epoch<=4097;epoch++){f.Clock.Advance(0.001);var next=f.Pair(epoch,[]);Assert.True((await state.ApplyAsync(next.Checkpoint,next.Lease)).Applied);}
  Assert.True(state.IsReady);Assert.False((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);Assert.True(state.IsReady);Assert.Equal(4097,state.CurrentEpoch);
  f.Clock.Advance(0.001);var contradiction=f.Pair(1,[],head:new string('c',64));Assert.False((await state.ApplyAsync(contradiction.Checkpoint,contradiction.Lease)).Applied);Assert.False(state.IsReady);
  await state.DisposeAsync();Assert.False(disk.Cleaned);
 }
 [Fact]public async Task ObservedRevocationCapacityFailureCannotForgetFactsOrCleanlyRestart()
 {
  using var f=new SecurityFixture();var disk=new MemoryDisk();await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock,maximumObservedFactBytes:1);
  var first=f.Pair(0,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);
  var observed=f.Pair(1,[Guid.NewGuid()]);var failed=await state.ApplyAsync(observed.Checkpoint,observed.Lease);Assert.False(failed.Applied);Assert.Equal("security_observed_fact_capacity_exceeded",failed.ErrorCode);
  f.Clock.Advance(1);var omitted=f.Pair(2,[]);Assert.False((await state.ApplyAsync(omitted.Checkpoint,omitted.Lease)).Applied);Assert.False(state.IsReady);
  await state.DisposeAsync();Assert.False(disk.Cleaned);
 }
 [Fact]public async Task ExactLeaseRetryDoesNotExtendItsOriginalDeadline()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  f.Clock.Advance(29);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);Assert.True(f.State.IsReady);
  f.Clock.Advance(2);f.Clock.ShiftUtc(-10);Assert.False(f.State.IsReady);Assert.False((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
 }
 [Fact]public async Task FailedSameAuthorityRenewalStillBlocksOldAuthenticatedTraffic()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);var revoked=Guid.NewGuid();var first=f.Pair(1,[revoked]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);disk.Fail=true;var renewal=f.Pair(1,[revoked]);Assert.False((await state.ApplyAsync(renewal.Checkpoint,renewal.Lease)).Applied);Assert.Equal(SecurityAdmissionDecision.Unavailable,state.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));Assert.Equal(SecurityAdmissionDecision.KnownRevoked,state.CanAdmit(revoked,f.Clock.GetUtcNow()));
 }
 [Fact]public async Task CancellingPublicationAfterFailedRenewalCannotLeaveOldTrafficAllowed()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);var revoked=Guid.NewGuid();var first=f.Pair(1,[revoked]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);disk.Delay=true;disk.Fail=true;var renewal=f.Pair(1,[revoked]);using var cancel=new CancellationTokenSource();
  var applying=state.ApplyAsync(renewal.Checkpoint,renewal.Lease,cancel.Token);await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));using var admission=await state.EnterAdmissionAsync();disk.Release.TrySetResult();await disk.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();admission.Dispose();
  SecurityApplyResult? result=null;try{result=await applying.WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){}
  Assert.Equal(SecurityAdmissionDecision.Unavailable,state.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));Assert.Equal(SecurityAdmissionDecision.KnownRevoked,state.CanAdmit(revoked,f.Clock.GetUtcNow()));Assert.NotNull(result);Assert.False(result.Applied);Assert.Equal("security_lkg_write_denied",result.ErrorCode);
 }
 [Fact]public async Task FailedNewerRenewalCannotBeHealedByTheOlderLease()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var renewal=f.Pair(1,[]);disk.Fail=true;
  Assert.False((await state.ApplyAsync(renewal.Checkpoint,renewal.Lease)).Applied);disk.Fail=false;
  Assert.False((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);Assert.False(state.IsReady);
  await state.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task UnexpectedRenewalWriteExceptionStillBlocksOldAdmission()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var renewal=f.Pair(1,[]);disk.Unexpected=true;
  var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>state.ApplyAsync(renewal.Checkpoint,renewal.Lease));Assert.Equal("injected unexpected failure",error.Message);
  Assert.False(state.IsReady);await state.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task CancelledRenewalBeforePersistenceBlocksAndRetainsMarker()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var renewal=f.Pair(1,[]);disk.Delay=true;
  using var cancel=new CancellationTokenSource();var applying=state.ApplyAsync(renewal.Checkpoint,renewal.Lease,cancel.Token);await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();
  await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await applying.WaitAsync(TimeSpan.FromSeconds(5)));
  Assert.False(state.IsReady);await state.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task CancellationAfterDurableRenewalCannotPreventPublication()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var renewal=f.Pair(1,[]);disk.Delay=true;
  using var cancel=new CancellationTokenSource();var applying=state.ApplyAsync(renewal.Checkpoint,renewal.Lease,cancel.Token);await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
  using var admission=await state.EnterAdmissionAsync();disk.Release.TrySetResult();await disk.Persisted.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();admission.Dispose();
  Assert.True((await applying.WaitAsync(TimeSpan.FromSeconds(5))).Applied);Assert.Equal(renewal.Lease.Payload.LeaseId,state.CurrentAdmissionLeaseId);Assert.True(state.IsReady);
 }
 [Fact]public async Task LeaseExpiringDuringPersistenceCannotBeReportedApplied()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);disk.Delay=true;
  var retry=state.ApplyAsync(first.Checkpoint,first.Lease);await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));f.Clock.Advance(30);Assert.False(state.IsReady);
  disk.Release.TrySetResult();Assert.False((await retry.WaitAsync(TimeSpan.FromSeconds(5))).Applied);Assert.False(state.IsReady);
 }
 [Theory]
 [InlineData("before","utc")]
 [InlineData("before","timestamp")]
 [InlineData("before","frequency")]
 [InlineData("after","utc")]
 [InlineData("after","timestamp")]
 [InlineData("after","frequency")]
 public async Task TimeSourceFailureCannotLeaveOldLeaseAllowed(string phase,string operation)
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);var clock=new ThrowingClock(f.Clock);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);var next=f.Pair(1,[]);
  var denied=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(next.Lease.Payload with{AllowAuthenticatedTraffic=false})),f.Keys,f.Env,f.Node,f.Instance);
  if(phase=="before")clock.FailNext(operation);else disk.Delay=true;
  var applying=state.ApplyAsync(next.Checkpoint,denied);
  if(phase=="after"){await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));clock.FailNext(operation);disk.Release.TrySetResult();}
  var result=await applying.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(result.Applied);Assert.False(state.IsReady);
  await state.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task TimeSourceFailureDuringReadinessReturnsUnavailableAndRemainsLatched()
 {
  using var f=new SecurityFixture();var clock=new ThrowingClock(f.Clock);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);clock.FailNext("utc");Assert.False(state.IsReady);Assert.False(state.IsReady);
  await state.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task ChangingTimestampFrequencyInvalidatesTheCurrentLease()
 {
  using var f=new SecurityFixture();var clock=new ThrowingClock(f.Clock);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);clock.SetFrequency(f.Clock.TimestampFrequency*2);
  Assert.False(state.IsReady);clock.SetFrequency(null);Assert.False(state.IsReady);await state.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact]public async Task ReadinessCannotStartANewClockSampleAfterShutdownBegins()
 {
  using var f=new SecurityFixture();using var clock=new BlockingClock(f.Clock);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,clock);
  var first=f.Pair(1,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);clock.Arm();
  var stopping=Task.Run(async()=>await state.DisposeAsync());await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
  var reading=Task.Run(()=>state.IsReady);var returnedBeforeRelease=await Task.WhenAny(reading,Task.Delay(300))==reading;
  clock.Release.Set();await stopping.WaitAsync(TimeSpan.FromSeconds(5));var ready=await reading.WaitAsync(TimeSpan.FromSeconds(5));
  Assert.True(returnedBeforeRelease,"Readiness waited to sample a clock after the shutdown barrier began.");Assert.False(ready);
 }
 [Fact]public async Task RevocationCannotOvertakeAnInFlightRenewalDiskWrite()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk);await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);var first=f.Pair(0,[]);Assert.True((await state.ApplyAsync(first.Checkpoint,first.Lease)).Applied);f.Clock.Advance(1);disk.Delay=true;var renewal=f.Pair(0,[]);var applying=state.ApplyAsync(renewal.Checkpoint,renewal.Lease);await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var revoked=Guid.NewGuid();var next=f.Pair(1,[revoked]);var denial=state.ApplyAsync(next.Checkpoint,next.Lease);Assert.False(denial.IsCompleted);disk.Release.TrySetResult();Assert.True((await applying).Applied);Assert.True((await denial).Applied);Assert.Equal(1,state.CurrentEpoch);Assert.Equal(SecurityAdmissionDecision.KnownRevoked,state.CanAdmit(revoked,f.Clock.GetUtcNow()));var saved=System.Text.Json.JsonSerializer.Deserialize<SecurityNodeMaterial>(await File.ReadAllBytesAsync(f.Disk.FilePath),WebApi.Contracts.Common.CanonicalJson.Options);Assert.Equal(next.Checkpoint.Bytes.ToArray(),saved!.Checkpoint);
 }
 [Fact]public async Task DisposeWaitsForInFlightPersistenceBeforeReleasingDirectory()
 {
  using var f=new SecurityFixture();var disk=new DelayedDisk(f.Disk){Delay=true};await using var state=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),disk,f.Clock);
  var first=f.Pair(0,[]);var writing=state.ApplyAsync(first.Checkpoint,first.Lease);await disk.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
  var stopping=state.DisposeAsync().AsTask();Assert.False(stopping.IsCompleted);
  await using var other=new SecurityStateStore(new(f.Env,()=>f.Node,Guid.NewGuid()),new SecurityLkgStore(f.DirectoryPath),f.Clock);
  Assert.False(await other.RestoreAsync(f.Keys));disk.Release.TrySetResult();Assert.True((await writing).Applied);await stopping.WaitAsync(TimeSpan.FromSeconds(5));
  Assert.False(state.IsReady);Assert.True(await other.RestoreAsync(f.Keys));Assert.False(other.IsReady);
 }
 private sealed class DelayedDisk(ISecurityLkgStore actual):ISecurityLkgStore
 {
  public bool Delay,Fail,Unexpected;public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously),Persisted=new(TaskCreationOptions.RunContinuationsAsynchronously),Failed=new(TaskCreationOptions.RunContinuationsAsynchronously);
  public ISecurityLkgOwner AcquireOwnership()=>new Owner(this,actual.AcquireOwnership());
  private sealed class Owner(DelayedDisk parent,ISecurityLkgOwner actual):ISecurityLkgOwner
  {
   public Task<SecurityNodeMaterial?> ReadAsync(CancellationToken ct=default)=>actual.ReadAsync(ct);
   public async Task WriteAtomicAsync(SecurityNodeMaterial material,CancellationToken ct=default)
   {if(parent.Delay){parent.Entered.TrySetResult();await parent.Release.Task.WaitAsync(ct);}if(parent.Unexpected)throw new InvalidOperationException("injected unexpected failure");if(parent.Fail){parent.Failed.TrySetResult();throw new IOException("injected renewal persistence failure");}await actual.WriteAtomicAsync(material,ct);if(parent.Delay)parent.Persisted.TrySetResult();}
   public Task CompleteCleanlyAsync(CancellationToken ct=default)=>actual.CompleteCleanlyAsync(ct);
   public ValueTask DisposeAsync()=>actual.DisposeAsync();
  }
 }
 private sealed class MemoryDisk:ISecurityLkgStore
 {
  public bool Cleaned;private SecurityNodeMaterial? material;
  public ISecurityLkgOwner AcquireOwnership()=>new Owner(this);
  private sealed class Owner(MemoryDisk disk):ISecurityLkgOwner
  {
   public Task<SecurityNodeMaterial?> ReadAsync(CancellationToken ct=default)=>Task.FromResult(disk.material);
   public Task WriteAtomicAsync(SecurityNodeMaterial value,CancellationToken ct=default){disk.material=value;return Task.CompletedTask;}
   public Task CompleteCleanlyAsync(CancellationToken ct=default){disk.Cleaned=true;return Task.CompletedTask;}
   public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
  }
 }
 private sealed class ThrowingClock(SecurityTestClock actual):TimeProvider
 {
  private readonly object sync=new();private string? failure;private long? frequency;
  public void FailNext(string operation){lock(sync)failure=operation;}
  public void SetFrequency(long? value){lock(sync)frequency=value;}
  private void Check(string operation){lock(sync){if(failure==operation){failure=null;throw new InvalidOperationException("injected time-source failure");}}}
  public override DateTimeOffset GetUtcNow(){Check("utc");return actual.GetUtcNow();}
  public override long GetTimestamp(){Check("timestamp");return actual.GetTimestamp();}
  public override long TimestampFrequency{get{Check("frequency");lock(sync)return frequency??actual.TimestampFrequency;}}
 }
 private sealed class BlockingClock(SecurityTestClock actual):TimeProvider,IDisposable
 {
  private int armed;public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);public readonly ManualResetEventSlim Release=new(false);
  public void Arm()=>Volatile.Write(ref armed,1);
  public override DateTimeOffset GetUtcNow(){if(Interlocked.Exchange(ref armed,0)==1){Entered.TrySetResult();if(!Release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("test clock release timeout");}return actual.GetUtcNow();}
  public override long GetTimestamp()=>actual.GetTimestamp();
  public override long TimestampFrequency=>actual.TimestampFrequency;
  public void Dispose()=>Release.Dispose();
 }
}

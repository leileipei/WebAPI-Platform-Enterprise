using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using WebApi.Gateway.Tests.Support;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class SecurityRecoveryTests
{
 [Fact] public async Task RestoredSecurityFactsCannotTrustPreviousInstanceLease()
 {
  using var f=new SecurityFixture();var revoked=Guid.NewGuid();var pair=f.Pair(1,[revoked]);await f.State.ApplyAsync(pair.Checkpoint,pair.Lease);await f.State.DisposeAsync();await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,Guid.NewGuid()),f.Disk,f.Clock);Assert.True(await restarted.RestoreAsync(f.Keys));Assert.Equal(1,restarted.CurrentEpoch);Assert.False(restarted.IsReady);Assert.Equal(SecurityAdmissionDecision.KnownRevoked,restarted.CanAdmit(revoked,f.Clock.GetUtcNow()));Assert.Equal(SecurityAdmissionDecision.Unavailable,restarted.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));Assert.False(File.Exists(f.Disk.FilePath+".previous"));
 }
 [Fact] public async Task CorruptSecurityFileFailsClosed()
 {
  using var f=new SecurityFixture();Directory.CreateDirectory(f.DirectoryPath);await File.WriteAllTextAsync(f.Disk.FilePath,"corrupt");if(!OperatingSystem.IsWindows()){File.SetUnixFileMode(f.DirectoryPath,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);File.SetUnixFileMode(f.Disk.FilePath,UnixFileMode.UserRead|UnixFileMode.UserWrite);}Assert.False(await f.State.RestoreAsync(f.Keys));Assert.False(f.State.IsReady);Assert.Equal(SecurityAdmissionDecision.Unavailable,f.State.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));
 }
 [Fact] public async Task InvalidSignedCheckpointFailsClosedWithoutEscapingRestore()
 {
  using var f=new SecurityFixture();var pair=f.Pair(1,[Guid.NewGuid()]);
  var checkpoint=JsonNode.Parse(pair.Checkpoint.Bytes.Span)!.AsObject();checkpoint["signature"]=Convert.ToBase64String(new byte[64]);
  await using(var owner=f.Disk.AcquireOwnership()){await owner.WriteAtomicAsync(new SecurityNodeMaterial(JsonSerializer.SerializeToUtf8Bytes(checkpoint),pair.Lease.Bytes.ToArray()));await owner.CompleteCleanlyAsync();}
  await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,Guid.NewGuid()),f.Disk,f.Clock);
  Assert.False(await restarted.RestoreAsync(f.Keys));Assert.False(restarted.IsReady);
  Assert.Equal(SecurityAdmissionDecision.Unavailable,restarted.CanAdmit(Guid.NewGuid(),f.Clock.GetUtcNow()));
 }
 [Fact] public async Task CorruptRestoredHighWaterCannotBeClearedByAnOrdinarySignedUpdate()
 {
  using var f=new SecurityFixture();var revoked=Guid.NewGuid();var before=f.Pair(1,[revoked]);Assert.True((await f.State.ApplyAsync(before.Checkpoint,before.Lease)).Applied);
  var document=JsonNode.Parse(before.Checkpoint.Bytes.Span)!.AsObject();document["signature"]=Convert.ToBase64String(new byte[64]);
  await f.State.DisposeAsync();await using(var owner=f.Disk.AcquireOwnership()){await owner.WriteAtomicAsync(new SecurityNodeMaterial(JsonSerializer.SerializeToUtf8Bytes(document),before.Lease.Bytes.ToArray()));await owner.CompleteCleanlyAsync();}
  var instance=Guid.NewGuid();await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,instance),f.Disk,f.Clock);
  Assert.False(await restarted.RestoreAsync(f.Keys));
  var older=f.Pair(0,[],leaseInstance:instance);Assert.False((await restarted.ApplyAsync(older.Checkpoint,older.Lease)).Applied);
  var newer=f.Pair(2,[revoked],leaseInstance:instance);Assert.False((await restarted.ApplyAsync(newer.Checkpoint,newer.Lease)).Applied);
  Assert.False(restarted.IsReady);Assert.Equal(SecurityAdmissionDecision.Unavailable,restarted.CanAdmit(revoked,f.Clock.GetUtcNow()));
 }
 [Fact] public async Task CorruptPersistedLeaseLocksRecoveryEvenWithValidCheckpoint()
 {
  using var f=new SecurityFixture();var before=f.Pair(1,[Guid.NewGuid()]);
  var document=JsonNode.Parse(before.Lease.Bytes.Span)!.AsObject();document["signature"]=Convert.ToBase64String(new byte[64]);
  await using(var owner=f.Disk.AcquireOwnership()){await owner.WriteAtomicAsync(new SecurityNodeMaterial(before.Checkpoint.Bytes.ToArray(),JsonSerializer.SerializeToUtf8Bytes(document)));await owner.CompleteCleanlyAsync();}
  var instance=Guid.NewGuid();await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,instance),f.Disk,f.Clock);
  Assert.False(await restarted.RestoreAsync(f.Keys));var candidate=f.Pair(0,[],leaseInstance:instance);
  Assert.False((await restarted.ApplyAsync(candidate.Checkpoint,candidate.Lease)).Applied);Assert.False(restarted.IsReady);
 }
 [Fact] public async Task ApplyBeforeRestoreCannotOverwriteExistingHighWater()
 {
  using var f=new SecurityFixture();var revoked=Guid.NewGuid();var stored=f.Pair(1,[revoked]);Assert.True((await f.State.ApplyAsync(stored.Checkpoint,stored.Lease)).Applied);
  var original=await File.ReadAllBytesAsync(f.Disk.FilePath);await f.State.DisposeAsync();var instance=Guid.NewGuid();await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,instance),f.Disk,f.Clock);
  var stale=f.Pair(0,[],leaseInstance:instance);Assert.False((await restarted.ApplyAsync(stale.Checkpoint,stale.Lease)).Applied);
  Assert.Equal(original,await File.ReadAllBytesAsync(f.Disk.FilePath));Assert.True(await restarted.RestoreAsync(f.Keys));
  Assert.Equal(SecurityAdmissionDecision.KnownRevoked,restarted.CanAdmit(revoked,f.Clock.GetUtcNow()));Assert.False(restarted.IsReady);
 }
 [Fact] public async Task ConcurrentRestoreAndApplyCannotReplaceExistingHighWater()
 {
  using var f=new SecurityFixture();var revoked=Guid.NewGuid();var stored=f.Pair(1,[revoked]);Assert.True((await f.State.ApplyAsync(stored.Checkpoint,stored.Lease)).Applied);
  await f.State.DisposeAsync();var instance=Guid.NewGuid();await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,instance),f.Disk,f.Clock);var stale=f.Pair(0,[],leaseInstance:instance);
  var restore=restarted.RestoreAsync(f.Keys);var apply=restarted.ApplyAsync(stale.Checkpoint,stale.Lease);await Task.WhenAll(restore,apply);
  Assert.True(await restore);Assert.False((await apply).Applied);Assert.Equal(SecurityAdmissionDecision.KnownRevoked,restarted.CanAdmit(revoked,f.Clock.GetUtcNow()));Assert.False(restarted.IsReady);
 }
 [Fact] public async Task UnreadableRestoredHighWaterCannotBeClearedByAnOrdinarySignedUpdate()
 {
  using var f=new SecurityFixture();var instance=Guid.NewGuid();await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,instance),new UnreadableRecoveryStore(),f.Clock);
  Assert.False(await restarted.RestoreAsync(f.Keys));var candidate=f.Pair(0,[],leaseInstance:instance);
  Assert.False((await restarted.ApplyAsync(candidate.Checkpoint,candidate.Lease)).Applied);Assert.False(restarted.IsReady);
 }
 [Fact] public async Task RecreatedStoreCannotRefreshAnAlreadyPersistedLeaseForTheSameInstance()
 {
  using var f=new SecurityFixture();var revoked=Guid.NewGuid();var first=f.Pair(1,[revoked]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);
  await f.State.DisposeAsync();await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,f.Clock);Assert.True(await restarted.RestoreAsync(f.Keys));
  Assert.False((await restarted.ApplyAsync(first.Checkpoint,first.Lease)).Applied);Assert.False(restarted.IsReady);
  f.Clock.Advance(1);var fresh=f.Pair(1,[revoked]);Assert.True((await restarted.ApplyAsync(fresh.Checkpoint,fresh.Lease)).Applied);
  Assert.False((await restarted.ApplyAsync(first.Checkpoint,first.Lease)).Applied);Assert.True(restarted.IsReady);
 }
 [Fact] public async Task RejectedOldLeaseCannotHideHigherVerifiedEpochAfterRecovery()
 {
  using var f=new SecurityFixture();var first=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);
  await f.State.DisposeAsync();await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,f.Instance),f.Disk,f.Clock);Assert.True(await restarted.RestoreAsync(f.Keys));
  f.Clock.Advance(1);var renewed=f.Pair(1,[]);Assert.True((await restarted.ApplyAsync(renewed.Checkpoint,renewed.Lease)).Applied);
  f.Clock.ShiftUtc(-1);var higherWithOlderLease=f.Pair(2,[Guid.NewGuid()]);f.Clock.ShiftUtc(1);
  Assert.False((await restarted.ApplyAsync(higherWithOlderLease.Checkpoint,higherWithOlderLease.Lease)).Applied);
  Assert.False(restarted.IsReady);await restarted.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Theory]
 [InlineData("expired")]
 [InlineData("future")]
 public async Task RejectedNewerSignedLeaseCannotLoseItsObservedTimeFloor(string scenario)
 {
  using var f=new SecurityFixture();var first=f.Pair(1,[]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);
  if(scenario=="future")f.Clock.ShiftUtc(10);else f.Clock.Advance(1);
  var next=f.Pair(1,[]);var payload=next.Lease.Payload;
  if(scenario=="future")f.Clock.ShiftUtc(-10);
  else{payload=payload with{FreshUntil=payload.IssuedAt.AddSeconds(5)};f.Clock.Advance(6);}
  var signed=SecurityCheckpointVerifier.VerifyNodeLease(new(f.Signer.SignNodeLease(payload)),f.Keys,f.Env,f.Node,f.Instance);
  Assert.False((await f.State.ApplyAsync(next.Checkpoint,signed)).Applied);Assert.False(f.State.IsReady);
  await f.State.DisposeAsync();Assert.True(File.Exists(Path.Combine(f.DirectoryPath,".security-running")));
 }
 [Fact] public async Task EncodedSecurityFileCannotExceedReadLimitOrReplacePriorState()
 {
  using var f=new SecurityFixture();var firstId=Guid.NewGuid();var secondId=Guid.NewGuid();var first=f.Pair(1,[firstId]);
  Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);var before=await File.ReadAllBytesAsync(f.Disk.FilePath);
  var next=f.Pair(2,[firstId,secondId]);var padded=new byte[25*1024*1024];Array.Fill(padded,(byte)' ');next.Checkpoint.Bytes.CopyTo(padded);
  var verified=SecurityCheckpointVerifier.VerifyCheckpoint(new(padded),f.Keys,f.Env);
  Assert.False((await f.State.ApplyAsync(verified,next.Lease)).Applied);Assert.Equal(before,await File.ReadAllBytesAsync(f.Disk.FilePath));Assert.False(f.State.IsReady);
 }
 [Fact] public async Task GrowingInputCannotBeReadPastTheConfiguredSecurityFileLimit()
 {
  using var source=new MemoryStream(new byte[11]);using var bounded=new SecurityLkgStore.BoundedReadStream(source,10);
  await bounded.ReadExactlyAsync(new byte[10]);
  await Assert.ThrowsAsync<InvalidDataException>(async()=>{await bounded.ReadExactlyAsync(new byte[1]);});
 }
 [Fact] public async Task ConcurrentStoresCannotOwnTheSameSecurityDirectory()
 {
  using var f=new SecurityFixture();var revoked=Guid.NewGuid();var first=f.Pair(1,[revoked]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);
  var competingDisk=new SecurityLkgStore(f.DirectoryPath);var instance=Guid.NewGuid();
  await using var competing=new SecurityStateStore(new(f.Env,()=>f.Node,instance),competingDisk,f.Clock);
  Assert.False(await competing.RestoreAsync(f.Keys));var candidate=f.Pair(0,[],leaseInstance:instance);
  Assert.False((await competing.ApplyAsync(candidate.Checkpoint,candidate.Lease)).Applied);Assert.False(competing.IsReady);
  await f.State.DisposeAsync();
  Assert.True(await competing.RestoreAsync(f.Keys));Assert.Equal(SecurityAdmissionDecision.KnownRevoked,competing.CanAdmit(revoked,f.Clock.GetUtcNow()));Assert.False(competing.IsReady);
 }
 [Fact] public async Task ReleasedSecurityDirectoryOwnerCannotWrite()
 {
  using var f=new SecurityFixture();var pair=f.Pair(0,[]);var owner=f.Disk.AcquireOwnership();
  await owner.WriteAtomicAsync(new(pair.Checkpoint.Bytes.ToArray(),pair.Lease.Bytes.ToArray()));await owner.CompleteCleanlyAsync();await owner.DisposeAsync();
  await Assert.ThrowsAsync<ObjectDisposedException>(()=>owner.WriteAtomicAsync(new(pair.Checkpoint.Bytes.ToArray(),pair.Lease.Bytes.ToArray())));
 }
 [Fact] public async Task ReplacingAnOrdinaryLockFileCannotCreateASecondUnixOwner()
 {
  using var f=new SecurityFixture();var pair=f.Pair(0,[]);Assert.True((await f.State.ApplyAsync(pair.Checkpoint,pair.Lease)).Applied);
  if(OperatingSystem.IsLinux()||OperatingSystem.IsMacOS())
  {
   var obsolete=Path.Combine(f.DirectoryPath,".security-writer.lock");await File.WriteAllTextAsync(obsolete,"untrusted");File.Delete(obsolete);await File.WriteAllTextAsync(obsolete,"replacement");
  }
  await using var other=new SecurityStateStore(new(f.Env,()=>f.Node,Guid.NewGuid()),new SecurityLkgStore(f.DirectoryPath),f.Clock);
  Assert.False(await other.RestoreAsync(f.Keys));Assert.False(other.IsReady);
 }
 [Fact] public void SecurityDirectoryIsAnchoredToAnAbsolutePathAtConstruction()
 {
  using var f=new SecurityFixture();var relative=Path.GetRelativePath(Environment.CurrentDirectory,f.DirectoryPath);var store=new SecurityLkgStore(relative);
  Assert.True(Path.IsPathFullyQualified(store.FilePath));Assert.Equal("security.lkg",Path.GetFileName(store.FilePath));
 }
 [Fact] public async Task ChildProcessCannotKeepSecurityDirectoryOwnershipAfterRelease()
 {
  if(!OperatingSystem.IsLinux()&&!OperatingSystem.IsMacOS())return;
  using var f=new SecurityFixture();var owner=f.Disk.AcquireOwnership();
  using var child=Process.Start(new ProcessStartInfo("/bin/sleep","20"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true})!;
  try
  {
   Assert.False(child.HasExited);await owner.CompleteCleanlyAsync();await owner.DisposeAsync();
   await using var successor=new SecurityLkgStore(f.DirectoryPath).AcquireOwnership();Assert.False(child.HasExited);
  }
  finally{if(!child.HasExited){child.Kill();await child.WaitForExitAsync();}await owner.DisposeAsync();}
 }
 [Fact] public async Task WriteDeniedBlocksAdmission()
 {
  using var f=new SecurityFixture();var a=Guid.NewGuid();var b=Guid.NewGuid();var first=f.Pair(1,[a]);await f.State.ApplyAsync(first.Checkpoint,first.Lease);using var locked=OperatingSystem.IsWindows()?new FileStream(f.Disk.FilePath,FileMode.Open,FileAccess.Read,FileShare.None):null;if(!OperatingSystem.IsWindows())File.SetUnixFileMode(f.DirectoryPath,UnixFileMode.UserRead|UnixFileMode.UserExecute);var next=f.Pair(2,[a,b]);Assert.False((await f.State.ApplyAsync(next.Checkpoint,next.Lease)).Applied);Assert.False(f.State.IsReady);Assert.Equal(1,f.State.CurrentEpoch);Assert.Equal(SecurityAdmissionDecision.KnownRevoked,f.State.CanAdmit(a,f.Clock.GetUtcNow()));Assert.Equal(SecurityAdmissionDecision.Unavailable,f.State.CanAdmit(b,f.Clock.GetUtcNow()));
 }
 [Fact] public async Task WriteFailureCannotBeHealedByAnOlderCheckpoint()
 {
  using var f=new SecurityFixture();var a=Guid.NewGuid();var b=Guid.NewGuid();var first=f.Pair(1,[a]);await f.State.ApplyAsync(first.Checkpoint,first.Lease);var next=f.Pair(2,[a,b]);
  using(var locked=OperatingSystem.IsWindows()?new FileStream(f.Disk.FilePath,FileMode.Open,FileAccess.Read,FileShare.None):null){if(!OperatingSystem.IsWindows())File.SetUnixFileMode(f.DirectoryPath,UnixFileMode.UserRead|UnixFileMode.UserExecute);Assert.False((await f.State.ApplyAsync(next.Checkpoint,next.Lease)).Applied);}
  if(!OperatingSystem.IsWindows())File.SetUnixFileMode(f.DirectoryPath,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
  Assert.False((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);Assert.False(f.State.IsReady);Assert.True((await f.State.ApplyAsync(next.Checkpoint,next.Lease)).Applied);Assert.Equal(SecurityAdmissionDecision.KnownRevoked,f.State.CanAdmit(b,f.Clock.GetUtcNow()));
 }
 [Fact] public async Task FailedWriteThenRestartCannotReopenAnOlderPersistedHighWater()
 {
  using var f=new SecurityFixture();var a=Guid.NewGuid();var b=Guid.NewGuid();var first=f.Pair(1,[a]);Assert.True((await f.State.ApplyAsync(first.Checkpoint,first.Lease)).Applied);
  using(var locked=OperatingSystem.IsWindows()?new FileStream(f.Disk.FilePath,FileMode.Open,FileAccess.Read,FileShare.None):null)
  {
   if(!OperatingSystem.IsWindows())File.SetUnixFileMode(f.DirectoryPath,UnixFileMode.UserRead|UnixFileMode.UserExecute);
   var next=f.Pair(2,[a,b]);Assert.False((await f.State.ApplyAsync(next.Checkpoint,next.Lease)).Applied);
  }
  if(!OperatingSystem.IsWindows())File.SetUnixFileMode(f.DirectoryPath,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
  await f.State.DisposeAsync();f.Clock.Advance(1);var instance=Guid.NewGuid();
  await using var restarted=new SecurityStateStore(new(f.Env,()=>f.Node,instance),f.Disk,f.Clock);Assert.False(await restarted.RestoreAsync(f.Keys));
  var stale=f.Pair(1,[a],leaseInstance:instance);Assert.False((await restarted.ApplyAsync(stale.Checkpoint,stale.Lease)).Applied);Assert.False(restarted.IsReady);
 }
 private sealed class UnreadableRecoveryStore:ISecurityLkgStore
 {
  public ISecurityLkgOwner AcquireOwnership()=>new NoopOwner();
  private sealed class NoopOwner:ISecurityLkgOwner
  {
   public Task<SecurityNodeMaterial?> ReadAsync(CancellationToken ct=default)=>throw new IOException("isolated read failure");
   public Task WriteAtomicAsync(SecurityNodeMaterial material,CancellationToken ct=default)=>Task.CompletedTask;
   public Task CompleteCleanlyAsync(CancellationToken ct=default)=>Task.CompletedTask;
   public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
  }
 }
}

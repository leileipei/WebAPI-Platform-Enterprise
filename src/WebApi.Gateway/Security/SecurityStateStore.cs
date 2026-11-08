using System.Collections.Frozen;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
using WebApi.Domain.CredentialSecurity;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Storage;
namespace WebApi.Gateway.Security;
public sealed record SecurityNodeAudience(Guid EnvironmentId,Func<Guid?> NodeId,Guid InstanceId);
public enum SecurityAdmissionDecision {Allow,KnownRevoked,Unavailable}
public sealed record SecurityApplyResult(bool Applied,bool NeedsFullCheckpoint=false,string? ErrorCode=null);
public sealed class SecurityStateStore(SecurityNodeAudience audience,ISecurityLkgStore disk,TimeProvider clock,long maximumObservedFactBytes=24L*1024*1024):IAsyncDisposable
{
 private sealed class LeaseLifetime
 {
  private int expired;
  public bool IsExpired=>Volatile.Read(ref expired)!=0;
  public void MarkExpired()=>Volatile.Write(ref expired,1);
 }
 private sealed record ObservedHead(string Head,bool RequireEnvironment,string MinimumSchema,LinkedListNode<long> Order);
 private sealed record ClockAnchor(DateTimeOffset Utc,long Start,long End,long Frequency);
 private sealed record State(VerifiedCheckpoint Checkpoint,VerifiedNodeLease? Lease,FrozenDictionary<Guid,SecurityRevocationProof> Revocations,ClockAnchor Anchor,long ReceivedAt,long ExpiresAt,bool Blocked,LeaseLifetime Lifetime);
 private const int MaximumObservedHeads=4096;
 private readonly long observedFactBudget=maximumObservedFactBytes is >0 and <=24L*1024*1024?maximumObservedFactBytes:throw new ArgumentOutOfRangeException(nameof(maximumObservedFactBytes));
 private long observedFactBytes;private bool factCapacityExceeded;
 private readonly SecurityAdmissionGate admission=new();private readonly SemaphoreSlim updates=new(1,1);private readonly object timestampSync=new();private readonly Dictionary<Guid,SecurityRevocationProof> observedRevocations=[];private readonly Dictionary<long,ObservedHead> observedHeads=[];private readonly LinkedList<long> headObservationOrder=[];private State? current;private long requiredEpoch;private Guid? observedGeneration;private bool generationConflict;private long conflictEpoch=-1;private bool recoveryRequired;private bool recoveryChecked;private bool clockRegressed;private bool hasTimestamp;private long highestTimestamp;private long observedCheckpointEpoch=-1;private long observedFullEpoch=-1;private string? observedHead;private string observedMinimumSchema="2.0";private bool observedEnvironmentRequired;private bool disposed;private ISecurityLkgOwner? ownership;private DateTimeOffset? restoredLeaseIssuedAt;private DateTimeOffset? observedLeaseIssuedAt;private long observedLeaseEpoch=-1;private string? observedLeaseHash;private DateTimeOffset? conflictingLeaseIssuedAt;private long conflictingLeaseEpoch=-1;private Guid? restoredLeaseNode;
 public bool IsReady=>CanAdmitCore(Guid.Empty,null)==SecurityAdmissionDecision.Allow;
 public Guid CurrentAdmissionLeaseId=>Volatile.Read(ref current)?.Lease?.Payload.LeaseId??Guid.Empty;
 public (Guid LeaseId,Guid Generation,long Epoch)? CurrentAdmissionIdentity
 {get{var lease=Volatile.Read(ref current)?.Lease?.Payload;return lease is null?null:(lease.LeaseId,lease.SecurityGeneration,lease.Epoch);}}
 public long? CurrentEpoch=>Volatile.Read(ref current)?.Checkpoint.Payload.Epoch;
 public VerifiedCheckpoint? CurrentCheckpoint=>Volatile.Read(ref current)?.Checkpoint;
 public Task<SecurityAdmissionGate.Ticket> EnterAdmissionAsync(CancellationToken ct=default)=>admission.EnterReadAsync(ct);
 public bool CanUseSnapshot(RuntimeSnapshot snapshot)
 {
  var state=Volatile.Read(ref current);
  if(state is null||CanAdmitCore(Guid.Empty,null)!=SecurityAdmissionDecision.Allow)return false;
  try{SnapshotValidator.Validate(snapshot,audience.EnvironmentId);}catch(ApiException){return false;}
  var checkpoint=state.Checkpoint.Payload;
  if(!SnapshotSchemaRules.AtLeast(snapshot.SchemaVersion,checkpoint.MinimumSnapshotSchema))return false;
  var governance=snapshot.RequiredSecurityGeneration is not null;
  if(!governance)
  {
   // Network 2.3 does not imply the governance 2.3 safety contract.
   return checkpoint.MinimumSnapshotSchema is not ("2.3" or "2.4")&&ReferenceEquals(state,Volatile.Read(ref current));
  }
  if(checkpoint.MinimumSnapshotSchema is "2.3" or "2.4"&&snapshot.Applications.Any(a=>a.Permissions.Any(p=>p.Mode!="RouteMethod")))return false;
  return snapshot.RequiredSecurityGeneration==checkpoint.SecurityGeneration&&snapshot.RequiredSecurityEpoch is >=0&&snapshot.RequiredSecurityEpoch<=checkpoint.Epoch
   &&snapshot.MinimumSnapshotSchema==checkpoint.MinimumSnapshotSchema&&snapshot.RequireEnvironmentCredentials==checkpoint.RequireEnvironmentCredentials
   &&ReferenceEquals(state,Volatile.Read(ref current));
 }
 public bool CredentialEnvironmentAllowed(RuntimeCredential credential)
 {
  if(credential.BindingMode=="EnvironmentBound")return credential.EnvironmentId==audience.EnvironmentId;
  if(credential.BindingMode is not (null or "LegacyGlobal")||credential.EnvironmentId is not null)return false;
  return Volatile.Read(ref current)?.Checkpoint.Payload.RequireEnvironmentCredentials!=true;
 }
 public SecurityAdmissionDecision CanAdmit(Guid credentialId,DateTimeOffset utcNow)
  =>CanAdmitCore(credentialId,utcNow);
 private SecurityAdmissionDecision CanAdmitCore(Guid credentialId,DateTimeOffset? requestedUtc)
 {
  if(Volatile.Read(ref disposed))return SecurityAdmissionDecision.Unavailable;
  var state=Volatile.Read(ref current);
  if(state is null)return SecurityAdmissionDecision.Unavailable;
  var timestampValid=TryTimestamp(state,requestedUtc,out _,out _,out var sampledUtc,out _);
  if(Volatile.Read(ref disposed))return SecurityAdmissionDecision.Unavailable;
  if(state.Revocations.ContainsKey(credentialId))return SecurityAdmissionDecision.KnownRevoked;
  if(!timestampValid)return SecurityAdmissionDecision.Unavailable;
  var utcNow=requestedUtc??sampledUtc;
  if(state.Blocked||state.Lifetime.IsExpired||Volatile.Read(ref clockRegressed)||Volatile.Read(ref generationConflict)||state.Checkpoint.Payload.Epoch<Volatile.Read(ref requiredEpoch)||state.Lease is null||!state.Lease.Payload.AllowAuthenticatedTraffic||(state.Lease.Payload.IssuedAt-utcNow).TotalSeconds>state.Lease.Payload.MaximumClockSkewSeconds||(state.Checkpoint.Payload.IssuedAt-utcNow).TotalSeconds>state.Lease.Payload.MaximumClockSkewSeconds)return SecurityAdmissionDecision.Unavailable;
  return SecurityAdmissionDecision.Allow;
 }
 private SecurityApplyResult Block(string code,bool needsFull=false)
 {if(current is not null)Volatile.Write(ref current,current with {Blocked=true});return new(false,needsFull,code);}
 private bool TryTimestamp(State? state,DateTimeOffset? requestUtc,out long start,out long stamp,out DateTimeOffset utc,out long frequency,bool closing=false)
 {
  // Bracket each wall-clock reading with monotonic readings under one lock.
  // The sampling interval accounts for preemption without inventing a fixed
  // tolerance or comparing different reads as one admission timestamp.
  start=stamp=frequency=0;utc=default;
  lock(timestampSync)
  {
   if(Volatile.Read(ref disposed)&&!closing)return false;
   try
   {
    start=clock.GetTimestamp();utc=clock.GetUtcNow();stamp=clock.GetTimestamp();frequency=clock.TimestampFrequency;
    if(frequency<=0||stamp<start||hasTimestamp&&start<highestTimestamp)Volatile.Write(ref clockRegressed,true);else{highestTimestamp=stamp;hasTimestamp=true;}
    if(state is not null)
    {
     if(stamp<state.ReceivedAt||state.Lease is not null&&!ClockAligned(state.Anchor,state.Lease.Payload.MaximumClockSkewSeconds,start,stamp,utc,frequency))Volatile.Write(ref clockRegressed,true);
     if(state.Lease is not null&&(requestUtc>=state.Lease.Payload.FreshUntil||requestUtc>=state.Checkpoint.Payload.FreshUntil||utc>=state.Lease.Payload.FreshUntil||utc>=state.Checkpoint.Payload.FreshUntil||stamp>=state.ExpiresAt))state.Lifetime.MarkExpired();
    }
    return !Volatile.Read(ref clockRegressed);
   }
   catch(Exception error)when(error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException){Volatile.Write(ref clockRegressed,true);return false;}
  }
 }
 private static bool SampleWidthAcceptable(long start,long end,int maximumSkew,long frequency)
 {
  if(frequency<=0)return false;
  return end>=start&&((double)end-start)/frequency<=2d*maximumSkew+2d/TimeSpan.TicksPerSecond;
 }
 private static bool ClockAligned(ClockAnchor anchor,int maximumSkew,long start,long end,DateTimeOffset utc,long frequency)
 {
  if(frequency!=anchor.Frequency||!SampleWidthAcceptable(start,end,maximumSkew,frequency))return false;
  var lower=((double)start-anchor.End)/frequency;var upper=((double)end-anchor.Start)/frequency;
  // DateTimeOffset has 100 ns ticks; two wall readings can differ by at most
  // two such representation ticks even when enclosed by monotonic readings.
  var elapsed=(utc-anchor.Utc).TotalSeconds;var skew=maximumSkew+2d/TimeSpan.TicksPerSecond;
  // Every possible position within both sampling brackets must respect the
  // allowed drift; a wide or ambiguous window cannot increase the allowance.
  return elapsed>=upper-skew&&elapsed<=lower+skew;
 }
 private bool ObserveCheckpoint(SecurityCheckpointPayload p)
 {
  // Verified facts remain security facts even when a later disk write or
  // freshness check fails. Partial checkpoints add facts but cannot assert
  // that absent facts have disappeared.
  var incoming=p.Revocations.ToDictionary(x=>x.CredentialId);var conflict=false;
  if(observedHeads.TryGetValue(p.Epoch,out var previousHead))
  {headObservationOrder.Remove(previousHead.Order);headObservationOrder.AddLast(previousHead.Order);if(previousHead.Head!=p.HeadHash||previousHead.RequireEnvironment!=p.RequireEnvironmentCredentials||previousHead.MinimumSchema!=p.MinimumSnapshotSchema){Volatile.Write(ref recoveryRequired,true);conflict=true;}}
  else
  {
   var order=headObservationOrder.AddLast(p.Epoch);observedHeads.Add(p.Epoch,new(p.HeadHash,p.RequireEnvironmentCredentials,p.MinimumSnapshotSchema,order));
   if(observedHeads.Count>MaximumObservedHeads){var oldest=headObservationOrder.First!;headObservationOrder.RemoveFirst();observedHeads.Remove(oldest.Value);}
  }
  // Unknown older heads are cached only as bounded comparison evidence, not
  // adopted as authority. Evicted historical chains need independent proof.
  foreach(var fact in p.Revocations)
  {
   if(observedRevocations.TryGetValue(fact.CredentialId,out var old))
   {if(old!=fact){Volatile.Write(ref recoveryRequired,true);conflict=true;}}
   else if(fact.Epoch<=observedFullEpoch)conflict=true;
  }
  if(p.Full&&observedRevocations.Values.Any(old=>old.Epoch<=p.Epoch&&(!incoming.TryGetValue(old.CredentialId,out var fact)||fact!=old)))conflict=true;
  if(p.Epoch==observedCheckpointEpoch&&(p.HeadHash!=observedHead||p.RequireEnvironmentCredentials!=observedEnvironmentRequired||p.MinimumSnapshotSchema!=observedMinimumSchema))conflict=true;
  if(p.Epoch>observedCheckpointEpoch&&(observedEnvironmentRequired&&!p.RequireEnvironmentCredentials||!SnapshotSchemaRules.AtLeast(p.MinimumSnapshotSchema,observedMinimumSchema)))conflict=true;
  if(p.Epoch<observedCheckpointEpoch&&(p.RequireEnvironmentCredentials&&!observedEnvironmentRequired||!SnapshotSchemaRules.AtLeast(observedMinimumSchema,p.MinimumSnapshotSchema)))conflict=true;
  foreach(var fact in p.Revocations)
  {
   if(observedRevocations.ContainsKey(fact.CredentialId))continue;
   // Even the bare facts must fit beneath the decoded 32 MiB LKG budget;
   // the complete signed checkpoint and Base64 wrapper need additional space.
   var cost=CanonicalJson.Serialize(fact).LongLength+1;
   if(cost>observedFactBudget-observedFactBytes){factCapacityExceeded=true;Volatile.Write(ref recoveryRequired,true);return false;}
   observedRevocations.Add(fact.CredentialId,fact);observedFactBytes+=cost;
  }
  if(p.Epoch>observedCheckpointEpoch){observedCheckpointEpoch=p.Epoch;observedHead=p.HeadHash;}
  observedEnvironmentRequired|=p.RequireEnvironmentCredentials;
  if(SnapshotSchemaRules.AtLeast(p.MinimumSnapshotSchema,observedMinimumSchema))observedMinimumSchema=p.MinimumSnapshotSchema;
  if(conflict){conflictEpoch=Math.Max(conflictEpoch,p.Epoch);return false;}
  if(p.Full)observedFullEpoch=Math.Max(observedFullEpoch,p.Epoch);
  return true;
 }
 public async Task<SecurityApplyResult> ApplyAsync(VerifiedCheckpoint checkpoint,VerifiedNodeLease lease,CancellationToken ct=default)
 {
  await updates.WaitAsync(ct);try{return await ApplyCoreAsync(checkpoint,lease,ct);}finally{updates.Release();}
 }
 private async Task<SecurityApplyResult> ApplyCoreAsync(VerifiedCheckpoint checkpoint,VerifiedNodeLease lease,CancellationToken ct)
 {
  using var ticket=await admission.EnterWriteAsync(ct);var p=checkpoint.Payload;var l=lease.Payload;var prior=current;
  if(Volatile.Read(ref disposed))return new(false,ErrorCode:"security_store_disposed");
  if(Volatile.Read(ref clockRegressed))return Block("security_monotonic_clock_regressed");
  if(Volatile.Read(ref recoveryRequired))return Block("security_lkg_recovery_required");
  try{ownership??=disk.AcquireOwnership();}
  catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or PlatformNotSupportedException){return Block("security_lkg_owner_unavailable");}
  if(!recoveryChecked)
  {
   try
   {
    // Never replace an existing file before its authenticated high-water is
    // restored. A missing file is not proof of trusted first installation;
    // that separate provenance check remains required at gateway integration.
    if(await ownership.ReadAsync(ct) is not null)return new(false,ErrorCode:"security_lkg_restore_required");
    recoveryChecked=true;
   }
   catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
   {Volatile.Write(ref recoveryRequired,true);return Block("security_lkg_recovery_required");}
  }
  if(p.EnvironmentId!=audience.EnvironmentId||l.EnvironmentId!=audience.EnvironmentId||l.NodeId!=audience.NodeId()||l.InstanceId!=audience.InstanceId)return new(false,ErrorCode:"security_node_audience_mismatch");
  if(l.SecurityGeneration!=p.SecurityGeneration||l.Epoch!=p.Epoch||l.CheckpointHash!=checkpoint.Hash||l.FreshUntil>p.FreshUntil)return new(false,ErrorCode:"security_checkpoint_lease_mismatch");
  if(restoredLeaseNode is Guid persistedNode&&persistedNode!=l.NodeId)return Block("security_lease_revalidation_required");
  if(Volatile.Read(ref generationConflict))return Block("security_generation_recovery_required");
  if(observedGeneration is Guid observed&&p.SecurityGeneration!=observed){Volatile.Write(ref generationConflict,true);return Block("security_generation_changed");}
  observedGeneration??=p.SecurityGeneration;
  if(!ObserveCheckpoint(p))return Block(factCapacityExceeded?"security_observed_fact_capacity_exceeded":"security_observed_checkpoint_conflict");
  if(p.Epoch<=conflictEpoch)return Block("security_epoch_conflict_recovery_required");
  if(p.Epoch<requiredEpoch)return new(false,ErrorCode:"security_epoch_below_observed_floor");
  Volatile.Write(ref requiredEpoch,Math.Max(requiredEpoch,p.Epoch));
  if(observedLeaseIssuedAt is DateTimeOffset observedAt&&l.IssuedAt<observedAt)return new(false,ErrorCode:"security_lease_below_observed_floor");
  var leaseHash=SecuritySignedEncoding.LeaseHash(l);
  if(conflictingLeaseIssuedAt is DateTimeOffset conflictAt&&p.Epoch==conflictingLeaseEpoch&&l.IssuedAt<=conflictAt)return Block("security_lease_time_conflict");
  if(observedLeaseIssuedAt==l.IssuedAt&&observedLeaseEpoch==p.Epoch&&observedLeaseHash is not null&&observedLeaseHash!=leaseHash)
  {conflictingLeaseIssuedAt=l.IssuedAt;conflictingLeaseEpoch=p.Epoch;return Block("security_lease_time_conflict");}
  var advancesLeaseFloor=observedLeaseIssuedAt is null||l.IssuedAt>observedLeaseIssuedAt;
  if(advancesLeaseFloor||p.Epoch>observedLeaseEpoch){observedLeaseIssuedAt=l.IssuedAt;observedLeaseEpoch=p.Epoch;observedLeaseHash=leaseHash;}
  if(conflictingLeaseIssuedAt is DateTimeOffset priorConflict&&(p.Epoch>conflictingLeaseEpoch||l.IssuedAt>priorConflict)){conflictingLeaseIssuedAt=null;conflictingLeaseEpoch=-1;}
  // Once a scoped, signed pair is known to match, its checkpoint floor must
  // survive even when its lease is too old to authorize this process.
  if(restoredLeaseIssuedAt is DateTimeOffset persistedAt&&l.IssuedAt<=persistedAt)return new(false,ErrorCode:"security_lease_revalidation_required");
  if(prior is not null)
  {
   if(p.SecurityGeneration!=prior.Checkpoint.Payload.SecurityGeneration){Volatile.Write(ref generationConflict,true);return Block("security_generation_changed");}
   if(p.Epoch<prior.Checkpoint.Payload.Epoch)return new(false,ErrorCode:"stale_security_epoch");
  }
  if(!p.Full)return Block("security_full_checkpoint_required",true);
  var facts=p.Revocations.ToFrozenDictionary(x=>x.CredentialId);
  if(prior is not null)
  {
   if(prior.Revocations.Any(x=>!facts.TryGetValue(x.Key,out var fact)||fact!=x.Value))return Block("security_tombstone_regression");
   var old=prior.Checkpoint.Payload;
   if(!SnapshotSchemaRules.AtLeast(p.MinimumSnapshotSchema,old.MinimumSnapshotSchema)||old.RequireEnvironmentCredentials&&!p.RequireEnvironmentCredentials){conflictEpoch=Math.Max(conflictEpoch,p.Epoch);return Block("security_epoch_conflict");}
   if(p.Epoch==old.Epoch&&(p.HeadHash!=old.HeadHash||facts.Count!=prior.Revocations.Count||p.RequireEnvironmentCredentials!=old.RequireEnvironmentCredentials||p.MinimumSnapshotSchema!=old.MinimumSnapshotSchema)){conflictEpoch=Math.Max(conflictEpoch,p.Epoch);return Block("security_epoch_conflict");}
  }
  if(!TryTimestamp(prior,null,out var start,out var stamp,out var now,out var frequency)||!SampleWidthAcceptable(start,stamp,l.MaximumClockSkewSeconds,frequency)){Volatile.Write(ref clockRegressed,true);return Block("security_clock_unreliable");}
  // The process anchor is not refreshed by retries or later leases: otherwise
  // many individually small wall-clock adjustments could hide large drift.
  var anchor=prior?.Lease is not null?prior.Anchor:new ClockAnchor(now,start,stamp,frequency);
  if(!ClockAligned(anchor,l.MaximumClockSkewSeconds,start,stamp,now,frequency)){Volatile.Write(ref clockRegressed,true);return Block("security_clock_unreliable");}
  if((p.IssuedAt-now).TotalSeconds>l.MaximumClockSkewSeconds||(l.IssuedAt-now).TotalSeconds>l.MaximumClockSkewSeconds||now>=p.FreshUntil||now>=l.FreshUntil)
   return advancesLeaseFloor?Block("security_lease_expired_or_future"):new(false,ErrorCode:"security_lease_expired_or_future");
  long deadline;
  try
  {
   var remaining=Math.Min((l.FreshUntil-now).Ticks,TimeSpan.FromSeconds(l.LeaseSeconds+l.MaximumClockSkewSeconds).Ticks);
   var delta=decimal.Floor((decimal)remaining*frequency/TimeSpan.TicksPerSecond);
   deadline=checked(start+(long)delta);
  }
  catch(OverflowException){return Block("security_clock_unreliable");}
  if(stamp>=deadline)return Block("security_lease_expired_during_clock_sample");
  // An older/same issued lease cannot gain a new monotonic deadline, even after wall-clock rollback.
  if(prior?.Lease is not null&&l.IssuedAt<=prior.Lease.Payload.IssuedAt)
  {
   if(l.IssuedAt<prior.Lease.Payload.IssuedAt||stamp>=prior.ExpiresAt||prior.Lifetime.IsExpired)return new(false,ErrorCode:"stale_security_lease");deadline=Math.Min(deadline,prior.ExpiresAt);
  }
  // Only a healthy renewal of exactly the same authority may keep serving its old lease while fsync runs.
  // All changes to denial facts or policy retain exclusive admission through durable publication.
  var renewal=prior is not null&&!prior.Blocked&&prior.Lease is not null&&p.Epoch==prior.Checkpoint.Payload.Epoch&&
   (!prior.Lease.Payload.AllowAuthenticatedTraffic||l.AllowAuthenticatedTraffic&&l.FreshUntil>=prior.Lease.Payload.FreshUntil&&p.FreshUntil>=prior.Checkpoint.Payload.FreshUntil&&deadline>=prior.ExpiresAt);
  if(renewal)ticket.Dispose();
  var persistenceFailed=false;Exception? interrupted=null;
  try{await ownership.WriteAtomicAsync(new(checkpoint.Bytes.ToArray(),lease.Bytes.ToArray()),ct);}
  catch(OperationCanceledException error){persistenceFailed=true;interrupted=error;}
  catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException){persistenceFailed=true;}
  catch(Exception error)when(error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException){persistenceFailed=true;interrupted=error;}
  // Once persistence has started, cancellation cannot interrupt either the
  // failure-close transition or publication of a completed durable write.
  using var publication=renewal?await admission.EnterWriteAsync(CancellationToken.None):null;
  if(persistenceFailed){var result=Block("security_lkg_write_denied");if(interrupted is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(interrupted).Throw();return result;}
  var lifetime=prior?.Lease?.Payload.LeaseId==l.LeaseId?prior.Lifetime:new LeaseLifetime();
  if(!TryTimestamp(prior,null,out var afterStart,out var afterStamp,out var afterUtc,out var afterFrequency)||!ClockAligned(anchor,l.MaximumClockSkewSeconds,afterStart,afterStamp,afterUtc,afterFrequency))
  {Volatile.Write(ref clockRegressed,true);return Block("security_clock_unreliable");}
  if(lifetime.IsExpired||afterStamp>=deadline||afterUtc>=l.FreshUntil||afterUtc>=p.FreshUntil)return Block("security_lease_expired_before_publication");
  Volatile.Write(ref current,new(checkpoint,lease,facts,anchor,stamp,deadline,false,lifetime));return new(true);
 }
 public async Task<bool> RestoreAsync(TrustedSigningKeys keys,CancellationToken ct=default)
 {
  using var ticket=await admission.EnterWriteAsync(ct);if(Volatile.Read(ref disposed)||Volatile.Read(ref recoveryRequired))return false;if(current is not null)return true;
  try{ownership??=disk.AcquireOwnership();}
  catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or PlatformNotSupportedException){return false;}
  try
  {
   var stored=await ownership.ReadAsync(ct);if(stored is null){recoveryChecked=true;return false;}var checkpoint=SecurityCheckpointVerifier.VerifyCheckpoint(new(stored.Checkpoint),keys,audience.EnvironmentId);
   var previousLease=SecurityCheckpointVerifier.VerifyPersistedNodeLease(new(stored.Lease),keys,audience.EnvironmentId);var l=previousLease.Payload;
   if(!checkpoint.Payload.Full||l.CheckpointHash!=checkpoint.Hash||l.SecurityGeneration!=checkpoint.Payload.SecurityGeneration||l.Epoch!=checkpoint.Payload.Epoch||l.FreshUntil>checkpoint.Payload.FreshUntil||audience.NodeId() is Guid currentNode&&currentNode!=l.NodeId)
   {Volatile.Write(ref recoveryRequired,true);return false;}
   // Never restore freshness from a former process instance, even if its old lease is still parseable.
   if(!ObserveCheckpoint(checkpoint.Payload)){Volatile.Write(ref recoveryRequired,true);return false;}
   recoveryChecked=true;restoredLeaseIssuedAt=l.IssuedAt;observedLeaseIssuedAt=l.IssuedAt;observedLeaseEpoch=l.Epoch;observedLeaseHash=SecuritySignedEncoding.LeaseHash(l);restoredLeaseNode=l.NodeId;observedGeneration=checkpoint.Payload.SecurityGeneration;Volatile.Write(ref requiredEpoch,checkpoint.Payload.Epoch);Volatile.Write(ref current,new(checkpoint,null,checkpoint.Payload.Revocations.ToFrozenDictionary(x=>x.CredentialId),new ClockAnchor(DateTimeOffset.MinValue,0,0,0),0,0,true,new LeaseLifetime()));return true;
  }
  catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException){Volatile.Write(ref recoveryRequired,true);return false;}
 }
 public async ValueTask DisposeAsync()
 {
  await updates.WaitAsync();try{using var ticket=await admission.EnterWriteAsync();
   // Publish the shutdown barrier under the sampling lock, so an earlier
   // readiness read cannot start a new clock observation after the snapshot.
   lock(timestampSync){if(Volatile.Read(ref disposed))return;Volatile.Write(ref disposed,true);}
   if(current is not null&&!TryTimestamp(current,null,out _,out _,out _,out _,closing:true))Volatile.Write(ref clockRegressed,true);
   var leasePersisted=observedLeaseIssuedAt is not DateTimeOffset leaseFloor||current?.Lease?.Payload.IssuedAt>=leaseFloor;
   var clean=current is not null&&!current.Blocked&&!Volatile.Read(ref recoveryRequired)&&!Volatile.Read(ref clockRegressed)&&!Volatile.Read(ref generationConflict)&&current.Checkpoint.Payload.Epoch>=Volatile.Read(ref requiredEpoch)&&leasePersisted;
   if(current is not null)Volatile.Write(ref current,current with{Blocked=true});
   if(ownership is not null){try{if(clean)await ownership.CompleteCleanlyAsync();}finally{await ownership.DisposeAsync();ownership=null;}}
  }finally{updates.Release();}
 }
}

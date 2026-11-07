using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.CredentialSecurity;
public sealed class SecurityAckService(WebApiDbContext db,NodeIdentityService identities,SecurityLedgerReader reader,SecuritySigningSettings signing)
{
 private static readonly HashSet<string> Errors=["security_lkg_write_denied","security_material_invalid","security_generation_recovery_required","security_state_unavailable","security_full_checkpoint_required","security_epoch_conflict","security_tombstone_regression"];
 private static ApiException Conflict()=>new(409,"security_ack_conflict","安全确认与当前节点或冻结目标不一致。");
 // Failure reports are authenticated separately from signed success proofs:
 // the caller must first authenticate the node identity at its ingress.
 public async Task<SecurityAckFailureResult> RecordFailureAsync(Guid nodeId,SecurityAckFailure request,NodeIdentity identity,CancellationToken ct=default)
 {
  if(db.Database.CurrentTransaction is not null||db.ChangeTracker.HasChanges())throw new InvalidOperationException("Security failure reporting requires its own clean transaction.");
  db.ChangeTracker.Clear();
  if(nodeId!=request.NodeId||nodeId!=identity.NodeId||request.EnvironmentId!=identity.EnvironmentId||request.InstanceId==Guid.Empty||request.OperationId==Guid.Empty||request.ChallengeNonce==Guid.Empty||request.ObservationSequence<=0||!Errors.Contains(request.ErrorCode))throw Conflict();
  bool duplicate;
  await using(var tx=await db.Database.BeginTransactionAsync(ct))
  {
   await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)",ct);
   await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={identity.EnvironmentId} FOR NO KEY UPDATE",ct);
   await identities.RevalidateAsync(identity,request.InstanceId,ct);
   var target=await db.Set<SecurityOperationTarget>().AsNoTracking().SingleOrDefaultAsync(x=>x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId,ct)??throw Conflict();
   var now=await SecurityDatabaseClock.ObserveAsync(db,ct);
   if(await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db,now,ct))throw Conflict();
   if(await db.Set<SecurityAckVerification>().AsNoTracking().AnyAsync(x=>x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId,ct))throw Conflict();
   var attempts=db.Set<SecurityAckAttempt>().AsNoTracking().Where(x=>x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId);
   var historical=await attempts.MaxAsync(x=>(DateTimeOffset?)x.ReceivedAt,ct);
   var historicalSequence=await attempts.MaxAsync(x=>x.ObservationSequence,ct);
   if(historical>now)throw Conflict();
   var latest=await db.Set<SecurityAckFailureLatest>().SingleOrDefaultAsync(x=>x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId,ct);
   if(historicalSequence>(latest?.LatestSequence??0)||latest is not null&&(now<latest.LastAcceptedAt||historical>latest.LastAcceptedAt||request.ObservationSequence<latest.LatestSequence||request.ObservationSequence==latest.LatestSequence&&(latest.ErrorCode!=request.ErrorCode||latest.ReportedNonce!=request.ChallengeNonce)))throw Conflict();
   if(latest is not null&&await attempts.AnyAsync(x=>x.ObservationSequence==latest.LatestSequence&&(x.ErrorCode!=latest.ErrorCode||x.ReportedNonce!=latest.ReportedNonce),ct))throw Conflict();
   duplicate=latest is not null&&request.ObservationSequence==latest.LatestSequence;
   if(!duplicate)
   {
    if(latest is null?request.ObservationSequence!=1:request.ObservationSequence-latest.LatestSequence!=1)throw Conflict();
    // An exact retry of an already accepted observation is idempotent even
    // after its short-lived revalidation challenge expires. New observations
    // must still present a challenge valid at the database's current time.
    if(request.ChallengeNonce!=target.ChallengeNonce)
    {
     var reported=await db.Set<SecurityAckRevalidationChallenge>().AsNoTracking().SingleOrDefaultAsync(x=>x.Nonce==request.ChallengeNonce&&x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId,ct);
     if(reported is null||reported.CreatedAt>now)throw Conflict();
     if(reported.ExpiresAt<=now)
     {
      using var clockBudget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
      await tx.CommitAsync(clockBudget.Token);
      throw Conflict();
     }
    }
    if(latest is null)
    {latest=new SecurityAckFailureLatest{OperationId=request.OperationId,EnvironmentId=request.EnvironmentId,NodeId=nodeId,InstanceGuid=request.InstanceId,ReportedNonce=request.ChallengeNonce,ErrorCode=request.ErrorCode,LastAcceptedAt=now,LatestSequence=request.ObservationSequence};db.Add(latest);}
    else
    {latest.ReportedNonce=request.ChallengeNonce;latest.ErrorCode=request.ErrorCode;latest.LastAcceptedAt=now;latest.LatestSequence=request.ObservationSequence;latest.ObservationCount=checked(latest.ObservationCount+1);}
    // Preserve the first immutable fact for each error while the bounded
    // per-target high-water distinguishes A(1)→B(2)→A(3) from replay of A(1).
    var seen=await db.Set<SecurityAckAttempt>().AsNoTracking().AnyAsync(x=>x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId&&x.ErrorCode==request.ErrorCode,ct);
    if(!seen)
    {
     var hash=Convert.ToHexStringLower(SHA256.HashData(CanonicalJson.Serialize(request)));
     db.Add(new SecurityAckAttempt{OperationId=request.OperationId,EnvironmentId=request.EnvironmentId,NodeId=nodeId,InstanceGuid=request.InstanceId,ChallengeNonce=target.ChallengeNonce,ReportedNonce=request.ChallengeNonce,ObservationSequence=request.ObservationSequence,RequestHash=hash,ErrorCode=request.ErrorCode,ReceivedAt=now});
    }
    try{await db.SaveChangesAsync(ct);}
    catch(DbUpdateException error) when(error.InnerException is PostgresException {SqlState:PostgresErrorCodes.CheckViolation,MessageText:"security_failure_nonce_invalid" or "security_failure_time_invalid" or "security_failure_sequence_invalid"})
    {
     // The challenge or database time may change between the precheck and
     // trigger. Roll back as a conflict; don't disguise unrelated DB faults.
     db.ChangeTracker.Clear();
     throw Conflict();
    }
   }
   await tx.CommitAsync(ct);
  }
  // A node is authenticated only for its own environment; do not disclose the
  // aggregate status of other environments through this diagnostic response.
  return new(duplicate);
 }
 public async Task<SecurityAckResult> RecordAsync(Guid nodeId,SecurityAck request,NodeIdentity identity,CancellationToken ct=default)
 {
  if(db.Database.CurrentTransaction is not null||db.ChangeTracker.HasChanges())throw new InvalidOperationException("Security ACK requires its own clean transaction.");
  db.ChangeTracker.Clear();
  if(nodeId!=request.NodeId||nodeId!=identity.NodeId||request.EnvironmentId!=identity.EnvironmentId||request.InstanceId==Guid.Empty||request.OperationId==Guid.Empty||request.ChallengeNonce==Guid.Empty||request.Checkpoint is null||request.Lease is null)throw Conflict();
  // Failures have a separate authenticated, bounded report path; accepting
  // them here would allow changing signed timestamps to bypass deduplication.
  if(!request.Success||!request.Durable||!request.AdmissionEnforced||request.ErrorCode is not null)throw Conflict();
  var keys=signing.LoadTrustedKeys();VerifiedCheckpoint cp;VerifiedNodeLease lease;
  try{cp=SecurityCheckpointVerifier.VerifyCheckpoint(new(request.Checkpoint),keys,identity.EnvironmentId);lease=SecurityCheckpointVerifier.VerifyNodeLease(new(request.Lease),keys,identity.EnvironmentId,nodeId,request.InstanceId);}catch(Exception error)when(error is InvalidDataException or ArgumentException){throw Conflict();}
  var p=cp.Payload;var l=lease.Payload;
  if(!p.Full||l.CheckpointHash!=cp.Hash||l.FreshUntil>p.FreshUntil||request.CheckpointHash!=cp.Hash||request.Epoch!=p.Epoch||l.Epoch!=p.Epoch||request.SecurityGeneration!=p.SecurityGeneration||l.SecurityGeneration!=p.SecurityGeneration||request.AppliedAt.Offset!=TimeSpan.Zero||request.AppliedAt==DateTimeOffset.MinValue||request.AppliedAt<l.IssuedAt.AddSeconds(-l.MaximumClockSkewSeconds)||request.AppliedAt<p.IssuedAt.AddSeconds(-l.MaximumClockSkewSeconds)||request.AppliedAt>=l.FreshUntil||request.AppliedAt>=p.FreshUntil)throw Conflict();
  var duplicate=false;
  await using(var tx=await db.Database.BeginTransactionAsync(ct))
  {
  await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)",ct);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={identity.EnvironmentId} FOR NO KEY UPDATE",ct);var node=await identities.RevalidateAsync(identity,request.InstanceId,ct);
  var target=await db.Set<SecurityOperationTarget>().SingleOrDefaultAsync(x=>x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId,ct)??throw Conflict();if(!target.ProtocolSupportedAtFreeze)throw Conflict();
  var authority=await reader.ReadCompleteLockedAsync(identity.EnvironmentId,ct);CurrentSecurityProofService.RequireFullProof(p,authority);if(!SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(node,authority.MinimumSnapshotSchema))throw Conflict();var required=authority.Events.SingleOrDefault(x=>x.OperationId==request.OperationId)??throw Conflict();var proof=p.Revocations.SingleOrDefault(x=>x.OperationId==request.OperationId);
  if(required.SecurityGeneration!=p.SecurityGeneration||p.Epoch<required.Epoch||p.Epoch>authority.Epoch||p.HeadHash!=authority.Events.Single(x=>x.Epoch==p.Epoch).EventHash||!l.AckChallenges!.Any(x=>x.OperationId==request.OperationId&&x.ChallengeNonce==request.ChallengeNonce&&x.RequiredEpoch==required.Epoch&&x.CredentialId==required.CredentialId&&x.EventType==(required.EventType=="EnvironmentPolicyChanged"?required.EventType:null)))throw Conflict();
  if(required.EventType=="CredentialRevoked"&&(proof is null||proof.CredentialId!=required.CredentialId||proof.Epoch!=required.Epoch||proof.EventHash!=required.EventHash)||required.EventType=="EnvironmentPolicyChanged"&&proof is not null)throw Conflict();
  var policy=SecurityPolicyRules.AtEpoch(authority,p.Epoch);if(policy.MinimumSnapshotSchema!=p.MinimumSnapshotSchema||policy.RequireEnvironmentCredentials!=p.RequireEnvironmentCredentials)throw Conflict();
  var issued=await db.Set<GatewaySecurityLease>().AsNoTracking().SingleOrDefaultAsync(x=>x.NodeId==nodeId&&x.InstanceId==request.InstanceId,ct)??throw Conflict();var now=await SecurityDatabaseClock.ObserveAsync(db,ct);
  // A valid signed lease can expire while the following checks are running.
  // Bound those checks independently of a disconnected client.
  using var validationBudget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
  var validationToken=validationBudget.Token;
  if(issued.EnvironmentId!=identity.EnvironmentId||issued.SecurityGeneration!=l.SecurityGeneration||issued.Epoch<l.Epoch||issued.LastIssuedLeaseUntil<l.FreshUntil||now<issued.IssuedAt||!await SecurityLeaseIssuanceEvidence.ContainsAsync(db,l,issued.OrganizationId,issued.ProjectId,validationToken)||request.AppliedAt>now.AddSeconds(l.MaximumClockSkewSeconds))throw Conflict();
  var appliedAt=request.AppliedAt.AddTicks(-(request.AppliedAt.Ticks%10));
  var previous=await db.Set<SecurityAckRecord>().AsNoTracking().SingleOrDefaultAsync(x=>x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId,validationToken);
  var verified=await db.Set<SecurityAckVerification>().AsNoTracking().SingleOrDefaultAsync(x=>x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId,validationToken);
  // Preserve exact idempotent retries of an immutable verified ACK, but never
  // create or revalidate one using a lease already observed expired.
  if(verified is null&&(await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db,now,validationToken)||await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().AnyAsync(x=>x.LeaseId==l.LeaseId,validationToken)))throw Conflict();
  if(verified is null&&l.FreshUntil<=now)
  {
   await SecurityLeaseExpiryRecorder.RecordAsync(db,l.LeaseId,now,validationToken);
   await tx.CommitAsync(validationToken);
   throw Conflict();
  }
  if(verified is not null&&l.FreshUntil<=now)
  {
   if(request.ChallengeNonce!=(verified.RevalidationNonce??target.ChallengeNonce)||verified.SecurityGeneration!=request.SecurityGeneration||verified.Epoch!=request.Epoch||verified.CheckpointHash!=request.CheckpointHash||verified.ChallengeNonce!=target.ChallengeNonce||verified.AppliedAt!=appliedAt||verified.LeaseId!=l.LeaseId)throw Conflict();
   await SecurityLeaseExpiryRecorder.RecordAsync(db,l.LeaseId,now,validationToken);
   await tx.CommitAsync(validationToken);
   return new(true);
  }
  SecurityAckRevalidationChallenge? revalidation=null;
  if(verified is not null)
  {
   if(request.ChallengeNonce!=(verified.RevalidationNonce??target.ChallengeNonce))throw Conflict();
  }
  else if(previous is not null&&request.Success)
  {
   revalidation=await db.Set<SecurityAckRevalidationChallenge>().AsNoTracking().SingleOrDefaultAsync(x=>x.Nonce==request.ChallengeNonce&&x.OperationId==request.OperationId&&x.EnvironmentId==identity.EnvironmentId&&x.NodeId==nodeId&&x.InstanceGuid==request.InstanceId,validationToken)??throw Conflict();
   // The lease and challenge use the database clock. AppliedAt comes from
   // the node, so permit only the skew signed into that specific lease.
   if(now<revalidation.CreatedAt||now<l.IssuedAt||l.IssuedAt<revalidation.CreatedAt||request.AppliedAt<revalidation.CreatedAt.AddSeconds(-l.MaximumClockSkewSeconds)||request.AppliedAt>=revalidation.ExpiresAt.AddSeconds(l.MaximumClockSkewSeconds))throw Conflict();
   if(now>=revalidation.ExpiresAt)
   {
    using var clockBudget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await tx.CommitAsync(clockBudget.Token);
    throw Conflict();
   }
  }
  else if(request.ChallengeNonce!=target.ChallengeNonce)throw Conflict();
  var beforeWrite=await SecurityDatabaseClock.ObserveAsync(db,validationToken);
  if(beforeWrite<now)throw Conflict();
  now=beforeWrite;
  if(verified is null&&l.FreshUntil<=now)
  {
   using var expiryBudget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
   await SecurityLeaseExpiryRecorder.RecordAsync(db,l.LeaseId,now,expiryBudget.Token);
   await tx.CommitAsync(expiryBudget.Token);
   throw Conflict();
  }
  if(verified is null&&await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db,now,validationToken))throw Conflict();
  if(verified is null&&revalidation is not null&&now>=revalidation.ExpiresAt)
  {
   using var clockBudget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
   await tx.CommitAsync(clockBudget.Token);
   throw Conflict();
  }
  if(verified is not null)
  {
   if(!request.Success||verified.SecurityGeneration!=request.SecurityGeneration||verified.Epoch!=request.Epoch||verified.CheckpointHash!=request.CheckpointHash||verified.ChallengeNonce!=target.ChallengeNonce||verified.AppliedAt!=appliedAt||verified.LeaseId!=l.LeaseId)throw Conflict();duplicate=true;
   if(l.FreshUntil<=now)
   {
    using var expiryBudget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await SecurityLeaseExpiryRecorder.RecordAsync(db,l.LeaseId,now,expiryBudget.Token);
    await tx.CommitAsync(expiryBudget.Token);
    return new(true);
   }
  }
  else if(request.Success)
  {
   if(now<l.IssuedAt||l.FreshUntil<=now||p.FreshUntil<=now)throw Conflict();
   if(previous is null)db.Add(new SecurityAckRecord{OperationId=request.OperationId,EnvironmentId=request.EnvironmentId,NodeId=nodeId,InstanceGuid=request.InstanceId,SecurityGeneration=p.SecurityGeneration,Epoch=p.Epoch,CheckpointHash=cp.Hash,ChallengeNonce=request.ChallengeNonce,AppliedAt=appliedAt,ReceivedAt=now,Durable=true,AdmissionEnforced=true});
   db.Add(new SecurityAckVerification{OperationId=request.OperationId,EnvironmentId=request.EnvironmentId,NodeId=nodeId,InstanceGuid=request.InstanceId,SecurityGeneration=p.SecurityGeneration,Epoch=p.Epoch,CheckpointHash=cp.Hash,ChallengeNonce=target.ChallengeNonce,RevalidationNonce=revalidation?.Nonce,LeaseId=l.LeaseId,AppliedAt=appliedAt,ReceivedAt=now});target.TargetState="Applied";
   // The independently verified current-proof endpoint owns the current receipt;
   // an operation ACK must not create or overwrite that independent evidence.
   db.Add(new GatewayNodeEvent{GatewayNodeId=nodeId,EventType=previous is null?"security_applied":"security_revalidated",Message="安全拒绝已耐久应用",Detail=System.Text.Json.JsonSerializer.Serialize(new{operationId=request.OperationId,instanceId=request.InstanceId,epoch=p.Epoch,checkpointHash=cp.Hash})});
  }
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
  }
  // A node has authority only over its own ACK, not over aggregate state of
  // other environments in the same operation. Exact retries remain idempotent.
  return new(duplicate);
 }
}

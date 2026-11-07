using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.CredentialSecurity;
public sealed class CurrentSecurityProofService(WebApiDbContext db,NodeIdentityService identities,SecurityLedgerReader reader,SecuritySigningSettings signing)
{
 public static ApiException Invalid()=>new(409,"security_current_proof_conflict","当前实例安全证明不完整、过期或与权威账本不符。");
 public static void RequireFullProof(SecurityCheckpointPayload proof,SecurityLedgerMaterial authority)
 {
  if(!proof.Full||proof.BaseEpoch is not null||proof.SecurityGeneration!=authority.SecurityGeneration||proof.Epoch<0||proof.Epoch>authority.Epoch)throw Invalid();var head=proof.Epoch==0?SecurityEventHash.Genesis:authority.Events.Single(x=>x.Epoch==proof.Epoch).EventHash;if(proof.HeadHash!=head)throw Invalid();var policy=SecurityPolicyRules.AtEpoch(authority,proof.Epoch);if(proof.RequireEnvironmentCredentials!=policy.RequireEnvironmentCredentials||proof.MinimumSnapshotSchema!=policy.MinimumSnapshotSchema)throw Invalid();var events=authority.Events.ToDictionary(e=>e.Epoch);var expected=authority.Revocations.Where(r=>r.Epoch<=proof.Epoch).Select(r=>new SecurityRevocationProof(r.CredentialId,r.OperationId,r.Epoch,r.RevokedAt,events[r.Epoch].EventHash)).OrderBy(r=>r.CredentialId).ToArray();if(!expected.SequenceEqual(proof.Revocations.OrderBy(r=>r.CredentialId)))throw Invalid();
 }
 public async Task RecordAsync(Guid nodeId,CurrentSecurityProof request,NodeIdentity identity,CancellationToken ct=default)
 {
  if(db.Database.CurrentTransaction is not null||db.ChangeTracker.HasChanges())throw new InvalidOperationException("Current security proof requires its own clean transaction.");
  db.ChangeTracker.Clear();
  if(nodeId!=identity.NodeId||request.InstanceId==Guid.Empty||!request.Durable||!request.AdmissionEnforced||request.AppliedAt.Offset!=TimeSpan.Zero)throw Invalid();VerifiedCheckpoint checkpoint;VerifiedNodeLease lease;try{var keys=signing.LoadTrustedKeys();checkpoint=SecurityCheckpointVerifier.VerifyCheckpoint(new(request.Checkpoint),keys,identity.EnvironmentId);lease=SecurityCheckpointVerifier.VerifyNodeLease(new(request.Lease),keys,identity.EnvironmentId,nodeId,request.InstanceId);}catch(Exception e)when(e is InvalidDataException or ArgumentException){throw Invalid();}var p=checkpoint.Payload;var l=lease.Payload;if(l.CheckpointHash!=checkpoint.Hash||l.SecurityGeneration!=p.SecurityGeneration||l.Epoch!=p.Epoch||l.FreshUntil>p.FreshUntil||!l.AllowAuthenticatedTraffic)throw Invalid();
  // Acquire the same governance and environment locks used by all authority
  // writers before reading. ReadCommitted takes a fresh statement snapshot
  // after any lock wait; starting RepeatableRead before waiting can preserve an
  // obsolete ledger snapshot. Non-cooperating database writers remain fenced
  // by the separate migration/deployment controls.
  await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)",ct);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={identity.EnvironmentId} FOR NO KEY UPDATE",ct);var node=await identities.RevalidateAsync(identity,request.InstanceId,ct);var authority=await reader.ReadCompleteLockedAsync(identity.EnvironmentId,ct);RequireFullProof(p,authority);if(!SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(node,authority.MinimumSnapshotSchema))throw Invalid();var issued=await db.Set<GatewaySecurityLease>().AsNoTracking().SingleOrDefaultAsync(x=>x.NodeId==nodeId&&x.InstanceId==request.InstanceId,ct);var now=await SecurityDatabaseClock.ObserveAsync(db,ct);
  // Finish bounded signed-lease validation even after client disconnect: its
  // clock window can expire while the database checks are in progress.
  using var validationBudget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
  var validationToken=validationBudget.Token;
  if(await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db,now,validationToken)||await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().AnyAsync(x=>x.LeaseId==l.LeaseId,validationToken))throw Invalid();
  if(issued is null||issued.EnvironmentId!=identity.EnvironmentId||issued.SecurityGeneration!=l.SecurityGeneration||issued.Epoch<l.Epoch||issued.LastIssuedLeaseUntil<l.FreshUntil||issued.IssuedAt<l.IssuedAt||now<issued.IssuedAt||now<p.IssuedAt||!await SecurityLeaseIssuanceEvidence.ContainsAsync(db,l,issued.OrganizationId,issued.ProjectId,validationToken))throw Invalid();
  if(l.FreshUntil<=now)
  {
   await SecurityLeaseExpiryRecorder.RecordAsync(db,l.LeaseId,now,validationToken);
   await tx.CommitAsync(validationToken);
   throw Invalid();
  }
  if(p.FreshUntil<=now||request.AppliedAt<l.IssuedAt.AddSeconds(-l.MaximumClockSkewSeconds)||request.AppliedAt<p.IssuedAt.AddSeconds(-l.MaximumClockSkewSeconds)||request.AppliedAt>=l.FreshUntil||request.AppliedAt>=p.FreshUntil||request.AppliedAt>now.AddSeconds(l.MaximumClockSkewSeconds)||p.Epoch!=authority.Epoch||p.HeadHash!=authority.HeadHash)throw Invalid();var prior=SecurityApplicationReceipt.Read(node);if(prior is not null&&(prior.SecurityGeneration!=p.SecurityGeneration||prior.Epoch>p.Epoch||prior.Epoch==p.Epoch&&prior.FreshUntil>l.FreshUntil))throw Invalid();var accepted=await db.Set<GatewaySecurityCurrentReceipt>().SingleOrDefaultAsync(x=>x.NodeId==nodeId&&x.InstanceId==request.InstanceId,validationToken);
  var beforeWrite=await SecurityDatabaseClock.ObserveAsync(db,validationToken);
  if(beforeWrite<now)throw Invalid();
  now=beforeWrite;
  if(l.FreshUntil<=now)
  {
   using var expiryBudget=new CancellationTokenSource(TimeSpan.FromSeconds(5));
   await SecurityLeaseExpiryRecorder.RecordAsync(db,l.LeaseId,now,expiryBudget.Token);
   await tx.CommitAsync(expiryBudget.Token);
   throw Invalid();
  }
  if(await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db,now,validationToken))throw Invalid();
  if(accepted is null){accepted=new(){NodeId=nodeId,InstanceId=request.InstanceId,EnvironmentId=identity.EnvironmentId,ProjectId=issued.ProjectId,OrganizationId=issued.OrganizationId};db.Add(accepted);}
  accepted.SecurityGeneration=p.SecurityGeneration;accepted.Epoch=p.Epoch;accepted.CheckpointHash=checkpoint.Hash;accepted.HeadHash=p.HeadHash;accepted.LeaseId=l.LeaseId;accepted.FreshUntil=l.FreshUntil;accepted.ReceivedAt=now;
  node.Metadata=SecurityApplicationReceipt.Merge(node.Metadata,new(node.Id,node.EnvironmentId,request.InstanceId,p.SecurityGeneration,p.Epoch,checkpoint.Hash,request.AppliedAt,p.HeadHash,l.FreshUntil,now,l.LeaseId));await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
 }
}

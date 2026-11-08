using System.Data;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.CredentialSecurity;
// Authority-only material: no signature or freshness. Gateways must never accept this as an admission checkpoint.
public sealed record SecurityLedgerMaterial(Guid EnvironmentId,Guid OrganizationId,Guid ProjectId,Guid SecurityGeneration,long Epoch,string HeadHash,bool RequireEnvironmentCredentials,string MinimumSnapshotSchema,IReadOnlyList<SecurityEvent> Events,IReadOnlyList<CredentialRevocation> Revocations,bool? InitialRequireEnvironmentCredentials=null);
public sealed class SecurityLedgerReader(WebApiDbContext db)
{
 public async Task<SecurityLedgerMaterial> ReadCompleteAsync(Guid environmentId,CancellationToken ct=default)
 {
  if(db.Database.CurrentTransaction is not null)throw new InvalidOperationException("Complete security read must own a repeatable-read transaction.");
  await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
  var material=await ReadCoreAsync(environmentId,ct);await tx.CommitAsync(ct);return material;
 }
 public async Task<SecurityLedgerMaterial> ReadCompleteLockedAsync(Guid environmentId,CancellationToken ct=default)
 {
  if(db.Database.CurrentTransaction is null||!await db.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND pid=pg_backend_pid() AND classid=0 AND objid=8901202 AND objsubid=1 AND granted) AS \"Value\"").SingleAsync(ct))throw new InvalidOperationException("Locked security read requires a governance transaction.");
  await db.Set<EnvironmentRecord>().FromSqlInterpolated($"SELECT * FROM environments WHERE id={environmentId} FOR NO KEY UPDATE").AsNoTracking().SingleAsync(ct);return await ReadCoreAsync(environmentId,ct);
 }
 public async Task<SecurityLedgerMaterial> ReadCompleteInSnapshotAsync(Guid environmentId,CancellationToken ct=default)
 {if(db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel!=IsolationLevel.RepeatableRead)throw new InvalidOperationException("Security snapshot read requires repeatable-read isolation.");return await ReadCoreAsync(environmentId,ct);}
 private async Task<SecurityLedgerMaterial> ReadCoreAsync(Guid environmentId,CancellationToken ct)
 {
  // Initialization is necessary, not by itself sufficient for a signed lease;
  // the signer must independently revalidate its evidence and node identity.
  var initialization=await db.Set<EnvironmentSecurityInitialization>().AsNoTracking().SingleOrDefaultAsync(x=>x.EnvironmentId==environmentId,ct);
  if(initialization is null||initialization.State is not ("FreshNonStrict" or "VerifiedLegacyNetwork" or "Governed"))throw new InvalidOperationException("Security environment initialization is not verified.");
  var state=await db.Set<EnvironmentSecurityState>().AsNoTracking().SingleAsync(x=>x.EnvironmentId==environmentId,ct);
  if(initialization.ProjectId!=state.ProjectId||initialization.OrganizationId!=state.OrganizationId)throw new InvalidOperationException("Security initialization scope mismatch.");
  // Read every event and tombstone, including unknown generations. Filtering them out could hide corruption.
  var events=await db.Set<SecurityEvent>().AsNoTracking().Where(x=>x.EnvironmentId==environmentId).OrderBy(x=>x.Epoch).ToArrayAsync(ct);
  var revoked=await db.Set<CredentialRevocation>().AsNoTracking().Where(x=>x.EnvironmentId==environmentId).OrderBy(x=>x.CredentialId).ToArrayAsync(ct);
  var material=new SecurityLedgerMaterial(state.EnvironmentId,state.OrganizationId,state.ProjectId,state.SecurityGeneration,state.Epoch,state.HeadHash,state.RequireEnvironmentCredentials,state.MinimumSnapshotSchema,events,revoked,state.InitialRequireEnvironmentCredentials);Validate(material);
  var operations=await db.Set<SecurityOperation>().AsNoTracking().Where(x=>events.Select(e=>e.OperationId).Contains(x.Id)).ToDictionaryAsync(x=>x.Id,ct);
  foreach(var e in events)if(!operations.TryGetValue(e.OperationId,out var op)||op.Kind!=(e.EventType=="CredentialRevoked"?"CredentialRevoke":"EnvironmentPolicyCutover")||op.CredentialId!=e.CredentialId||e.EventType=="EnvironmentPolicyChanged"&&op.ApplicationId is not null||op.OrganizationId!=e.OrganizationId||op.CommittedAt!=e.OccurredAt)throw new InvalidOperationException("Security event operation mismatch.");
  var credentials=await (from c in db.Set<ApplicationCredential>().AsNoTracking() join a in db.Set<ApplicationRecord>().AsNoTracking() on c.ApplicationId equals a.Id where revoked.Select(r=>r.CredentialId).Contains(c.Id) select new {c.Id,c.ApplicationId,c.OrganizationId,c.Status,c.RevokedAt,c.BindingMode,c.EnvironmentId,a.ProjectId}).ToDictionaryAsync(x=>x.Id,ct);
  foreach(var r in revoked)if(!credentials.TryGetValue(r.CredentialId,out var c)||c.Status!="Revoked"||c.RevokedAt==null||c.ApplicationId!=r.ApplicationId||c.OrganizationId!=r.OrganizationId||c.ProjectId is Guid project&&project!=r.ProjectId||c.BindingMode=="EnvironmentBound"&&c.EnvironmentId!=r.EnvironmentId)throw new InvalidOperationException("Security tombstone credential mismatch.");
  return material;
 }
 public static void Validate(SecurityLedgerMaterial m)
 {
  if(m.EnvironmentId==Guid.Empty||m.OrganizationId==Guid.Empty||m.ProjectId==Guid.Empty||m.SecurityGeneration==Guid.Empty||m.Epoch<0||m.Events.Count!=m.Epoch||m.MinimumSnapshotSchema is not ("2.0" or "2.1" or "2.2" or "2.3" or "2.4"))throw new InvalidOperationException("Incomplete security ledger.");
  var previous=SecurityEventHash.Genesis;long epoch=0;var facts=new Dictionary<Guid,SecurityEvent>();EnvironmentSecurityPolicy? policy=null;
  foreach(var e in m.Events)
  {
   if(e.EnvironmentId!=m.EnvironmentId||e.OrganizationId!=m.OrganizationId||e.ProjectId!=m.ProjectId||e.SecurityGeneration!=m.SecurityGeneration||e.Epoch!=++epoch||e.PreviousHash!=previous||e.EventHash!=SecurityEventHash.Compute(e))throw new InvalidOperationException("Invalid security hash chain.");
   if(e.EventType=="CredentialRevoked"){if(e.CredentialId is not Guid id||id==Guid.Empty||e.PolicyJson!=null||!facts.TryAdd(id,e))throw new InvalidOperationException("Invalid revocation event.");}
   else if(e.EventType=="EnvironmentPolicyChanged") {if(e.CredentialId is not null)throw new InvalidOperationException("Invalid policy event.");var next=SecurityPolicyRules.Parse(e.PolicyJson);if(!SecurityPolicyRules.Strengthens(next,policy??new(m.InitialRequireEnvironmentCredentials??false,"2.0")))throw new InvalidOperationException("Security policy regression.");policy=next;}
   else throw new InvalidOperationException("Unknown security event.");previous=e.EventHash;
  }
  if(m.HeadHash!=previous||m.Revocations.Count!=facts.Count)throw new InvalidOperationException("Incomplete security tombstones.");
  if(policy is not null&&(m.RequireEnvironmentCredentials!=policy.RequireEnvironmentCredentials||m.MinimumSnapshotSchema!=policy.MinimumSnapshotSchema)||policy is null&&(m.MinimumSnapshotSchema!="2.0"||m.InitialRequireEnvironmentCredentials is bool initial&&m.RequireEnvironmentCredentials!=initial))throw new InvalidOperationException("Security policy authority mismatch.");
  var seen=new HashSet<Guid>();foreach(var r in m.Revocations)
   if(!seen.Add(r.CredentialId)||r.EnvironmentId!=m.EnvironmentId||r.OrganizationId!=m.OrganizationId||r.ProjectId!=m.ProjectId||r.SecurityGeneration!=m.SecurityGeneration||!facts.TryGetValue(r.CredentialId,out var e)||r.Epoch!=e.Epoch||r.OperationId!=e.OperationId||r.RevokedAt!=e.OccurredAt)throw new InvalidOperationException("Invalid security tombstone.");
 }
}

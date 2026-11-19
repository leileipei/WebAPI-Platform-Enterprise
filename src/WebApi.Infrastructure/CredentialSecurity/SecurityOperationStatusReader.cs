using System.Data;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.CredentialSecurity;
public sealed class SecurityOperationStatusReader(WebApiDbContext db,PostgresRevocationReceiptEvidence? evidence=null)
{
 private sealed record RuntimeStatus(string Status,IReadOnlyList<RevocationTargetDto> Targets,IReadOnlyList<RevocationCoverageGapDto> CoverageGaps,
  DateTimeOffset ObservedAt,DateTimeOffset? EarliestConfirmedLeaseExpiry);
 private sealed record LockedStatus(RuntimeStatus Runtime,RevocationOperationDto? Receipt,PostgresRevocationReceiptEvidence.LocalEvidence? Evidence);
 private static readonly TimeSpan HeartbeatGrace=TimeSpan.FromSeconds(new PublishSettings().HeartbeatGraceSeconds);

 public async Task<(string Status,IReadOnlyList<RevocationTargetDto> Targets,IReadOnlyList<RevocationCoverageGapDto> CoverageGaps,DateTimeOffset ObservedAt,DateTimeOffset? EarliestConfirmedLeaseExpiry)> ReadAsync(Guid operation,CancellationToken ct=default)
 {
  var (runtime,_,_)=await ReadLockedAsync(operation,null,ct);
  return(runtime.Status,runtime.Targets,runtime.CoverageGaps,runtime.ObservedAt,runtime.EarliestConfirmedLeaseExpiry);
 }

 public async Task<RevocationOperationDto> ReadRevocationAsync(Guid operation,ActorContext actor,
  Func<WebApiDbContext,CancellationToken,Task> requireSession,CancellationToken ct=default)
 {
  async Task<RevocationOperationDto> AuthorizeAsync(CancellationToken token)
  {
   await requireSession(db,token);
   var row=await db.Set<SecurityOperation>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==operation && x.Kind=="CredentialRevoke",token);
   if(row?.CredentialId is not Guid credential) throw RevocationMissing();
   var facts=await db.Set<SecurityEvent>().AsNoTracking().Where(x=>x.OperationId==operation&&x.EventType!=CredentialRevocationEvents.Inherited)
    .OrderBy(x=>x.EnvironmentId).ToArrayAsync(token);
   if(facts.Length==0||facts.Any(x=>x.OrganizationId!=row.OrganizationId||x.CredentialId!=credential||
    !CredentialRevocationEvents.IsRevocation(x.EventType)||!CredentialRevocationEvents.ValidMaterial(x)))
    throw RevocationMissing();
   var authorization=new AuthorizationService(db);
   foreach(var fact in facts)
   {
    var target=new ResourceRef("credential",credential,new(fact.OrganizationId,fact.ProjectId,fact.EnvironmentId));
    if(fact.EventType==CredentialRevocationEvents.Archived)await authorization.RequireArchivedCredentialRevocationAsync(actor,target,token);
    else await authorization.RequireAsync(actor,"credential.revoke",target,token);
   }
   return new(operation,credential,row.CommittedAt,row.Revision,"Unconfirmed",$"/api/v1/credential-revocations/{operation}",
    facts.Select(fact=>new RevocationEnvironmentDto(fact.EnvironmentId,fact.SecurityGeneration,fact.Epoch,fact.EventHash,
     CredentialRevocationEvents.CoverageState(fact))).ToArray(),[]);
  }

  var first=await ReadLockedAsync(operation,AuthorizeAsync,ct,readEvidence:evidence is not null);
  if(evidence is null||first.Evidence is not { } local)
   return ProjectReceipt(first.Receipt!,first.Runtime,null,null);

  // Witness I/O is bracketed by local, authorized samples. The confirmed
  // journal attests the first sample. The last transaction tests that its
  // facts are still applicable and that the session can still read them.
  var independent=await evidence.ObserveAsync(local,ct);
  var latest=await ReadLockedAsync(operation,AuthorizeAsync,ct,readEvidence:independent is not null,witnesses:local.Witnesses);
  var receipt=latest.Receipt!;
  var original=first.Receipt!;
  var verification=receipt.CredentialId==original.CredentialId&&receipt.CommittedAt==original.CommittedAt&&
   receipt.Revision==original.Revision&&receipt.Environments.SequenceEqual(original.Environments)
   ?PostgresRevocationReceiptEvidence.Reconcile(local,independent,latest.Evidence):null;
  return ProjectReceipt(receipt,latest.Runtime,verification,first.Runtime);
 }

 private static ApiException RevocationMissing()=>new(404,"credential_revocation_not_found","找不到撤销操作。");

 private async Task<LockedStatus> ReadLockedAsync(Guid operation,
  Func<CancellationToken,Task<RevocationOperationDto>>? authorize,CancellationToken ct,bool readEvidence=false,
  PostgresRevocationReceiptEvidence.Sources? witnesses=null)
 {
  // Lock before reading to avoid fixing a snapshot before an authority change.
  if(db.Database.CurrentTransaction is not null)
   throw new InvalidOperationException("Security operation status must own a fresh transaction.");
  await using var snapshot=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
  await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)",ct);

  // Authorize only the original operation scope, including sealed environments.
  // Inherited environments are neither exposed nor locked by the public poll.
  if(authorize is not null)await db.Database.ExecuteSqlRawAsync("SET LOCAL search_path = pg_catalog, public, pg_temp",ct);
  var receipt=authorize is null?null:await authorize(ct);
  var environments=receipt?.Environments.Select(x=>x.EnvironmentId).ToArray()??
   await db.Set<SecurityEvent>().AsNoTracking().Where(x=>x.OperationId==operation).Select(x=>x.EnvironmentId)
   .Concat(db.Set<SecurityOperationTarget>().AsNoTracking().Where(x=>x.OperationId==operation).Select(x=>x.EnvironmentId))
   .Distinct().OrderBy(x=>x).ToArrayAsync(ct);
  foreach(var environment in environments)
   await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM public.environments WHERE id={environment} FOR UPDATE",ct);
  var visible=receipt?.Environments.Where(x=>x.CoverageState!="SealedNoIngressAtCommit").Select(x=>x.EnvironmentId).ToArray();
  var local=readEvidence&&receipt is not null&&evidence is not null?await evidence.ReadLocalAsync(db,receipt,witnesses,ct):null;
  using var completionBudget=new CancellationTokenSource();
  var recordingExpiry=false;
  CancellationToken ExpiryToken()
  {
   if(!recordingExpiry){recordingExpiry=true;completionBudget.CancelAfter(TimeSpan.FromSeconds(5));}
   return completionBudget.Token;
  }
  var result=await ReadCoreAsync(operation,visible,ct,ExpiryToken);
  await snapshot.CommitAsync(recordingExpiry?completionBudget.Token:ct);
  return new(result,receipt,local);
 }

 private static RevocationOperationDto ProjectReceipt(RevocationOperationDto receipt,RuntimeStatus runtime,
  PostgresRevocationReceiptEvidence.Observation? verification,RuntimeStatus? first)
 {
  var gaps=runtime.CoverageGaps.ToList();
  var covered=verification?.Environments.Where(pair=>pair.Value.ValidUntil>runtime.ObservedAt)
   .ToDictionary(pair=>pair.Key,pair=>pair.Value)??[];
  if(verification is null)gaps.Add(new(null,null,"independent_coverage_unverified"));
  else
   foreach(var env in receipt.Environments.Where(x=>!covered.ContainsKey(x.EnvironmentId)))
    gaps.Add(new(env.EnvironmentId,null,"independent_coverage_unverified"));
  var previouslyApplied=first?.Targets.Where(target=>target.State=="Applied")
   .Select(target=>(target.EnvironmentId,target.NodeId,target.InstanceGuid)).ToHashSet()??[];
  var targets=runtime.Targets.Select(target=>covered.TryGetValue(target.EnvironmentId,out var valid)&&
   valid.CurrentInstances.Contains((target.NodeId,target.InstanceGuid))&&
   target.LastHeartbeatAt is DateTimeOffset heartbeat&&heartbeat<=runtime.ObservedAt&&runtime.ObservedAt-heartbeat<=HeartbeatGrace&&
   (target.State!="Applied"||previouslyApplied.Contains((target.EnvironmentId,target.NodeId,target.InstanceGuid)))
   ?target
   :target with {State="Pending",Reason="independent_coverage_unverified",ErrorCode=null}).ToArray();
  var applied=targets.Count(target=>target.State=="Applied");
  var confirmed=gaps.Count==0&&applied==targets.Length;
  return receipt with {Status=confirmed?"FullyApplied":applied>0?"Partial":"Unconfirmed",Targets=targets,CoverageGaps=gaps};
 }

 private async Task<RuntimeStatus> ReadCoreAsync(Guid operation,Guid[]? visible,CancellationToken ct,Func<CancellationToken> expiryToken)
 {
  var targets=await db.Set<SecurityOperationTarget>().AsNoTracking().Where(x=>x.OperationId==operation&&(visible==null||visible.Contains(x.EnvironmentId))).OrderBy(x=>x.EnvironmentId).ThenBy(x=>x.NodeId).ThenBy(x=>x.CreatedAt).ToArrayAsync(ct);
  var eventEnvironmentIds=await db.Set<SecurityEvent>().AsNoTracking().Where(x=>x.OperationId==operation&&(visible==null||visible.Contains(x.EnvironmentId))).Select(x=>x.EnvironmentId).Distinct().ToArrayAsync(ct);
  var acks=await db.Set<SecurityAckVerification>().AsNoTracking().Where(x=>x.OperationId==operation&&(visible==null||visible.Contains(x.EnvironmentId))).ToArrayAsync(ct);var ids=targets.Select(x=>x.NodeId).Distinct().ToArray();
  var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(x=>ids.Contains(x.Id)||x.Enabled&&eventEnvironmentIds.Contains(x.EnvironmentId)).ToDictionaryAsync(x=>x.Id,ct);
  var environmentIds=targets.Select(x=>x.EnvironmentId).Concat(eventEnvironmentIds).Distinct().ToArray();
  var securityStates=await db.Set<EnvironmentSecurityState>().AsNoTracking().Where(x=>environmentIds.Contains(x.EnvironmentId)).ToDictionaryAsync(x=>x.EnvironmentId,ct);
  var receipts=nodes.Values.Select(SecurityApplicationReceipt.Read).Where(x=>x is not null).ToArray();
  var instances=receipts.Select(x=>x!.InstanceId).Distinct().ToArray();
  var acceptedReceipts=await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().Where(x=>ids.Contains(x.NodeId)&&instances.Contains(x.InstanceId)).ToArrayAsync(ct);
  var receiptLeaseIds=acceptedReceipts.Select(x=>x.LeaseId).Distinct().ToArray();
  var receiptIssuances=await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().Where(x=>receiptLeaseIds.Contains(x.LeaseId)).ToDictionaryAsync(x=>x.LeaseId,ct);
  var observedExpired=(await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().Where(x=>receiptLeaseIds.Contains(x.LeaseId)).Select(x=>x.LeaseId).ToArrayAsync(ct)).ToHashSet();
  var latestLeases=(await db.Set<GatewaySecurityLease>().AsNoTracking().Where(x=>ids.Contains(x.NodeId)&&instances.Contains(x.InstanceId)).ToArrayAsync(ct))
   .ToDictionary(x=>(x.NodeId,x.InstanceId));
  var attempts=await db.Set<SecurityAckAttempt>().AsNoTracking().Where(x=>x.OperationId==operation&&(visible==null||visible.Contains(x.EnvironmentId))).OrderByDescending(x=>x.ReceivedAt).ToArrayAsync(ct);
  var latestFailures=await db.Set<SecurityAckFailureLatest>().AsNoTracking().Where(x=>x.OperationId==operation&&(visible==null||visible.Contains(x.EnvironmentId))).ToArrayAsync(ct);
  var acksByTarget=acks.ToDictionary(x=>(x.EnvironmentId,x.NodeId,x.InstanceGuid));
  var receiptsByInstance=acceptedReceipts.ToDictionary(x=>(x.NodeId,x.InstanceId));
  var attemptsByTarget=attempts.ToLookup(x=>(x.EnvironmentId,x.NodeId,x.InstanceGuid));
  var failuresByTarget=latestFailures.ToDictionary(x=>(x.EnvironmentId,x.NodeId,x.InstanceGuid));
  // Sample after the potentially slow reads and lock waits, so a lease that
  // expires during those reads is never described as currently covered.
  var now=await SecurityDatabaseClock.ObserveAsync(db,ct);
  var hasNewExpiry=acceptedReceipts.Any(r=>receiptIssuances.TryGetValue(r.LeaseId,out var issued)&&r.FreshUntil==issued.FreshUntil&&now>=issued.FreshUntil&&!observedExpired.Contains(r.LeaseId));
  var completionToken=hasNewExpiry?expiryToken():ct;
  var clockRegressed=await SecurityLeaseExpiryRecorder.HasLaterObservationAsync(db,now,completionToken);
  // Persist observations before returning so a later DB-clock rollback, or a
  // different control-plane instance, cannot revive a lease already seen to
  // have expired. This does not prove clock correctness before observation or
  // after restoring an older database; those remain deployment gates.
  foreach(var r in acceptedReceipts)
   if(receiptIssuances.TryGetValue(r.LeaseId,out var issued)&&r.FreshUntil==issued.FreshUntil&&now>=issued.FreshUntil&&observedExpired.Add(r.LeaseId))
    await SecurityLeaseExpiryRecorder.RecordAsync(db,r.LeaseId,now,completionToken);
  bool Confirmed(SecurityOperationTarget target,Guid instance)
  {
   if(clockRegressed||!target.ProtocolSupportedAtFreeze||!nodes.TryGetValue(target.NodeId,out var node)||node.EnvironmentId!=target.EnvironmentId||!securityStates.TryGetValue(target.EnvironmentId,out var securityState)||SecurityApplicationReceipt.Read(node) is not CurrentSecurityReceipt receipt||receipt.InstanceId!=instance||receipt.SecurityGeneration!=securityState.SecurityGeneration||receipt.Epoch!=securityState.Epoch||receipt.HeadHash!=securityState.HeadHash||!SnapshotSchemaCapabilities.SupportsGovernanceAtFloor(node,securityState.MinimumSnapshotSchema))return false;
   acksByTarget.TryGetValue((target.EnvironmentId,target.NodeId,instance),out var operationAck);
   // A current lease or proof can NEVER replace this exact immutable operation/target ACK.
   if(operationAck is null||operationAck.ChallengeNonce!=target.ChallengeNonce||operationAck.SecurityGeneration!=receipt.SecurityGeneration||operationAck.Epoch>receipt.Epoch||operationAck.ReceivedAt>now)return false;
   // A durable operation ACK cannot prove its own authority. The separately
   // persisted current receipt must be accepted only by the signature/lease
   // validation path before it can support this status.
   return receiptsByInstance.TryGetValue((target.NodeId,instance),out var r)&&!observedExpired.Contains(r.LeaseId)&&SecurityApplicationReceipt.MatchesAccepted(receipt,r)
    &&receiptIssuances.TryGetValue(r.LeaseId,out var issuance)&&issuance.NodeId==r.NodeId&&issuance.InstanceId==r.InstanceId
    // A newer issuance is not proof of application. Conversely, an older
    // accepted receipt cannot establish current coverage after a different
    // lease was issued, even if its own expiry has not yet passed.
    &&latestLeases.TryGetValue((r.NodeId,r.InstanceId),out var latest)&&latest.LeaseId==r.LeaseId
    &&latest.EnvironmentId==r.EnvironmentId&&latest.ProjectId==r.ProjectId&&latest.OrganizationId==r.OrganizationId
    &&latest.SecurityGeneration==r.SecurityGeneration&&latest.Epoch==r.Epoch&&latest.CheckpointHash==r.CheckpointHash
    &&latest.IssuedAt==issuance.IssuedAt&&latest.FreshUntil==issuance.FreshUntil&&latest.LastIssuedLeaseUntil>=latest.FreshUntil
    &&issuance.EnvironmentId==r.EnvironmentId&&issuance.ProjectId==r.ProjectId&&issuance.OrganizationId==r.OrganizationId
    &&r.ProjectId==securityState.ProjectId&&r.OrganizationId==securityState.OrganizationId
    &&issuance.SecurityGeneration==r.SecurityGeneration&&issuance.Epoch==r.Epoch&&issuance.CheckpointHash==r.CheckpointHash
    // Operation ACKs are immutable historical facts, but FullyApplied also
    // describes current coverage. A receipt cannot establish that coverage
    // once its independently issued lease has reached its expiry boundary.
    &&issuance.FreshUntil==r.FreshUntil&&now<issuance.FreshUntil&&issuance.IssuedAt<=now&&r.ReceivedAt<=now&&r.ReceivedAt>=issuance.IssuedAt&&r.ReceivedAt<issuance.FreshUntil;

  }
  var result=new List<RevocationTargetDto>();
  foreach(var target in targets)
  {
   var state=target.InstanceGuid==Guid.Empty?"AwaitingRegistration":"Pending";
   if(nodes.TryGetValue(target.NodeId,out var node)&&Guid.TryParse(node.InstanceId,out var current))
   {
    if(current==target.InstanceGuid&&Confirmed(target,current))state="Applied";
    // A matching recorded horizon does not establish completeness after a
    // restore or unknown legacy history. Replacement stays pending until an
    // independently verified history/isolation proof is implemented.
   }
   nodes.TryGetValue(target.NodeId,out var details);
   Guid? currentInstance=Guid.TryParse(details?.InstanceId,out var parsed)?parsed:null;
   var targetAttempts=attemptsByTarget[(target.EnvironmentId,target.NodeId,target.InstanceGuid)].ToArray();
   var failures=targetAttempts.Select(x=>x.ErrorCode).Distinct().ToArray();
   failuresByTarget.TryGetValue((target.EnvironmentId,target.NodeId,target.InstanceGuid),out var latestFailure);
   var timeInconsistent=targetAttempts.Any(x=>x.ReceivedAt>now||latestFailure is not null&&x.ReceivedAt>latestFailure.LastAcceptedAt)||latestFailure is not null&&latestFailure.LastAcceptedAt>now;
   var sequenceInconsistent=targetAttempts.Any(x=>x.ObservationSequence>(latestFailure?.LatestSequence??0)||latestFailure is not null&&x.ObservationSequence==latestFailure.LatestSequence&&(x.ErrorCode!=latestFailure.ErrorCode||x.ReportedNonce!=latestFailure.ReportedNonce));
   // Pre-upgrade first-occurrence facts cannot establish which of several
   // different errors was most recent; do not manufacture an ordering.
   var error=state is "Applied" or "SafeSuperseded"||timeInconsistent||sequenceInconsistent?null:latestFailure?.ErrorCode??(failures.Length==1?failures[0]:null);
   var reason=state is "Applied" or "SafeSuperseded"?null:!target.ProtocolSupportedAtFreeze?"protocol_unsupported":target.InstanceGuid==Guid.Empty?"awaiting_registration":currentInstance!=target.InstanceGuid?"awaiting_successor_and_expiry":timeInconsistent?"failure_time_inconsistent":sequenceInconsistent?"failure_sequence_inconsistent":error is not null?"application_failed":failures.Length>1?"multiple_security_failures":details?.LastHeartbeatAt is null||details.LastHeartbeatAt<now.AddSeconds(-120)?"offline":"awaiting_ack";
   result.Add(new(target.EnvironmentId,target.NodeId,target.InstanceGuid,state,details?.NodeName,currentInstance,reason,target.LastLeaseUntil,details?.LastHeartbeatAt,error));
  }
  var gaps=new List<RevocationCoverageGapDto>();
  if(clockRegressed)gaps.Add(new(null,null,"database_clock_regressed"));
  if(eventEnvironmentIds.Length==0&&visible is not { Length: 0 })gaps.Add(new(null,null,"operation_has_no_environment_events"));
  var eventEnvironments=eventEnvironmentIds.ToHashSet();
  foreach(var environment in targets.Select(t=>t.EnvironmentId).Distinct().Where(id=>!eventEnvironments.Contains(id)))
   gaps.Add(new(environment,null,"target_without_environment_event"));
  var targetKeys=targets.Select(t=>(t.EnvironmentId,t.NodeId,t.InstanceGuid)).ToHashSet();
  var targetsByEnvironment=targets.ToLookup(t=>t.EnvironmentId);
  var enabledByEnvironment=nodes.Values.Where(n=>n.Enabled).ToLookup(n=>n.EnvironmentId);
  foreach(var environment in eventEnvironmentIds)
  {
   if(!targetsByEnvironment[environment].Any())gaps.Add(new(environment,null,"environment_has_no_targets"));
   var enabled=enabledByEnvironment[environment].ToArray();
   if(enabled.Length==0)gaps.Add(new(environment,null,"no_enabled_gateway_nodes"));
   foreach(var node in enabled)
   {
    if(!Guid.TryParse(node.InstanceId,out var current)||current==Guid.Empty)gaps.Add(new(environment,node.Id,"node_instance_unknown"));
    else if(!targetKeys.Contains((environment,node.Id,current)))gaps.Add(new(environment,node.Id,"current_node_target_missing"));
   }
  }
  var applied=result.Count(x=>x.State is "Applied" or "SafeSuperseded");
  // These timestamps describe only this diagnostic observation. The expiry
  // bound does not guarantee unchanged nodes, ledger or traffic availability.
  var earliestExpiry=result.Where(x=>x.State=="Applied").Select(x=>(DateTimeOffset?)receiptsByInstance[(x.NodeId,x.InstanceGuid)].FreshUntil).Min();
  return new(result.Count>0&&gaps.Count==0&&applied==result.Count?"FullyApplied":applied>0?"Partial":"Unconfirmed",result,gaps,now,earliestExpiry);
 }
}

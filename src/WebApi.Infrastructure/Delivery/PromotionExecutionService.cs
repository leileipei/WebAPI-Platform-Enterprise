using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Delivery;
public sealed class PromotionExecutionService(WebApiDbContext db,DeliveryLockCoordinator locks,DeliveryReleaseGuard guard,PromotionReadService reads,ReleaseArtifactService artifacts)
{
 public async Task LockReleaseAsync(ReleaseRecord release,CancellationToken ct)
 {await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);var p=await reads.ResolvePromotionAsync(release.Id,ct);if(p is not null)await locks.LockAsync(p.SourceEnvironmentId,p.TargetEnvironmentId,ct);else await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={release.EnvironmentId} FOR UPDATE",ct);}
 public async Task RequireExecutionAsync(ReleaseRecord release,ActorContext actor,CancellationToken ct)
 {
  await LockReleaseAsync(release,ct);if(release.ReleaseType=="publish"){await guard.RequireReadyAsync(release,actor,ct);return;}var p=await reads.ResolvePromotionAsync(release.Id,ct);if(p is null)return;await artifacts.GetAsync(p.ArtifactId,actor,ct);
  if(release.ArtifactId!=p.ArtifactId||release.SourceReleaseId!=p.SourceReleaseId)throw new ApiException(409,"invalid_delivery_recovery_chain","恢复发布缺少原晋级制品追溯。");
 }
 public async Task RequireRecoveryVisibilityAsync(ReleaseRecord origin,ActorContext actor,CancellationToken ct)
 {await LockReleaseAsync(origin,ct);var p=await reads.ResolvePromotionAsync(origin.Id,ct);if(p is not null)await artifacts.GetAsync(p.ArtifactId,actor,ct);}
 public async Task RecordDeploymentStateAsync(ReleaseRecord release,CancellationToken ct)
 {
  var p=await reads.ResolvePromotionAsync(release.Id,ct);if(p is null)return;var rollback=release.ReleaseType=="rollback";var recovery=release;while(recovery.ReleaseType=="retry"&&recovery.RecoveryOf is Guid origin){recovery=await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==origin,ct);if(recovery.ReleaseType=="rollback")rollback=true;}var state=release.Status switch{"Building" or "Publishing"=>"Deploying","Failed"=>"DeploymentFailed","Succeeded"=>rollback?"RolledBack":"Verifying",_=>null};if(state is null||p.Status==state)return;
  if(p.Status=="Completed"&&state!="RolledBack"){if(rollback)db.Add(new ReleasePromotionEvent{OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,EnvironmentId=p.TargetEnvironmentId,PromotionId=p.Id,Phase="Deployment",FromStatus="Completed",ToStatus="Completed",ReasonCode="historical_rollback_"+release.Status.ToLowerInvariant(),ActorId=release.PublishRequestedBy,ReleaseId=release.Id});return;}
  var before=p.Status;p.Status=state;p.Revision++;if(state=="RolledBack")p.CompletedAt=DateTimeOffset.UtcNow;else p.CompletedAt=null;
  db.Add(new ReleasePromotionEvent{OrganizationId=p.OrganizationId,ProjectId=p.ProjectId,EnvironmentId=p.TargetEnvironmentId,PromotionId=p.Id,Phase="Deployment",FromStatus=before,ToStatus=state,ReasonCode=release.FailureCode??("release_"+release.Status.ToLowerInvariant()),ActorId=release.PublishRequestedBy,ReleaseId=release.Id});
 }
}

using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
public sealed class PromotionReadService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,ReleaseArtifactService artifacts,PromotionMappingService mappings)
{
 public async Task<ReleasePromotion?> ResolvePromotionAsync(Guid releaseId,CancellationToken ct)
 {
  var visited=new HashSet<Guid>();Guid? environment=null;var id=releaseId;
  for(var depth=0;depth<100;depth++){
   if(!visited.Add(id))throw InvalidChain();var release=await db.Set<ReleaseRecord>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct)??throw InvalidChain();environment??=release.EnvironmentId;if(release.EnvironmentId!=environment)throw InvalidChain();
   if(release.PromotionId is Guid promotionId){if(release.RecoveryOf is not null||release.RollbackOf is not null||release.ReleaseType!="publish")throw InvalidChain();return await db.Set<ReleasePromotion>().SingleOrDefaultAsync(p=>p.Id==promotionId&&p.TargetReleaseId==release.Id&&p.TargetEnvironmentId==environment&&p.ArtifactId==release.ArtifactId&&p.SourceReleaseId==release.SourceReleaseId,ct)??throw InvalidChain();}
   if(release.RecoveryOf is Guid recovery){if(release.ReleaseType!="retry"||release.RollbackOf is not null)throw InvalidChain();id=recovery;continue;}
   if(release.RollbackOf is Guid rollback){if(release.ReleaseType!="rollback")throw InvalidChain();id=rollback;continue;}return null;
  }throw InvalidChain();
 }
 public async Task<PromotionDto> ReadAsync(Guid id,ActorContext actor,CancellationToken ct)
 {
  var p=await db.Set<ReleasePromotion>().AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw ScopeResolver.Missing();foreach(var environment in new[]{p.SourceEnvironmentId,p.TargetEnvironmentId}){var scope=await scopes.EnvironmentAsync(environment,ct);if(scope.OrganizationId!=p.OrganizationId||scope.ProjectId!=p.ProjectId||!await auth.CanAsync(actor,"release.read",new("environment",environment,scope),ct)||!await auth.CanAsync(actor,"environment.read",new("environment",environment,scope),ct))throw ScopeResolver.Missing();}
  var artifact=await artifacts.GetAsync(p.ArtifactId,actor,ct);var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(n=>n.EnvironmentId==p.TargetEnvironmentId&&n.Enabled).OrderBy(n=>n.Id).Select(n=>new PromotionNodeState(n.Id,n.NodeName,n.CurrentConfigVersion,n.CurrentDeploymentSequence,n.Status,n.LastHeartbeatAt)).ToArrayAsync(ct);
  var latest=await db.Set<ReleasePromotionEvent>().AsNoTracking().Where(e=>e.PromotionId==id&&e.Phase=="Deployment"&&e.ReleaseId!=null).OrderByDescending(e=>e.OccurredAt).ThenByDescending(e=>e.Id).Select(e=>e.ReleaseId).FirstOrDefaultAsync(ct);
  return PromotionMappingService.View(p,artifact.ArtifactHash,await mappings.LoadAsync(id,ct)) with{Deployment=new(latest??p.TargetReleaseId,nodes)};
 }
 private static ApiException InvalidChain()=>new(409,"invalid_delivery_recovery_chain","发布恢复追溯存在循环、跨环境或无效正式关联。");
}

using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Delivery.Pipelines;

internal static class PipelineAttemptFacts
{
 internal static async Task<ReleasePromotion?> FormalAsync(WebApiDbContext db,ReleasePipelineStageAttempt attempt,CancellationToken ct)
 {
  // A fresh, unemitted attempt has no formal candidate, even when its history does.
  if(attempt.PromotionId is null&&attempt.ActualReleaseId is null)return null;
  var origin=attempt;var depth=0;
  while(origin.PromotionId is null&&origin.OriginAttemptId is Guid id){
   if(++depth>100)throw Changed();
   var previous=await db.Set<ReleasePipelineStageAttempt>().SingleOrDefaultAsync(a=>a.Id==id&&a.RunStageId==attempt.RunStageId&&a.RunId==attempt.RunId&&a.ProjectId==attempt.ProjectId&&a.EnvironmentId==attempt.EnvironmentId,ct)??throw Changed();
   if(previous.AttemptNo>=origin.AttemptNo)throw Changed();origin=previous;
  }
  if(origin.PromotionId is not Guid promotionId)return null;
  var formal=await db.Set<ReleasePromotion>().SingleOrDefaultAsync(p=>p.Id==promotionId&&p.PipelineRunStageId==attempt.RunStageId&&p.StageAttemptId==origin.Id&&p.ProjectId==attempt.ProjectId&&p.TargetEnvironmentId==attempt.EnvironmentId,ct)??throw Changed();
  if(attempt.ActualReleaseId is Guid release&&(await PromotionReadService.ResolvePromotionWithinAsync(db,release,ct))?.Id!=formal.Id)throw Changed();
  return formal;
 }
 private static ApiException Changed()=>new(409,"pipeline_attempt_formal_changed","阶段尝试与实际发布的正式申请关联已变化，请刷新上下文。");
}

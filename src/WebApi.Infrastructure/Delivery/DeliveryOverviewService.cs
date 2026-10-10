using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Delivery;
public sealed class DeliveryOverviewService(WebApiDbContext db,ScopeResolver scopes,AuthorizationService auth,WebApi.Infrastructure.Delivery.Pipelines.PipelineReadService pipelines)
{
 public async Task<DeliveryOverviewDto> ReadAsync(Guid projectId,int page,int pageSize,ActorContext actor,CancellationToken ct)
 {
  using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(10));var token=budget.Token;
  try{
   await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);await db.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '10000'",token);
   var project=await scopes.ProjectAsync(projectId,token);var envs=await db.Set<EnvironmentRecord>().AsNoTracking().Where(e=>e.ProjectId==projectId).OrderBy(e=>e.SortOrder).ThenBy(e=>e.Id).ToArrayAsync(token);
   var readable=new List<EnvironmentRecord>();var contracts=new List<Guid>();var policies=new List<Guid>();
   foreach(var env in envs){var scope=project with{EnvironmentId=env.Id};if(!await auth.CanAsync(actor,"release.read",new("environment",env.Id,scope),token)||!await auth.CanAsync(actor,"environment.read",new("environment",env.Id,scope),token))continue;readable.Add(env);var contract=true;foreach(var permission in new[]{"route.read","api.read","api.version.read","api.schema.read"})contract&=await auth.CanAsync(actor,permission,new("environment",env.Id,scope),token);if(contract)contracts.Add(env.Id);if(await auth.CanAsync(actor,"policy.read",new("environment",env.Id,scope),token))policies.Add(env.Id);}
   if(readable.Count==0)throw ScopeResolver.Missing();var ids=readable.Select(e=>e.Id).ToArray();var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(n=>ids.Contains(n.EnvironmentId)&&n.Enabled).OrderBy(n=>n.Id).Select(n=>new{n.EnvironmentId,Node=new PromotionNodeState(n.Id,n.NodeName,n.CurrentConfigVersion,n.CurrentDeploymentSequence,n.Status,n.LastHeartbeatAt)}).ToArrayAsync(token);
   var environments=readable.Select(e=>new DeliveryEnvironmentDto(e.Id,e.Code,e.Name,e.IsProduction,e.DesiredConfigVersion,e.DeploymentSequence,nodes.Where(n=>n.EnvironmentId==e.Id).Select(n=>n.Node).ToArray())).ToArray();var coverage=await auth.CanAsync(actor,"release.read",new("project",projectId,project),token)&&await auth.CanAsync(actor,"environment.read",new("project",projectId,project),token)?"Complete":"Partial";
   var pipeline=await pipelines.OverviewAsync(projectId,page,pageSize,actor,token);
   if(contracts.Count==0){await transaction.CommitAsync(token);return new(projectId,coverage,environments,"Restricted",null,null,pipeline.Page,pipeline.Counts,pipeline.Visibility);}
   var sourceIds=contracts.ToArray();var policyIds=policies.ToArray();
   var query=db.Set<ReleasePromotion>().FromSqlInterpolated($"SELECT p.* FROM release_promotions p JOIN release_artifacts a ON a.id=p.artifact_id WHERE p.project_id={projectId} AND p.gate_origin='ProjectConnection' AND p.source_environment_id=ANY({sourceIds}) AND p.target_environment_id=ANY({ids}) AND (p.source_environment_id=ANY({policyIds}) OR NOT jsonb_path_exists(a.canonical_content, '$.routes[*].policies[*]'))").AsNoTracking();
   var total=await query.TagWith("delivery-authorized-promotion-count").CountAsync(token);page=Math.Clamp(page,1,1000000);pageSize=Math.Clamp(pageSize,1,100);
   var counts=await query.GroupBy(p=>1).Select(g=>new DeliveryCounts(g.Count(p=>p.Status=="WaitingApproval"),g.Count(p=>p.Status=="Verifying"),g.Count(p=>p.Status=="DeploymentFailed"),g.Count(p=>p.Status=="VerificationFailed"))).FirstOrDefaultAsync(token)??new(0,0,0,0);
   var rows=await (from p in query.OrderByDescending(p=>p.CreatedAt).ThenBy(p=>p.Id).Skip((page-1)*pageSize).Take(pageSize) join a in db.Set<ReleaseArtifact>() on p.ArtifactId equals a.Id select new PromotionSummaryDto(p.Id,p.ArtifactId,a.ArtifactHash,p.SourceEnvironmentId,p.TargetEnvironmentId,p.SourceReleaseId,p.TargetReleaseId,p.Status,p.RequestedBy,p.CreatedAt,p.Revision)).TagWith("delivery-bounded-promotion-page").ToArrayAsync(token);
   await transaction.CommitAsync(token);return new(projectId,coverage,environments,"Visible",new(rows,total,page,pageSize),counts,pipeline.Page,pipeline.Counts,pipeline.Visibility);
  }catch(OperationCanceledException)when(!ct.IsCancellationRequested&&budget.IsCancellationRequested){throw Timeout();}catch(PostgresException e)when(e.SqlState=="57014"&&!ct.IsCancellationRequested){throw Timeout();}
 }
 private static ApiException Timeout()=>new(503,"delivery_query_timeout","交付查询超时，请稍后重试。");
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Gateway;
public sealed class ReleaseTimeoutService(WebApiDbContext db,WebApi.Infrastructure.Delivery.PromotionExecutionService deliveryExecution)
{
    public async Task<int> ExpireAsync(CancellationToken ct=default)
    {
        var now=DateTimeOffset.UtcNow;var rows=await db.Set<ReleaseRecord>().AsNoTracking().Where(r=>r.Status=="Publishing"&&r.DeadlineAt<=now).Select(r=>new {r.Id,r.EnvironmentId}).Take(100).ToArrayAsync(ct);var count=0;
        foreach(var item in rows)
        {await using var tx=await db.Database.BeginTransactionAsync(ct);var initial=await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r=>r.Id==item.Id,ct);await deliveryExecution.LockReleaseAsync(initial,ct);var r=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==item.Id,ct);if(r.Status=="Publishing"&&r.DeadlineAt<=DateTimeOffset.UtcNow) {await FailAsync(db,r,"ack_timeout",ct);await deliveryExecution.RecordDeploymentStateAsync(r,ct);await db.SaveChangesAsync(ct);count++;}await tx.CommitAsync(ct);db.ChangeTracker.Clear();}return count;
    }
    internal static async Task FailAsync(WebApiDbContext db,ReleaseRecord r,string code,CancellationToken ct)
    {
        r.Status="Failed";r.FailureCode=code;r.CompletedAt=DateTimeOffset.UtcNow;var config=await db.Set<GatewayConfigVersion>().SingleAsync(v=>v.EnvironmentId==r.EnvironmentId&&v.VersionNo==r.ToConfigVersion,ct);config.Status="Failed";
        var project=await db.Set<EnvironmentRecord>().Where(e=>e.Id==r.EnvironmentId).Select(e=>e.ProjectId).SingleAsync(ct);var org=await db.Set<Project>().Where(p=>p.Id==project).Select(p=>p.OrganizationId).SingleAsync(ct);
        db.Add(new AuditLog {EnvironmentId=r.EnvironmentId,ProjectId=project,OrganizationId=org,UserId=r.PublishRequestedBy,Action="release.failed",ResourceType="ReleaseRecord",ResourceId=r.Id.ToString(),TraceId=r.PublishTraceId,AfterJson=JsonSerializer.Serialize(new {error=code,configVersion=r.ToConfigVersion,deploymentSequence=r.DeploymentSequence})});await db.SaveChangesAsync(ct);
    }
}

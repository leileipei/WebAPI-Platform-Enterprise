using WebApi.Contracts.Releases;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Governance;
using Microsoft.EntityFrameworkCore;
using WebApi.ControlPlane.Governance;
namespace WebApi.ControlPlane.Releases;
public static class ReleaseEndpoints
{
    public static void MapReleases(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        g.MapGet("/organizations/{id:guid}/approval-flows",async(Guid id,HttpContext ctx,ApprovalFlowService service,CancellationToken ct)=>Results.Ok(await service.ListAsync(id,ctx.Actor(),ct)));
        g.MapPost("/organizations/{id:guid}/approval-flows",async(Guid id,SaveApprovalFlowRequest request,HttpContext ctx,ApprovalFlowService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveAsync(id,null,request,null,ctx.Actor(),ct)));
        g.MapPut("/approval-flows/{id:guid}",async(Guid id,SaveApprovalFlowRequest request,HttpContext ctx,ApprovalFlowService service,WebApiDbContext db,CancellationToken ct)=>{var f=await db.Set<ApprovalFlow>().AsNoTracking().SingleOrDefaultAsync(f=>f.Id==id,ct)??throw ScopeResolver.Missing();return GovernanceEndpoints.Command(ctx,await service.SaveAsync(f.OrganizationId,id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct));});
        g.MapGet("/environments/{id:guid}/releases",async(Guid id,HttpContext ctx,ReleaseService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.ListAsync(id,ctx.Actor(),page??1,pageSize??50,ct)));
        g.MapPost("/environments/{id:guid}/releases",async(Guid id,CreateReleaseRequest request,HttpContext ctx,ReleaseService service,CancellationToken ct)=>Results.Ok(await service.CreateAsync(id,request,ctx.Actor(),ct)));
        g.MapGet("/releases/{id:guid}",async(Guid id,HttpContext ctx,ReleaseService service,CancellationToken ct)=>Results.Ok(await service.GetAsync(id,ctx.Actor(),ct)));
        g.MapPost("/releases/{id:guid}/submit",async(Guid id,HttpContext ctx,ReleaseService service,CancellationToken ct)=>Results.Ok(await service.SubmitAsync(id,ctx.Actor(),ct)));
        g.MapPost("/releases/{id:guid}/publish",async(Guid id,HttpContext ctx,PublishCoordinator service,CancellationToken ct)=>Results.Ok(await service.StartAsync(id,ctx.Actor(),ct)));
        g.MapPost("/releases/{id:guid}/rollback",async(Guid id,CreateRollbackRequest request,HttpContext ctx,RollbackService service,CancellationToken ct)=>Results.Ok(await service.CreateAsync(id,request.TargetConfigVersion,ctx.Actor(),ct)));
        g.MapPost("/releases/{id:guid}/retry",async(Guid id,HttpContext ctx,ReleaseRecoveryService service,CancellationToken ct)=>Results.Ok(await service.RetryAsync(id,ctx.Actor(),ct)));
        g.MapPost("/releases/{id:guid}/approve",async(Guid id,ApprovalActionRequest request,HttpContext ctx,ReleaseService service,CancellationToken ct)=>Results.Ok(await service.ApproveAsync(id,request.Comment,ctx.Actor(),ct)));
        g.MapPost("/releases/{id:guid}/reject",async(Guid id,ApprovalActionRequest request,HttpContext ctx,ReleaseService service,CancellationToken ct)=>Results.Ok(await service.RejectAsync(id,request.Comment,ctx.Actor(),ct)));
        g.MapPost("/releases/{id:guid}/cancel",async(Guid id,HttpContext ctx,ReleaseService service,CancellationToken ct)=>Results.Ok(await service.CancelAsync(id,ctx.Actor(),ct)));
    }
}

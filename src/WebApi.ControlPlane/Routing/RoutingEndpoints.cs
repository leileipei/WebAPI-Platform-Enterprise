using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.ControlPlane.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Routing;
namespace WebApi.ControlPlane.Routing;
public static class RoutingEndpoints
{
    public static void MapRouting(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        g.MapGet("/environments/{id:guid}/routes",async(Guid id,HttpContext ctx,RouteService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.ListAsync(id,ctx.Actor(),page??1,pageSize??50,ct)));
        g.MapGet("/routes/{id:guid}",async(Guid id,HttpContext ctx,RouteService service,CancellationToken ct)=>{var r=await service.GetAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(r.Revision);return Results.Ok(r);});
        g.MapPost("/environments/{id:guid}/routes",async(Guid id,SaveRouteRequest request,HttpContext ctx,RouteService service,CancellationToken ct)=>{if(request.Id is not null) throw new ApiException(422,"unexpected_id","创建路由不能指定ID。");return GovernanceEndpoints.Command(ctx,await service.SaveAsync(id,request,null,ctx.Actor(),ct));});
        g.MapPut("/routes/{id:guid}",async(Guid id,SaveRouteRequest request,HttpContext ctx,RouteService service,WebApiDbContext db,CancellationToken ct)=>{if(request.Id is Guid bodyId&&bodyId!=id) throw new ApiException(422,"mismatched_id","请求ID不一致。");var r=await db.Set<ApiRoute>().AsNoTracking().SingleOrDefaultAsync(r=>r.Id==id,ct)??throw ScopeResolver.Missing();return GovernanceEndpoints.Command(ctx,await service.SaveAsync(r.EnvironmentId,request with {Id=id},ctx.Request.Headers.IfMatch,ctx.Actor(),ct));});
        g.MapDelete("/routes/{id:guid}",async(Guid id,HttpContext ctx,RouteService service,CancellationToken ct)=>{await service.DeleteAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
        g.MapGet("/environments/{id:guid}/clusters",async(Guid id,HttpContext ctx,ClusterService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.ListAsync(id,ctx.Actor(),page??1,pageSize??50,ct)));
        g.MapPost("/environments/{id:guid}/clusters",async(Guid id,SaveClusterRequest request,HttpContext ctx,ClusterService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveAsync(id,null,request,null,ctx.Actor(),ct)));
        g.MapGet("/clusters/{id:guid}",async(Guid id,HttpContext ctx,ClusterService service,CancellationToken ct)=>{var c=await service.GetAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(c.Revision);return Results.Ok(c);});
        g.MapPut("/clusters/{id:guid}",async(Guid id,SaveClusterRequest request,HttpContext ctx,ClusterService service,WebApiDbContext db,CancellationToken ct)=>{var c=await db.Set<UpstreamCluster>().AsNoTracking().SingleOrDefaultAsync(c=>c.Id==id,ct)??throw ScopeResolver.Missing();return GovernanceEndpoints.Command(ctx,await service.SaveAsync(c.EnvironmentId,id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct));});
        g.MapDelete("/clusters/{id:guid}",async(Guid id,HttpContext ctx,ClusterService service,CancellationToken ct)=>{await service.DeleteAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
        g.MapGet("/clusters/{id:guid}/destinations",async(Guid id,HttpContext ctx,ClusterService service,CancellationToken ct)=>Results.Ok((await service.GetAsync(id,ctx.Actor(),ct)).Destinations));
        g.MapPost("/clusters/{id:guid}/destinations",async(Guid id,SaveDestinationRequest request,HttpContext ctx,ClusterService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveDestinationAsync(id,null,request,null,ctx.Actor(),ct)));
        g.MapPut("/destinations/{id:guid}",async(Guid id,SaveDestinationRequest request,HttpContext ctx,ClusterService service,WebApiDbContext db,CancellationToken ct)=>{var d=await db.Set<UpstreamDestination>().AsNoTracking().SingleOrDefaultAsync(d=>d.Id==id,ct)??throw ScopeResolver.Missing();return GovernanceEndpoints.Command(ctx,await service.SaveDestinationAsync(d.ClusterId,id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct));});
        g.MapDelete("/destinations/{id:guid}",async(Guid id,HttpContext ctx,ClusterService service,CancellationToken ct)=>{await service.DeleteDestinationAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
    }
}

using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Gateway;
namespace WebApi.ControlPlane.Gateway;
public static class GatewayReadEndpoints
{
    public static void MapGatewayRead(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        g.MapGet("/environments/{id:guid}/gateway-nodes",async(Guid id,HttpContext ctx,GatewayReadService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.NodesAsync(id,ctx.Actor(),page??1,pageSize??50,ct)));
        g.MapGet("/gateway-nodes/{id:guid}",async(Guid id,HttpContext ctx,GatewayReadService service,CancellationToken ct)=>{var node=await service.NodeAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=node.ETag;return Results.Ok(node);});
        g.MapGet("/gateway-nodes/{id:guid}/events",async(Guid id,HttpContext ctx,GatewayReadService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.EventsAsync(id,ctx.Actor(),page??1,pageSize??50,ct)));
        g.MapPut("/gateway-nodes/{id:guid}/enabled",async(Guid id,SetNodeEnabledRequest request,HttpContext ctx,GatewayReadService service,CancellationToken ct)=>{var node=await service.SetEnabledAsync(id,request.Enabled,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);ctx.Response.Headers.ETag=node.ETag;return Results.Ok(node);});
        g.MapGet("/environments/{id:guid}/snapshots",async(Guid id,HttpContext ctx,GatewayReadService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.SnapshotsAsync(id,ctx.Actor(),page??1,pageSize??50,ct)));
        g.MapGet("/environments/{id:guid}/snapshots/{version:long}",async(Guid id,long version,HttpContext ctx,GatewayReadService service,CancellationToken ct)=>Results.Ok(await service.SnapshotAsync(id,version,ctx.Actor(),ct)));
        g.MapGet("/environments/{id:guid}/snapshots/{version:long}/download",async(Guid id,long version,HttpContext ctx,GatewayReadService service,CancellationToken ct)=>{var data=await service.SnapshotAsync(id,version,ctx.Actor(),ct);ctx.Response.Headers.CacheControl="no-store";return Results.File(JsonSerializer.SerializeToUtf8Bytes(data,CanonicalJson.Options),"application/json",$"snapshot-{version}-management.json");});
    }
}

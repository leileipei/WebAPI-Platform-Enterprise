using WebApi.Contracts.Releases;
using WebApi.ControlPlane.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Delivery;
namespace WebApi.ControlPlane.Releases;
public static class DeliveryEndpoints
{
    public static void MapDelivery(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        g.MapGet("/projects/{id:guid}/delivery-policy",async(Guid id,HttpContext ctx,ProjectDeliveryPolicyService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";var value=await service.GetAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=WebApi.Contracts.Common.RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPut("/projects/{id:guid}/delivery-policy",async(Guid id,SaveDeliveryPolicyRequest request,HttpContext ctx,ProjectDeliveryPolicyService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return GovernanceEndpoints.Command(ctx,await service.SaveAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct));});
        g.MapPost("/releases/{id:guid}/artifacts",async(Guid id,HttpContext ctx,ReleaseArtifactService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.CreateAsync(id,ctx.Actor(),ct));});
        g.MapGet("/release-artifacts/{id:guid}",async(Guid id,HttpContext ctx,ReleaseArtifactService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.GetAsync(id,ctx.Actor(),ct));});
    }
}

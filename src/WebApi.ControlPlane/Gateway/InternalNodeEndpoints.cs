using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Gateway;
namespace WebApi.ControlPlane.Gateway;
public static class InternalNodeEndpoints
{
    private static string Secret(HttpContext ctx)
    {var value=ctx.Request.Headers.Authorization.ToString();if(!value.StartsWith("Bearer ",StringComparison.Ordinal)||value.Length>1031) throw NodeIdentityService.Denied();return value[7..];}
    public static void MapInternalNodes(this WebApplication app)
    {
        var g=app.MapGroup("/internal/v1/nodes").AllowAnonymous().AddEndpointFilter<RequestValidationFilter>();
        g.MapPost("/register",async(RegisterNodeRequest request,HttpContext ctx,NodeRegistry nodes,CancellationToken ct)=>Results.Ok(await nodes.RegisterAsync(request,Secret(ctx),ct)));
        g.MapPost("/{id:guid}/heartbeat",async(Guid id,NodeHeartbeat request,HttpContext ctx,NodeIdentityService identities,NodeRegistry nodes,CancellationToken ct)=>{var identity=await identities.AuthenticateAsync(id,Secret(ctx),ct);await nodes.HeartbeatAsync(identity,request,ct);return Results.NoContent();});
        g.MapGet("/{id:guid}/desired",async(Guid id,HttpContext ctx,NodeIdentityService identities,NodeRegistry nodes,CancellationToken ct)=>{var identity=await identities.AuthenticateAsync(id,Secret(ctx),ct);ctx.Response.Headers.CacheControl="no-store";var desired=await nodes.DesiredAsync(identity,ct);return desired is null?Results.NoContent():Results.Ok(desired);});
        g.MapPost("/{id:guid}/ack",async(Guid id,NodeAck request,HttpContext ctx,NodeIdentityService identities,AckService acks,CancellationToken ct)=>{var identity=await identities.AuthenticateAsync(id,Secret(ctx),ct);return Results.Ok(await acks.RecordAsync(id,request,identity,ct));});
    }
}

using WebApi.Contracts.Common;
using WebApi.ControlPlane.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Sso;
namespace WebApi.ControlPlane.Sso;
public static class ExternalIdentityEndpoints
{
    private static async Task<byte[]> Body(HttpContext context,CancellationToken ct)
    {
        if(context.Request.ContentLength>8192)throw Large();
        using var buffer=new MemoryStream();var bytes=new byte[1024];
        while(true){var count=await context.Request.Body.ReadAsync(bytes,ct);if(count==0)break;if(buffer.Length+count>8192)throw Large();buffer.Write(bytes,0,count);}
        return buffer.ToArray();
    }
    public static void MapExternalIdentities(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/users").RequireAuthorization();
        group.AddEndpointFilter(async(context,next)=>{context.HttpContext.Response.Headers.CacheControl="no-store";return await next(context);});
        group.MapPost("/sso",async(HttpContext ctx,ExternalIdentityService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.CreateAsync(SsoIdentityValidator.ParseCreate(await Body(ctx,ct)),new(ctx.Request.Headers["Idempotency-Key"].ToString()),ctx.Actor(),ct)));
        group.MapGet("/{userId:guid}/external-identity",async(Guid userId,HttpContext ctx,ExternalIdentityService service,CancellationToken ct)=>{
            var identity=await service.GetAsync(userId,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(identity.Revision);return Results.Ok(identity);
        });
        group.MapPut("/{userId:guid}/external-identity",async(Guid userId,HttpContext ctx,ExternalIdentityService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,
            await service.SaveAsync(userId,SsoIdentityValidator.ParseSave(await Body(ctx,ct)),ctx.Request.Headers.IfMatch,new(ctx.Request.Headers["Idempotency-Key"].ToString()),ctx.Actor(),ct)));
    }
    private static ApiException Large()=>new(413,"sso_request_too_large","身份绑定请求不能超过8192字节。");
}

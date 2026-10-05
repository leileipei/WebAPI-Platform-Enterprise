using WebApi.Contracts.Common;
using WebApi.ControlPlane.Security;
using WebApi.ControlPlane.Governance;
using WebApi.Infrastructure.Settings;
namespace WebApi.ControlPlane.Settings;
public static class SystemSettingsEndpoints
{
    private static async Task<byte[]> Body(HttpContext ctx,CancellationToken ct){if(ctx.Request.ContentLength>8192)throw new ApiException(413,"settings_too_large","设置请求不能超过8192字节。");using var buffer=new MemoryStream();var bytes=new byte[1024];while(true){var count=await ctx.Request.Body.ReadAsync(bytes,ct);if(count==0)break;if(buffer.Length+count>8192)throw new ApiException(413,"settings_too_large","设置请求不能超过8192字节。");buffer.Write(bytes,0,count);}return buffer.ToArray();}
    public static void MapSystemSettings(this WebApplication app)
    {
        app.MapGet("/api/v1/environments/{id:guid}/route-defaults",async(Guid id,HttpContext ctx,SystemSettingsService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.RouteDefaultsAsync(id,ctx.Actor(),ct));}).RequireAuthorization();
        var g=app.MapGroup("/api/v1/settings/system").RequireAuthorization();
        g.AddEndpointFilter(async(context,next)=>{context.HttpContext.Response.Headers.CacheControl="no-store";return await next(context);});
        g.MapGet("",async(HttpContext ctx,SystemSettingsService service,CancellationToken ct)=>Results.Ok(await service.ListAsync(ctx.Actor(),ct)));
        g.MapGet("/{group}",async(string group,HttpContext ctx,SystemSettingsService service,CancellationToken ct)=>{var value=await service.GetAsync(group,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPost("/{group}/validate",async(string group,HttpContext ctx,SystemSettingsService service,CancellationToken ct)=>Results.Ok(await service.ValidateAsync(group,SystemSettingsValidator.Parse(group,await Body(ctx,ct)),ctx.Actor(),ct)));
        g.MapPost("/{group}/preview",async(string group,HttpContext ctx,SystemSettingsService service,CancellationToken ct)=>Results.Ok(await service.PreviewAsync(group,SystemSettingsValidator.Parse(group,await Body(ctx,ct)),ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapPut("/{group}",async(string group,HttpContext ctx,SystemSettingsService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveAsync(group,SystemSettingsValidator.ParseSave(group,await Body(ctx,ct)),ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
    }
}

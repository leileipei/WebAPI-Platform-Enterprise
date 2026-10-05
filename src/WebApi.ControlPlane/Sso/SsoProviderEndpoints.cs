using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Sso;
using WebApi.ControlPlane.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Sso;
using Microsoft.Extensions.Options;
namespace WebApi.ControlPlane.Sso;
public static class SsoProviderEndpoints
{
    private static async Task<byte[]> Body(HttpContext context,CancellationToken ct)
    {
        if(context.Request.ContentLength>16384)throw Large();
        using var buffer=new MemoryStream();var bytes=new byte[2048];
        while(true){var count=await context.Request.Body.ReadAsync(bytes,ct);if(count==0)break;if(buffer.Length+count>16384)throw Large();buffer.Write(bytes,0,count);}
        return buffer.ToArray();
    }
    private static async Task Empty(HttpContext context,CancellationToken ct)
    {
        var bytes=await Body(context,ct);if(bytes.Length==0)return;
        try {using var doc=JsonDocument.Parse(bytes);if(doc.RootElement.ValueKind!=JsonValueKind.Object||doc.RootElement.EnumerateObject().Any())throw Invalid();}
        catch(JsonException){throw Invalid();}
    }
    private static CommandRequestContext Command(HttpContext ctx)=>new(ctx.Request.Headers["Idempotency-Key"].ToString());
    public static void MapSsoProviders(this WebApplication app)
    {
        app.MapGet("/api/v1/auth/sso/providers",async(Guid? organizationId,HttpContext ctx,SsoProviderService service,CancellationToken ct)=>{
            ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.PublicAsync(organizationId,ct));
        }).AllowAnonymous();
        var group=app.MapGroup("/api/v1/settings/sso/providers").RequireAuthorization();
        group.AddEndpointFilter(async(context,next)=>{context.HttpContext.Response.Headers.CacheControl="no-store";return await next(context);});
        group.MapGet("",async(Guid? organizationId,string? search,int? page,int? pageSize,HttpContext ctx,SsoProviderService service,CancellationToken ct)=>
            Results.Ok(await service.ListAsync(ctx.Actor(),organizationId,search,page??1,pageSize??50,ct)));
        group.MapGet("/{id:guid}",async(Guid id,HttpContext ctx,SsoProviderService service,CancellationToken ct)=>{
            var view=await service.GetAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(view.Revision);return Results.Ok(view);
        });
        group.MapPost("",async(HttpContext ctx,SsoProviderService service,IOptions<SsoOptions> options,IHostEnvironment environment,CancellationToken ct)=>
            GovernanceEndpoints.Command(ctx,await service.CreateAsync(SsoProviderValidator.Parse(await Body(ctx,ct),environment.IsDevelopment()&&options.Value.FixtureEnabled),Command(ctx),ctx.Actor(),ct)));
        group.MapPut("/{id:guid}",async(Guid id,HttpContext ctx,SsoProviderService service,IOptions<SsoOptions> options,IHostEnvironment environment,CancellationToken ct)=>
            GovernanceEndpoints.Command(ctx,await service.SaveAsync(id,SsoProviderValidator.Parse(await Body(ctx,ct),environment.IsDevelopment()&&options.Value.FixtureEnabled),ctx.Request.Headers.IfMatch,Command(ctx),ctx.Actor(),ct)));
        group.MapPost("/{id:guid}/test",async(Guid id,HttpContext ctx,SsoProviderService service,CancellationToken ct)=>{
            await Empty(ctx,ct);return GovernanceEndpoints.Command(ctx,await service.TestAsync(id,ctx.Request.Headers.IfMatch,Command(ctx),ctx.Actor(),ct));
        });
        foreach(var operation in new[]{SsoProviderOperation.Enable,SsoProviderOperation.Disable,SsoProviderOperation.Default,SsoProviderOperation.Rotate})
        {
            group.MapPost("/{id:guid}/"+operation.ToString().ToLowerInvariant(),async(Guid id,HttpContext ctx,SsoProviderService service,CancellationToken ct)=>{
                await Empty(ctx,ct);return GovernanceEndpoints.Command(ctx,await service.OperateAsync(id,operation,ctx.Request.Headers.IfMatch,Command(ctx),ctx.Actor(),ct));
            });
        }
    }
    private static ApiException Large()=>new(413,"sso_request_too_large","SSO 请求不能超过16384字节。");
    private static ApiException Invalid()=>new(422,"invalid_sso_request","该操作不接受额外字段。");
}

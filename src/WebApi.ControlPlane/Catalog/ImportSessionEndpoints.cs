using WebApi.Contracts.Catalog;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Catalog;
namespace WebApi.ControlPlane.Catalog;
public static class ImportSessionEndpoints
{
    public static void MapImportSessions(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/openapi/import-previews").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        group.MapPost("",async(CreateImportPreviewRequest request,HttpContext ctx,ImportPreviewService service,CancellationToken ct)=>Results.Ok(await service.CreateAsync(request,ctx.Actor(),ct)));
        group.MapGet("/{id:guid}",async(Guid id,HttpContext ctx,ImportPreviewService service,CancellationToken ct)=>Results.Ok(await service.GetAsync(id,ctx.Actor(),ct)));
        group.MapPost("/{id:guid}/commit",async(Guid id,CommitImportSessionRequest request,HttpContext ctx,ImportPreviewService service,CancellationToken ct)=>Results.Ok(await service.CommitAsync(id,request,ctx.Actor(),ct)));
        group.MapDelete("/{id:guid}",async(Guid id,HttpContext ctx,ImportPreviewService service,CancellationToken ct)=>{await service.RevokeAsync(id,ctx.Actor(),ct);return Results.NoContent();});
    }
}

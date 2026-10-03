using WebApi.Contracts.Catalog;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Catalog;
namespace WebApi.ControlPlane.Catalog;
public static class OpenApiImportEndpoints
{
    public static void MapOpenApiImport(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1/openapi").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        g.MapPost("/import-preview",async(ImportPreviewRequest request,HttpContext ctx,OpenApiImportService service,CancellationToken ct)=>Results.Ok(await service.PreviewAsync(request,ctx.Actor(),ct)));
        g.MapPost("/import-commit",async(ImportCommitRequest request,HttpContext ctx,OpenApiImportService service,CancellationToken ct)=>Results.Ok(await service.CommitAsync(request,ctx.Actor(),ct)));
    }
}

using WebApi.Contracts.Catalog;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Catalog;
namespace WebApi.ControlPlane.Catalog;
public static class SchemaValidationEndpoints
{
    public static void MapSchemaValidation(this WebApplication app)
    {
        app.MapPost("/api/v1/versions/{id:guid}/schema-validation",async(Guid id,ValidateSchemaRequest request,HttpContext context,SchemaValidationService service,CancellationToken ct)=>Results.Ok(await service.ValidateAsync(id,request,context.Actor(),ct))).RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
    }
}

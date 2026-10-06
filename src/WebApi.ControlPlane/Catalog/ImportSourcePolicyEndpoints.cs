using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.ControlPlane.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Catalog;
namespace WebApi.ControlPlane.Catalog;
public static class ImportSourcePolicyEndpoints
{
    public static void MapImportSourcePolicies(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/projects/{id:guid}/import-source-policy").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        group.MapGet("", async (Guid id, HttpContext ctx, ImportSourcePolicyService service, CancellationToken ct) => { var value = await service.GetAsync(id, ctx.Actor(), ct); ctx.Response.Headers.ETag = RevisionTag.Format(value.Revision); return Results.Ok(value); });
        group.MapPut("", async (Guid id, SaveImportSourcePolicyRequest request, HttpContext ctx, ImportSourcePolicyService service, CancellationToken ct) => GovernanceEndpoints.Command(ctx, await service.SaveAsync(id, request, ctx.Request.Headers.IfMatch, ctx.Actor(), ct)));
    }
}

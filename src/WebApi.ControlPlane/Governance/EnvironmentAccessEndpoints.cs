using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Governance;
namespace WebApi.ControlPlane.Governance;
public static class EnvironmentAccessEndpoints
{
    private static AccessView View(string? view)=>view switch {null or "running"=>AccessView.Running,"working"=>AccessView.Working,_=>throw new ApiException(422,"invalid_access_view","请选择 working 或 running 视图。")};
    public static void MapEnvironmentAccess(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1/environments/{environmentId:guid}/apis/{apiId:guid}").RequireAuthorization();
        g.MapGet("/access-addresses",async(Guid environmentId,Guid apiId,Guid? versionId,string? view,HttpContext ctx,EnvironmentApiAddressService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.GetAsync(environmentId,apiId,versionId,View(view),ctx.Actor(),ct));});
        g.MapGet("/openapi",async(Guid environmentId,Guid apiId,Guid? versionId,string? view,HttpContext ctx,EnvironmentApiDocumentService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";using var doc=await service.BuildAsync(environmentId,apiId,versionId,View(view),ctx.Actor(),ct);return Results.Text(doc.RootElement.GetRawText(),"application/json");});
    }
}

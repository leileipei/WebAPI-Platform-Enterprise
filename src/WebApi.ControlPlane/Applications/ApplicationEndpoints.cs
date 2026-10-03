using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.ControlPlane.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Applications;
namespace WebApi.ControlPlane.Applications;
public static class ApplicationEndpoints
{
    public static void MapApplications(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        g.MapGet("/applications",async(HttpContext ctx,ApplicationService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.ListAsync(ctx.Actor(),page??1,pageSize??50,ct)));
        g.MapPost("/applications",async(SaveApplicationRequest request,HttpContext ctx,ApplicationService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.CreateAsync(request,ctx.Actor(),ct)));
        g.MapGet("/applications/{id:guid}",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>{var value=await service.DetailAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Application.Revision);return Results.Ok(value);});
        g.MapPut("/applications/{id:guid}",async(Guid id,SaveApplicationRequest request,HttpContext ctx,ApplicationService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapDelete("/applications/{id:guid}",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>{await service.DeleteAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
        g.MapGet("/applications/{id:guid}/credentials",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>Results.Ok((await service.DetailAsync(id,ctx.Actor(),ct)).Credentials));
        g.MapPost("/applications/{id:guid}/credentials",async(Guid id,CredentialCreateRequest request,HttpContext ctx,ApplicationService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.CreateCredentialAsync(id,request,ctx.Actor(),ct));});
        g.MapGet("/credentials/{id:guid}",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>{var value=await service.CredentialAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPut("/credentials/{id:guid}",async(Guid id,CredentialUpdateRequest request,HttpContext ctx,ApplicationService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveCredentialAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapGet("/applications/{id:guid}/permissions",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>Results.Ok((await service.DetailAsync(id,ctx.Actor(),ct)).Permissions));
        g.MapPost("/applications/{id:guid}/permissions",async(Guid id,PermissionSaveRequest request,HttpContext ctx,ApplicationService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SavePermissionAsync(id,null,request,null,ctx.Actor(),ct)));
        g.MapPut("/applications/{appId:guid}/permissions/{id:guid}",async(Guid appId,Guid id,PermissionSaveRequest request,HttpContext ctx,ApplicationService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SavePermissionAsync(appId,id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapDelete("/application-permissions/{id:guid}",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>{await service.DeletePermissionAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
    }
}

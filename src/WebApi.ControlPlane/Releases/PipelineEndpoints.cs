using WebApi.Contracts.Releases;
using WebApi.Contracts.Common;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Delivery.Pipelines;
namespace WebApi.ControlPlane.Releases;
public static class PipelineEndpoints
{
    public static void MapPipelines(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        g.MapGet("/projects/{id:guid}/release-pipelines",async(Guid id,int? page,int? pageSize,HttpContext ctx,PipelineDefinitionService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.ListAsync(id,page??1,pageSize??50,ctx.Actor(),ct));});
        g.MapPost("/projects/{id:guid}/release-pipelines",async(Guid id,PipelineDefinition request,HttpContext ctx,PipelineDefinitionService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";var value=await service.CreateAsync(id,request,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapGet("/release-pipelines/{id:guid}",async(Guid id,HttpContext ctx,PipelineDefinitionService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";var value=await service.GetAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPut("/release-pipelines/{id:guid}",async(Guid id,PipelineDefinition request,HttpContext ctx,PipelineDefinitionService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";var value=await service.SaveAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapGet("/release-pipelines/{id:guid}/versions",async(Guid id,HttpContext ctx,PipelineDefinitionService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.VersionsAsync(id,ctx.Actor(),ct));});
        g.MapPost("/release-pipelines/{id:guid}/versions",async(Guid id,HttpContext ctx,PipelineDefinitionService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.PublishVersionAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct));});
        g.MapPost("/projects/{id:guid}/delivery-policy/activate-pipeline",async(Guid id,ActivatePipelineRequest request,HttpContext ctx,PipelineActivationService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";var value=await service.ActivateAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPost("/projects/{id:guid}/delivery-policy/restore-connection",async(Guid id,RestoreDeliveryPolicyRequest request,HttpContext ctx,PipelineActivationService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";var value=await service.RestoreAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPost("/release-pipelines/{id:guid}/archive",async(Guid id,HttpContext ctx,PipelineDefinitionService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";var value=await service.ArchiveAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
    }
}

using WebApi.Contracts.Common;
using WebApi.Contracts.Catalog;
using WebApi.ControlPlane.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
namespace WebApi.ControlPlane.Catalog;
public static class CatalogEndpoints
{
    public static void MapCatalog(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        g.MapGet("/projects/{id:guid}/apis",async(Guid id,[AsParameters]DirectoryFilter filter,HttpContext ctx,CatalogService service,ApiDirectoryService directory,CancellationToken ct)=>{
            if(filter.Advanced)return Results.Ok<object>(await directory.ListAsync(id,filter,ctx.Actor(),ct));
            return Results.Ok<object>(await service.ListAsync(id,ctx.Actor(),filter.Page??1,filter.PageSize??50,ct));
        });
        g.MapPost("/projects/{id:guid}/apis/export",async(Guid id,[AsParameters]DirectoryFilter filter,DirectoryExport request,HttpContext ctx,ApiDirectoryService directory,CancellationToken ct)=>
            Results.Ok(await directory.ExportAsync(id,filter,request,ctx.Actor(),ct)));
        g.MapPost("/projects/{id:guid}/apis",async(Guid id,SaveApiRequest request,HttpContext ctx,CatalogService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.CreateAsync(id,request,ctx.Actor(),ct)));
        g.MapGet("/apis/{id:guid}",async(Guid id,Guid? environmentId,HttpContext ctx,CatalogService service,CancellationToken ct)=>{var detail=await service.DetailAsync(id,ctx.Actor(),ct,environmentId);ctx.Response.Headers.ETag=RevisionTag.Format(detail.WorkingRevision);return Results.Ok(detail);});
        g.MapPut("/apis/{id:guid}",async(Guid id,SaveApiRequest request,HttpContext ctx,CatalogService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapDelete("/apis/{id:guid}",async(Guid id,HttpContext ctx,CatalogService service,CancellationToken ct)=>{await service.DeleteAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
        g.MapGet("/apis/{id:guid}/versions",async(Guid id,HttpContext ctx,CatalogService service,CancellationToken ct)=>Results.Ok(await service.VersionsAsync(id,ctx.Actor(),ct)));
        g.MapGet("/apis/{id:guid}/overview",async(Guid id,Guid environmentId,HttpContext ctx,ApiRuntimeOverview service,CancellationToken ct)=>
            Results.Ok(await service.GetAsync(id,environmentId,ctx.Actor(),ct)));
        g.MapPost("/versions/{id:guid}/retire",async(Guid id,HttpContext ctx,CatalogService service,CancellationToken ct)=>
            GovernanceEndpoints.Command(ctx,await service.RetireVersionAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapPost("/apis/{id:guid}/versions",async(Guid id,CreateVersionRequest request,HttpContext ctx,CatalogService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.CreateVersionAsync(id,request,ctx.Actor(),ct)));
        g.MapGet("/versions/{id:guid}",async(Guid id,HttpContext ctx,CatalogService service,CancellationToken ct)=>{var value=await service.VersionAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPut("/versions/{id:guid}",async(Guid id,CreateVersionRequest request,HttpContext ctx,CatalogService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveVersionAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapDelete("/versions/{id:guid}",async(Guid id,HttpContext ctx,CatalogService service,CancellationToken ct)=>{await service.DeleteVersionAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
        g.MapGet("/versions/{id:guid}/openapi",async(Guid id,HttpContext ctx,VersionContractSourceService service,string? representation,CancellationToken ct)=>{
            var document=await service.OpenApiAsync(id,representation,ctx.Actor(),ct);
            return Results.Text(document.Content,document.MediaType);
        });
        g.MapGet("/projects/{id:guid}/api-groups",async(Guid id,HttpContext ctx,CatalogService service,CancellationToken ct)=>Results.Ok(await service.GroupsAsync(id,ctx.Actor(),ct)));
        g.MapPost("/projects/{id:guid}/api-groups",async(Guid id,SaveGroupRequest request,HttpContext ctx,CatalogService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveGroupAsync(id,null,request,null,ctx.Actor(),ct)));
        g.MapPut("/api-groups/{id:guid}",async(Guid id,SaveGroupRequest request,HttpContext ctx,CatalogService service,WebApiDbContext db,CancellationToken ct)=>{var group=await db.Set<ApiGroup>().AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw ScopeResolver.Missing();return GovernanceEndpoints.Command(ctx,await service.SaveGroupAsync(group.ProjectId,id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct));});
        g.MapDelete("/api-groups/{id:guid}",async(Guid id,HttpContext ctx,CatalogService service,CancellationToken ct)=>{await service.DeleteGroupAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
        g.MapGet("/versions/{id:guid}/parameters",async(Guid id,HttpContext ctx,CatalogService service,WebApiDbContext db,CancellationToken ct)=>{var value=await service.ParametersAsync(id,ctx.Actor(),ct,includeExamples:true);ctx.Response.Headers.ETag=RevisionTag.Format(await db.Set<ApiVersion>().Where(x=>x.Id==id).Select(x=>x.Revision).SingleAsync(ct));return Results.Ok(value);});
        g.MapPut("/versions/{id:guid}/parameters",async(Guid id,IReadOnlyList<SaveParameterRequest> request,HttpContext ctx,CatalogService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveParametersAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapGet("/versions/{id:guid}/schemas",async(Guid id,HttpContext ctx,CatalogService service,WebApiDbContext db,CancellationToken ct)=>{var value=await service.SchemasAsync(id,ctx.Actor(),ct,includeExamples:true);ctx.Response.Headers.ETag=RevisionTag.Format(await db.Set<ApiVersion>().Where(x=>x.Id==id).Select(x=>x.Revision).SingleAsync(ct));return Results.Ok(value);});
        g.MapPut("/versions/{id:guid}/schemas",async(Guid id,IReadOnlyList<SaveSchemaRequest> request,HttpContext ctx,CatalogService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveSchemasAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
    }
}

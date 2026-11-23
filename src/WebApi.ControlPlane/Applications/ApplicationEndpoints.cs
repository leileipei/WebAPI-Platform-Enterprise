using WebApi.Contracts.CredentialUsage;
using WebApi.Infrastructure.CredentialUsage;
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
        g.MapGet("/applications",async(HttpContext ctx,ApplicationService service,Guid? organizationId,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.ListAsync(ctx.Actor(),page??1,pageSize??50,ct,organizationId)));
        g.MapPost("/applications",async(SaveApplicationRequest request,HttpContext ctx,ApplicationService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.CreateAsync(request,ctx.Actor(),ct)));
        g.MapGet("/applications/{id:guid}",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>{var value=await service.DetailAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Application.Revision);return Results.Ok(value);});
        g.MapPut("/applications/{id:guid}",async(Guid id,SaveApplicationRequest request,HttpContext ctx,ApplicationService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapDelete("/applications/{id:guid}",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>{await service.DeleteAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
        g.MapGet("/applications/{id:guid}/credential-rollout",async(Guid id,Guid environmentId,Guid releaseId,Guid newCredentialId,Guid oldCredentialId,string stage,HttpContext ctx,CredentialRolloutQuery query,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await query.ReadAsync(id,environmentId,releaseId,newCredentialId,oldCredentialId,stage,ctx.Actor(),ct));});
        g.MapGet("/applications/{id:guid}/overview",async(Guid id,HttpContext ctx,ApplicationOverviewQuery query,TimeProvider clock,Guid? environmentId,DateTimeOffset? from,DateTimeOffset? to,CancellationToken ct)=>
        {
            var now=clock.GetUtcNow();var end=to??new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,0,0,TimeSpan.Zero);ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(await query.ReadAsync(id,environmentId,new UsageWindow(from??end.AddHours(-24),end),ctx.Actor(),ct));
        });
        g.MapGet("/applications/{id:guid}/audit",async(Guid id,HttpContext ctx,ApplicationAuditQuery query,int? page,int? pageSize,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await query.ReadAsync(id,page??1,pageSize??50,ctx.Actor(),ct));});
        g.MapGet("/applications/{id:guid}/credential-usage",async(Guid id,HttpContext ctx,CredentialUsageQuery query,TimeProvider clock,Guid? environmentId,DateTimeOffset? from,DateTimeOffset? to,int? page,int? pageSize,CancellationToken ct)=>
        {
            var now=clock.GetUtcNow();var end=to??new DateTimeOffset(now.Year,now.Month,now.Day,now.Hour,0,0,TimeSpan.Zero);ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(await query.ReadAsync(id,environmentId,new UsageWindow(from??end.AddHours(-24),end),page??1,pageSize??50,ctx.Actor(),ct));
        });
        g.MapGet("/applications/{id:guid}/credentials",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>Results.Ok((await service.DetailAsync(id,ctx.Actor(),ct)).Credentials));
        g.MapPost("/applications/{id:guid}/credentials",async(Guid id,CredentialCreateRequest request,HttpContext ctx,CredentialCreateReceiptService service,CommandRequestContext command,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.CreateAsync(id,request,command,ctx.Actor(),ct));});
        g.MapGet("/credentials/{id:guid}",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>{var value=await service.CredentialAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPost("/credentials/{id:guid}/impact",async(Guid id,CredentialImpactRequest request,HttpContext ctx,CredentialImpactService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.PreviewAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct));});
        g.MapPut("/credentials/{id:guid}",async(Guid id,CredentialUpdateRequest request,HttpContext ctx,ApplicationService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveCredentialAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapGet("/applications/{id:guid}/permissions",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>Results.Ok((await service.DetailAsync(id,ctx.Actor(),ct)).Permissions));
        var matrix = g.MapGroup("/applications/{id:guid}/permission-matrix");
        matrix.MapGet("", async (Guid id, Guid projectId, int? page, int? pageSize, HttpContext ctx, PermissionMatrixService service, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ReadAsync(id, projectId, page ?? 1, pageSize ?? 50, ctx.Actor(), ct));
        });
        matrix.MapPost("/preview", async (Guid id, MatrixPreviewRequest request, HttpContext ctx, PermissionMatrixService service, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var actor = ctx.Actor();
            return Results.Ok(await service.PreviewAsync(id, request, actor, CredentialGovernanceEndpoints.RequireCurrentSession(ctx.User, actor), ct));
        });
        matrix.MapPost("/commit", async (Guid id, MatrixCommitRequest request, HttpContext ctx, CommandRequestContext command, PermissionMatrixService service, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var actor = ctx.Actor();
            return Results.Ok(await service.CommitAsync(id, request, command, actor, CredentialGovernanceEndpoints.RequireCurrentSession(ctx.User, actor), ct));
        });
        g.MapGet("/applications/{id:guid}/route-method-options", async (Guid id, Guid environmentId, Guid apiId,
            HttpContext ctx, ApplicationService service, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ReadRouteMethodOptionsAsync(id, environmentId, apiId, ctx.Actor(), ct));
        });
        g.MapPost("/applications/{id:guid}/permissions", async (Guid id, PermissionSaveRequest request, HttpContext ctx,
            ApplicationService service, CancellationToken ct) =>
        {
            var actor = ctx.Actor();
            return GovernanceEndpoints.Command(ctx, await service.SavePermissionAsync(id, null, request, null, actor, ct,
                CredentialGovernanceEndpoints.RequireCurrentSession(ctx.User, actor)));
        });
        g.MapPut("/applications/{appId:guid}/permissions/{id:guid}", async (Guid appId, Guid id, PermissionSaveRequest request,
            HttpContext ctx, ApplicationService service, CancellationToken ct) =>
        {
            var actor = ctx.Actor();
            return GovernanceEndpoints.Command(ctx, await service.SavePermissionAsync(appId, id, request, ctx.Request.Headers.IfMatch, actor, ct,
                CredentialGovernanceEndpoints.RequireCurrentSession(ctx.User, actor)));
        });
        g.MapDelete("/application-permissions/{id:guid}",async(Guid id,HttpContext ctx,ApplicationService service,CancellationToken ct)=>{var actor=ctx.Actor();await service.DeletePermissionAsync(id,ctx.Request.Headers.IfMatch,actor,ct,CredentialGovernanceEndpoints.RequireCurrentSession(ctx.User,actor));return Results.NoContent();});
    }
}

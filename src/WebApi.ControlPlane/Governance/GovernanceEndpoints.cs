using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Governance;
namespace WebApi.ControlPlane.Governance;
public static class GovernanceEndpoints
{
    public static IResult Command<T>(HttpContext ctx,CommandResult<T> value) {ctx.Response.Headers.ETag=value.ETag;return Results.Ok(value.Value);}
    public static void MapGovernance(this WebApplication app)
    {
        var g=app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        g.MapGet("/scope-tree",async(HttpContext ctx,GovernanceService service,CancellationToken ct)=>Results.Ok(await service.GetScopeTreeAsync(ctx.Actor(),ct)));
        g.MapGet("/organizations",async(HttpContext ctx,GovernanceService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(Pagination.Slice((await service.GetScopeTreeAsync(ctx.Actor(),ct)).Organizations,page,pageSize)));
        g.MapGet("/organizations/{id:guid}",async(Guid id,HttpContext ctx,GovernanceService service,CancellationToken ct)=>{var value=await service.OrganizationAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPost("/organizations",async(SaveResourceRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.CreateOrganizationAsync(request,ctx.Actor(),ct)));
        g.MapPut("/organizations/{id:guid}",async(Guid id,SaveResourceRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.SaveOrganizationAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapGet("/organizations/{id:guid}/projects",async(Guid id,HttpContext ctx,GovernanceService service,int? page,int? pageSize,CancellationToken ct)=>{var list=(await service.GetScopeTreeAsync(ctx.Actor(),ct)).Projects.Where(x=>x.OrganizationId==id).ToArray();return Results.Ok(Pagination.Slice(list,page,pageSize));});
        g.MapGet("/projects/{id:guid}",async(Guid id,HttpContext ctx,GovernanceService service,CancellationToken ct)=>{var value=(await service.GetScopeTreeAsync(ctx.Actor(),ct)).Projects.SingleOrDefault(x=>x.Id==id)??throw ScopeResolver.Missing();ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPost("/organizations/{id:guid}/projects",async(Guid id,SaveResourceRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.CreateProjectAsync(id,request,ctx.Actor(),ct)));
        g.MapPut("/projects/{id:guid}",async(Guid id,SaveResourceRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.SaveProjectAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapGet("/projects/{id:guid}/environments",async(Guid id,HttpContext ctx,GovernanceService service,int? page,int? pageSize,CancellationToken ct)=>{var list=(await service.GetScopeTreeAsync(ctx.Actor(),ct)).Environments.Where(x=>x.ProjectId==id).ToArray();return Results.Ok(Pagination.Slice(list,page,pageSize));});
        g.MapGet("/environments/{id:guid}",async(Guid id,HttpContext ctx,GovernanceService service,CancellationToken ct)=>{var value=(await service.GetScopeTreeAsync(ctx.Actor(),ct)).Environments.SingleOrDefault(x=>x.Id==id)??throw ScopeResolver.Missing();ctx.Response.Headers.ETag=RevisionTag.Format(value.Revision);return Results.Ok(value);});
        g.MapPost("/projects/{id:guid}/environments",async(Guid id,CreateEnvironmentRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.CreateEnvironmentAsync(id,request,ctx.Actor(),ct)));
        g.MapPut("/environments/{id:guid}",async(Guid id,UpdateEnvironmentRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.SaveEnvironmentAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapGet("/users",async(HttpContext ctx,GovernanceService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.UsersAsync(ctx.Actor(),page??1,pageSize??50,ct)));
        g.MapPost("/users",async(CreateUserRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.CreateUserAsync(request,ctx.Actor(),ct)));
        g.MapPut("/users/{id:guid}",async(Guid id,UpdateUserRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.SaveUserAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapPut("/users/{id:guid}/roles",async(Guid id,AssignRolesRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.AssignRolesAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapGet("/users/{id:guid}/scopes",async(Guid id,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Results.Ok(await service.UserScopesAsync(id,ctx.Actor(),ct)));
        g.MapPut("/users/{id:guid}/scopes",async(Guid id,SaveScopesRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.SaveScopesAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapGet("/roles",async(HttpContext ctx,GovernanceService service,CancellationToken ct)=>Results.Ok(await service.RolesAsync(ctx.Actor(),ct)));
        g.MapPost("/roles",async(SaveRoleRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.CreateRoleAsync(request,ctx.Actor(),ct)));
        g.MapPut("/roles/{id:guid}",async(Guid id,SaveRoleRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.SaveRoleAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapDelete("/roles/{id:guid}",async(Guid id,HttpContext ctx,GovernanceService service,CancellationToken ct)=>{await service.DeleteRoleAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
        g.MapGet("/permissions",async(HttpContext ctx,GovernanceService service,CancellationToken ct)=>Results.Ok(await service.PermissionDictionaryAsync(ctx.Actor(),ct)));
        g.MapPut("/roles/{id:guid}/permissions",async(Guid id,AssignPermissionsRequest request,HttpContext ctx,GovernanceService service,CancellationToken ct)=>Command(ctx,await service.PermissionsAsync(id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct)));
        g.MapGet("/audit-logs/export",async(HttpContext ctx,AuditExportService service,string? traceId,string? resourceId,CancellationToken ct)=>{var result=await service.ExportAsync(ctx.Actor(),traceId,resourceId,ct);ctx.Response.Headers.CacheControl="no-store";ctx.Response.Headers["X-Export-Row-Count"]=result.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture);ctx.Response.Headers["X-Export-Truncated"]=result.Truncated?"true":"false";return Results.File(result.Bytes,"text/csv; charset=utf-8","audit-log-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+".csv");});
        g.MapGet("/audit-logs",async(HttpContext ctx,GovernanceService service,int? page,int? pageSize,string? traceId,string? resourceId,CancellationToken ct)=>Results.Ok(await service.AuditAsync(ctx.Actor(),page??1,pageSize??50,ct,traceId,resourceId)));
    }
}

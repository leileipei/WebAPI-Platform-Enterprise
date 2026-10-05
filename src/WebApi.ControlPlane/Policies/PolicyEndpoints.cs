using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.ControlPlane.Governance;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Policies;
namespace WebApi.ControlPlane.Policies;
public static class PolicyEndpoints
{
    public static void MapPolicies(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        group.MapGet("/organizations/{id:guid}/policies",async(Guid id,HttpContext ctx,PolicyService service,int? page,int? pageSize,string? type,string? search,bool? enabled,CancellationToken ct)=>Results.Ok(await service.ListAsync(new(id),ctx.Actor(),page??1,pageSize??50,type,search,enabled,ct)));
        group.MapPost("/organizations/{id:guid}/policies",async(Guid id,SavePolicyRequest request,HttpContext ctx,PolicyService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.SaveAsync(new(id),null,request,null,ctx.Actor(),ct)));
        group.MapGet("/projects/{id:guid}/policies",async(Guid id,HttpContext ctx,PolicyService service,ScopeResolver scopes,int? page,int? pageSize,string? type,string? search,bool? enabled,CancellationToken ct)=>{var scope=await scopes.ProjectAsync(id,ct);return Results.Ok(await service.ListAsync(new(scope.OrganizationId,id),ctx.Actor(),page??1,pageSize??50,type,search,enabled,ct));});
        group.MapPost("/projects/{id:guid}/policies",async(Guid id,SavePolicyRequest request,HttpContext ctx,PolicyService service,ScopeResolver scopes,CancellationToken ct)=>{var scope=await scopes.ProjectAsync(id,ct);return GovernanceEndpoints.Command(ctx,await service.SaveAsync(new(scope.OrganizationId,id),null,request,null,ctx.Actor(),ct));});
        group.MapGet("/policies/{id:guid}",async(Guid id,HttpContext ctx,PolicyService service,CancellationToken ct)=>{var p=await service.GetAsync(id,ctx.Actor(),ct);ctx.Response.Headers.ETag=RevisionTag.Format(p.VersionNo);return Results.Ok(p);});
        group.MapPut("/policies/{id:guid}",async(Guid id,SavePolicyRequest request,HttpContext ctx,PolicyService service,CancellationToken ct)=>{var p=await service.GetAsync(id,ctx.Actor(),ct);return GovernanceEndpoints.Command(ctx,await service.SaveAsync(new(p.OrganizationId,p.ProjectId),id,request,ctx.Request.Headers.IfMatch,ctx.Actor(),ct));});
        group.MapDelete("/policies/{id:guid}",async(Guid id,HttpContext ctx,PolicyService service,CancellationToken ct)=>{await service.DeleteAsync(id,ctx.Request.Headers.IfMatch,ctx.Actor(),ct);return Results.NoContent();});
        group.MapPost("/policies/{id:guid}/copy",async(Guid id,CopyPolicyRequest request,HttpContext ctx,PolicyService service,CancellationToken ct)=>GovernanceEndpoints.Command(ctx,await service.CopyAsync(id,request,ctx.Actor(),ct)));
        group.MapPost("/policies/validate",async(ValidatePolicyRequest request,HttpContext ctx,PolicyService service,CancellationToken ct)=>Results.Ok(await service.ValidateAsync(request,ctx.Actor(),ct)));
        group.MapGet("/policies/{id:guid}/references",async(Guid id,HttpContext ctx,PolicyReferenceService service,int? page,int? pageSize,CancellationToken ct)=>Results.Ok(await service.ListAsync(id,ctx.Actor(),page??1,pageSize??50,ct)));
    }
}

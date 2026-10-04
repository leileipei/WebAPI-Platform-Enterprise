using WebApi.Contracts.Alerts;
using WebApi.ControlPlane.Observability;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Alerts;
namespace WebApi.ControlPlane.Alerts;
public static class AlertRuleEndpoints
{
    public static void MapAlertRules(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/observability/alert-rules").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        group.MapGet("/",async([AsParameters]MetricQueryParameters query,HttpContext ctx,AlertRuleService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.ListAsync(ctx.Actor(),query.Scope,query.Page??1,query.PageSize??50,ct));});
        group.MapGet("/{id:guid}",async(Guid id,HttpContext ctx,AlertRuleService service,CancellationToken ct)=>Reply(ctx,await service.DetailAsync(ctx.Actor(),id,ct)));
        group.MapPost("/",async(SaveAlertRuleRequest request,HttpContext ctx,AlertRuleService service,CancellationToken ct)=>Reply(ctx,await service.CreateAsync(ctx.Actor(),request,ct)));
        group.MapPut("/{id:guid}",async(Guid id,SaveAlertRuleRequest request,HttpContext ctx,AlertRuleService service,CancellationToken ct)=>Reply(ctx,await service.UpdateAsync(ctx.Actor(),id,request,ctx.Request.Headers.IfMatch.ToString(),ct)));
        group.MapPost("/{id:guid}/enable",async(Guid id,HttpContext ctx,AlertRuleService service,CancellationToken ct)=>Reply(ctx,await service.SetEnabledAsync(ctx.Actor(),id,true,ctx.Request.Headers.IfMatch.ToString(),ct)));
        group.MapPost("/{id:guid}/disable",async(Guid id,HttpContext ctx,AlertRuleService service,CancellationToken ct)=>Reply(ctx,await service.SetEnabledAsync(ctx.Actor(),id,false,ctx.Request.Headers.IfMatch.ToString(),ct)));
        group.MapPost("/preview",async(SaveAlertRuleRequest request,HttpContext ctx,AlertRuleService service,CancellationToken ct)=>Results.Ok(await service.PreviewAsync(ctx.Actor(),request,ct)));
        group.MapPost("/test",async(SaveAlertRuleRequest request,HttpContext ctx,AlertRuleService service,CancellationToken ct)=>Results.Ok(await service.TestAsync(ctx.Actor(),request,ct)));
    }
    private static IResult Reply(HttpContext ctx,AlertRuleDto rule){ctx.Response.Headers.ETag=$"\"{rule.Revision}\"";ctx.Response.Headers.CacheControl="no-store";return Results.Ok(rule);}
}

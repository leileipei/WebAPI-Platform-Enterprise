using WebApi.Contracts.Alerts;
using WebApi.ControlPlane.Observability;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Alerts;
namespace WebApi.ControlPlane.Alerts;
public static class AlertEventEndpoints
{
    public static void MapAlertEvents(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/observability/alerts").RequireAuthorization().AddEndpointFilter<RequestValidationFilter>();
        group.MapGet("/",async([AsParameters]MetricQueryParameters query,string? severity,string? source,string? status,HttpContext ctx,AlertEventService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.ListAsync(ctx.Actor(),query.Scope,new(severity,source,status,query.Page??1,query.PageSize??50),ct));});
        group.MapGet("/{id:guid}",async(Guid id,HttpContext ctx,AlertEventService service,CancellationToken ct)=>Reply(ctx,await service.DetailAsync(ctx.Actor(),id,ct)));
        foreach(var kind in new[]{"Ack","Resolve","Silence","Unsilence"})
        {
            var expected=kind;group.MapPost("/{id:guid}/"+kind.ToLowerInvariant(),async(Guid id,AlertAction action,HttpContext ctx,AlertEventService service,CancellationToken ct)=>{
                if(action.Kind!=expected)throw new WebApi.Contracts.Common.ApiException(422,"mismatched_alert_action","事件命令与路径不一致。");return Reply(ctx,await service.ActAsync(ctx.Actor(),id,action,ctx.Request.Headers.IfMatch.ToString(),ct));});
        }
    }
    private static IResult Reply(HttpContext ctx,AlertEventDto e){ctx.Response.Headers.ETag=$"\"{e.Revision}\"";ctx.Response.Headers.CacheControl="no-store";return Results.Ok(e);}
}

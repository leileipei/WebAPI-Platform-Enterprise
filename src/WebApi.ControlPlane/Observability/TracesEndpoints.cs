using WebApi.Contracts.Observability;using WebApi.ControlPlane.Security;using WebApi.Infrastructure.Observability;
namespace WebApi.ControlPlane.Observability;
public sealed class TraceQueryParameters
{
    public Guid OrganizationId{get;set;}public Guid ProjectId{get;set;}public Guid? EnvironmentId{get;set;}public bool? AllAccessibleEnvironments{get;set;}
    public DateTimeOffset? Start{get;set;}public DateTimeOffset? End{get;set;}[Microsoft.AspNetCore.Mvc.FromQuery]public string? TraceId{get;set;}public Guid? ApiId{get;set;}public double? MinDurationMs{get;set;}public string? Outcome{get;set;}public int? Limit{get;set;}public string? Cursor{get;set;}
    public ObservationScopeRequest Scope=>new(OrganizationId,ProjectId,EnvironmentId,AllAccessibleEnvironments??false);
    public TimeRange Range{get{var end=End??DateTimeOffset.UtcNow;return new(Start??end.AddHours(-1),end);}}
    public TraceFilter Filter=>new(TraceId,ApiId,MinDurationMs,Outcome,Limit??50,Cursor);
}
public static class TracesEndpoints
{
    public static void MapObservationTraces(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/observability").RequireAuthorization();
        group.MapGet("/traces",async([AsParameters]TraceQueryParameters q,HttpContext ctx,TraceQueryService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.SearchAsync(ctx.Actor(),q.Scope,q.Range,q.Filter,ct));});
        group.MapGet("/traces/{traceId}",async(string traceId,[AsParameters]TraceQueryParameters q,HttpContext ctx,TraceQueryService service,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.DetailAsync(ctx.Actor(),q.Scope,traceId,q.Range,ct));});
    }
}

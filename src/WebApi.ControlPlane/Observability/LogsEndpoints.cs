using WebApi.Contracts.Common;using WebApi.Contracts.Observability;using WebApi.ControlPlane.Security;using WebApi.Infrastructure.Observability;
namespace WebApi.ControlPlane.Observability;
public sealed class LogQueryParameters
{
    public Guid OrganizationId{get;set;}public Guid ProjectId{get;set;}public Guid? EnvironmentId{get;set;}public bool? AllAccessibleEnvironments{get;set;}
    public DateTimeOffset? Start{get;set;}public DateTimeOffset? End{get;set;}public Guid? ApiId{get;set;}public Guid? ApplicationId{get;set;}public Guid? DestinationId{get;set;}public string? Status{get;set;}
    public double? MinDurationMs{get;set;}public double? MaxDurationMs{get;set;}public string? Keyword{get;set;}public string? TraceId{get;set;}public int? Limit{get;set;}public string? Cursor{get;set;}
    public Guid? PolicyId{get;set;}public string? PolicyDecision{get;set;}
    public ObservationScopeRequest Scope=>new(OrganizationId,ProjectId,EnvironmentId,AllAccessibleEnvironments??false);
    public TimeRange Range{get{var end=End??DateTimeOffset.UtcNow;return new(Start??end.AddHours(-1),end);}}
    public LogFilter Filter=>new(ApiId,ApplicationId,Status,MinDurationMs,MaxDurationMs,null,Keyword,TraceId,Limit??50,Cursor,DestinationId,PolicyId,PolicyDecision);
}
public sealed record LogQueryBody(ObservationScopeRequest Scope,TimeRange Range,LogFilter Filter);
public static class LogsEndpoints
{
    public static void MapObservationLogs(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/observability").RequireAuthorization();
        group.MapGet("/logs",async([AsParameters]LogQueryParameters q,HttpContext ctx,AccessLogQueryService service,CancellationToken ct)=>{RejectUrlIp(ctx);ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.QueryAsync(ctx.Actor(),q.Scope,q.Range,q.Filter,ct));});
        group.MapPost("/logs/query",async(LogQueryBody q,HttpContext ctx,AccessLogQueryService service,CancellationToken ct)=>{RejectUrlIp(ctx);ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.QueryAsync(ctx.Actor(),q.Scope,q.Range,q.Filter,ct));});
        group.MapGet("/logs/{logId:guid}",async(Guid logId,[AsParameters]LogQueryParameters q,HttpContext ctx,AccessLogQueryService service,CancellationToken ct)=>{RejectUrlIp(ctx);ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.DetailAsync(ctx.Actor(),q.Scope,q.Range,logId,ct));});
        group.MapGet("/logs/export",async([AsParameters]LogQueryParameters q,HttpContext ctx,AccessLogQueryService service,CancellationToken ct)=>await Export(q.Scope,q.Range,q.Filter,ctx,service,ct));
        group.MapPost("/logs/export",async(LogQueryBody q,HttpContext ctx,AccessLogQueryService service,CancellationToken ct)=>await Export(q.Scope,q.Range,q.Filter,ctx,service,ct));
    }
    private static void RejectUrlIp(HttpContext ctx){if(ctx.Request.Query.ContainsKey("ip"))throw new ApiException(422,"invalid_observation_query","IP筛选请通过请求体提交，不能放入URL。");}
    private static async Task<IResult> Export(ObservationScopeRequest scope,TimeRange range,LogFilter filter,HttpContext ctx,AccessLogQueryService service,CancellationToken ct)
    {RejectUrlIp(ctx);using var buffer=new MemoryStream();var result=await service.ExportAsync(ctx.Actor(),scope,range,filter,buffer,ct);ctx.Response.Headers.CacheControl="no-store";ctx.Response.Headers["X-WebApi-Source-State"]=result.SourceState.ToString();ctx.Response.Headers["X-WebApi-Export-Rows"]=result.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture);ctx.Response.Headers["X-WebApi-Export-Truncated"]=result.Truncated?"true":"false";return Results.File(buffer.ToArray(),"text/csv; charset=utf-8","access-logs.csv");}
}

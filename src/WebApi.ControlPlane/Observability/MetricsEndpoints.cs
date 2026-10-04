using WebApi.Contracts.Common;
using Microsoft.AspNetCore.Mvc;
using WebApi.Contracts.Observability;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Observability;
namespace WebApi.ControlPlane.Observability;
public sealed class MetricQueryParameters
{
    public Guid OrganizationId {get;set;}
    public Guid ProjectId {get;set;}
    public Guid? EnvironmentId {get;set;}
    public bool? AllAccessibleEnvironments {get;set;}
    [FromQuery] public Guid? ApiId {get;set;}
    public Guid? ApplicationId {get;set;}
    public Guid? DestinationId {get;set;}
    public DateTimeOffset? Start {get;set;}
    public DateTimeOffset? End {get;set;}
    public string? GroupBy {get;set;}
    public string? SortBy {get;set;}
    public int? Page {get;set;}
    public int? PageSize {get;set;}
    public ObservationScopeRequest Scope=>new(OrganizationId,ProjectId,EnvironmentId,AllAccessibleEnvironments??false);
    public TimeRange Range{get{var end=End??DateTimeOffset.UtcNow;return new(Start??end.AddHours(-1),end);}}
    public MetricFilter Filter(Guid? api=null)=>new(api??ApiId,ApplicationId,DestinationId,GroupBy??"None",Page??1,PageSize??50,SortBy??"request_count");
}
public static class MetricsEndpoints
{
    public static void MapObservationMetrics(this WebApplication app)
    {
        var group=app.MapGroup("/api/v1/observability").RequireAuthorization();
        group.MapGet("/metrics",async([AsParameters]MetricQueryParameters query,HttpContext ctx,ObservationQueryService service,CancellationToken ct)=>{
            ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.MetricsAsync(ctx.Actor(),query.Scope,query.Range,query.Filter(),ct));
        });
        group.MapGet("/apis/{apiId:guid}/metrics",async(Guid apiId,[AsParameters]MetricQueryParameters query,HttpContext ctx,ObservationQueryService service,CancellationToken ct)=>{
            if(query.ApiId is Guid selected&&selected!=apiId)throw new ApiException(422,"invalid_observation_query","API筛选与路径不一致。");
            ctx.Response.Headers.CacheControl="no-store";return Results.Ok(await service.MetricsAsync(ctx.Actor(),query.Scope,query.Range,query.Filter(apiId),ct));
        });
    }
}

using System.Text.Json;
using WebApi.Contracts.Comparisons;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Comparisons;
using WebApi.Infrastructure.Comparisons.Rules;
namespace WebApi.ControlPlane.Comparisons;
public static class ComparisonEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web){MaxDepth=128};
    public static void MapComparisons(this WebApplication app)
    {
        var api=app.MapGroup("/api/v1").RequireAuthorization();api.AddEndpointFilter(async(context,next)=>{context.HttpContext.Response.Headers.CacheControl="no-store";return await next(context);});
        api.MapGet("/comparisons/rules",()=>Results.Json(new{engineVersion=ContractComparisonEngine.EngineVersion,adapterVersion="oas-http-model-v2",limits=new WebApi.Contracts.OpenApi.ContractLimits(),rules=CompatibilityRuleCatalog.All.Concat(OpenApiRuleCatalog.All)},JsonOptions));
        api.MapPost("/apis/{id:guid}/version-comparisons",async(Guid id,CreateVersionComparisonRequest request,HttpContext ctx,VersionComparisonService service,CancellationToken ct)=>Results.Json(await service.CreateAsync(id,request,ctx.Actor(),ct),JsonOptions));
        api.MapGet("/apis/{id:guid}/version-comparisons",async(Guid id,string? cursor,int? limit,Guid? reviewId,HttpContext ctx,VersionComparisonService service,CancellationToken ct)=>Results.Json(await service.ListAsync(id,cursor,limit,reviewId,ctx.Actor(),ct),JsonOptions));
        api.MapGet("/version-comparisons/{id:guid}",async(Guid id,HttpContext ctx,VersionComparisonService service,CancellationToken ct)=>Results.Json(await service.GetAsync(id,ctx.Actor(),ct),JsonOptions));
        api.MapGet("/version-comparisons/{id:guid}/export",async(Guid id,string? format,HttpContext ctx,VersionComparisonService service,CancellationToken ct)=>{var value=await service.ExportAsync(id,format??"json",ctx.Actor(),ct);return Results.File(value.Bytes,value.ContentType,value.FileName);});
        api.MapPost("/version-comparisons/{id:guid}/reviews",async(Guid id,CreateRiskReviewRequest request,HttpContext ctx,VersionRiskReviewService service,CancellationToken ct)=>Results.Json(await service.CreateAsync(id,request,ctx.Actor(),ct),JsonOptions));
        api.MapGet("/version-comparisons/{id:guid}/reviews",async(Guid id,string? cursor,int? limit,HttpContext ctx,VersionRiskReviewService service,CancellationToken ct)=>Results.Json(await service.ListAsync(id,cursor,limit,ctx.Actor(),ct),JsonOptions));
    }
}

using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Delivery;
namespace WebApi.ControlPlane.Releases;

public static class VerificationReportEndpoints
{
    public static void MapVerificationReports(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/verification-reports").RequireAuthorization();
        group.MapPost("", async (Guid? artifactId, Guid? promotionId, HttpContext context, VerificationReportStore reports, CancellationToken ct) =>
        {
            var limit = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = VerificationReportStore.MaximumBytes + 65536;
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Headers.ContentEncoding.Count > 0) throw new WebApi.Contracts.Common.ApiException(422, "invalid_report_encoding", "报告上传不支持压缩内容编码。");
            var value = await reports.StoreAsync(context.Request.Body, context.Request.ContentType ?? "", artifactId, promotionId, context.Actor(), ct);
            return Results.Ok(value);
        });
        group.MapGet("/{id:guid}/download", async (Guid id, HttpContext context, VerificationReportStore reports, CancellationToken ct) =>
        {
            var metadata = await reports.GetMetadataAsync(id, context.Actor(), ct);
            var stream = await reports.OpenAsync(id, context.Actor(), ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(stream, metadata.ContentType, $"verification-report-{id:N}" + (metadata.ContentType == "application/pdf" ? ".pdf" : ".txt"), enableRangeProcessing: false);
        });
    }
}

using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.ControlPlane.Security;
using WebApi.Infrastructure.Releases;

namespace WebApi.ControlPlane.Releases;

public static class ApprovalInboxEndpoints
{
    public static void MapApprovalInbox(this WebApplication app) =>
        app.MapGet("/api/v1/approvals", async (HttpContext ctx, ApprovalInboxService service, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ListAsync(Parse(ctx.Request.Query), ctx.Actor(), ct));
        }).RequireAuthorization();

    private static ApprovalInboxFilter Parse(IQueryCollection query)
    {
        string? Value(string key)
        {
            if (!query.TryGetValue(key, out var value)) return null;
            if (value.Count != 1 || string.IsNullOrWhiteSpace(value[0])) throw Invalid();
            return value[0];
        }
        Guid? Id(string key) { var value = Value(key); if (value is null) return null; return Guid.TryParse(value, out var id) ? id : throw Invalid(); }
        int Number(string key, int fallback) { var value = Value(key); return value is null ? fallback : int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : throw Invalid(); }
        return new(Value("view") ?? "PendingMine", Id("organizationId"), Id("projectId"), Id("environmentId"), Value("status"), Number("page", 1), Number("pageSize", 50));
    }
    private static ApiException Invalid() => new(422, "invalid_approval_filter", "审批筛选或分页参数无效。");
}

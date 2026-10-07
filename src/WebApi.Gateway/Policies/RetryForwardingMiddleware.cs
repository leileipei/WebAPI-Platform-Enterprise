using Microsoft.AspNetCore.Http.Features;
namespace WebApi.Gateway.Policies;
public sealed class RetryForwardingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RetryCoordinator coordinator)
    {
        var execution = TrafficExecutionContext.From(context) ?? throw new InvalidOperationException("Authorized traffic context is unavailable.");
        var binding = (execution.Route.PolicyBindings ?? []).SingleOrDefault(b => execution.Generation.Retry.ContainsKey(b.PolicyId));
        if (binding is null || !Eligible(context)) { await next(context); return; }
        var config = execution.Generation.Retry[binding.PolicyId];
        if (config.MaxAttempts == 1) { await next(context); return; }
        await coordinator.ExecuteAsync(context, execution, config, context.RequestAborted);
    }
    internal static bool Eligible(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != false) return false;
        if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding") || context.Response.HasStarted) return false;
        if (request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true) return false;
        foreach (var name in new[] { "Upgrade", "Range", "If-Match", "If-None-Match", "If-Modified-Since", "If-Unmodified-Since", "If-Range" })
            if (request.Headers.ContainsKey(name)) return false;
        return !request.Headers.Connection.ToString().Split(',').Any(p => p.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase));
    }
}

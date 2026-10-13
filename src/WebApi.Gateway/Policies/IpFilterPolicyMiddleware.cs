using System.Diagnostics;
using WebApi.Gateway.Observability;

namespace WebApi.Gateway.Policies;

public sealed class IpFilterPolicyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var execution = TrafficExecutionContext.From(context) ?? throw new InvalidOperationException("Routing policy context unavailable.");
        foreach (var binding in execution.Route.PolicyBindings ?? [])
        {
            if (!execution.Generation.CompiledIpFilters.TryGetValue(binding.PolicyId, out var filter)) continue;
            var policy = execution.Generation.PoliciesById[binding.PolicyId];
            var result = filter.Evaluate(context.Connection.RemoteIpAddress);
            execution.Record(policy, result.Allowed ? "Allowed" : "Rejected", result.Reason);
            if (!result.Allowed)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { code = "ip_filter_rejected", reason = result.Reason,
                    traceId = context.TraceIdentifier, w3cTraceId = RequestTelemetryState.From(context)?.Context.TraceId ?? Activity.Current?.TraceId.ToHexString() });
                return;
            }
        }
        await next(context);
    }
}

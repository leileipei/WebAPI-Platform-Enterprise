using System.Globalization;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Security;
using Yarp.ReverseProxy.Model;

namespace WebApi.Gateway.Policies;

public sealed class RoutingGenerationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RuntimeGenerationStore store)
    {
        var metadata = context.GetReverseProxyFeature().Route.Config.Metadata!;
        using var lease = store.Acquire(long.Parse(metadata["deploymentSequence"], CultureInfo.InvariantCulture));
        var generation = lease.Generation;
        var route = generation.Indexes.Routes[Guid.Parse(metadata["runtimeRouteId"])];
        context.Items[ApiKeyMiddleware.GenerationItem] = generation;
        TrafficExecutionContext.Attach(context, generation, route);
        if (context.Items.TryGetValue(AdmissionGate.Item, out var ticket)) ((AdmissionGate.Ticket)ticket!).Dispose();
        var telemetry = RequestTelemetryState.From(context);
        if (telemetry is not null)
        {
            var cluster = generation.Indexes.Clusters[route.ClusterId];
            var auth = generation.Indexes.RouteAuthentication[route.Id].Configuration;
            var mode = auth?.Mode ?? (route.RequireApiKey ? AuthenticationMode.ApiKey : AuthenticationMode.Anonymous);
            telemetry.Context = telemetry.Context with
            {
                ApiId = route.ApiId, ApiVersionId = route.ApiVersionId, RouteId = route.Id,
                RuntimeClusterId = cluster.Id, ClusterId = cluster.SourceId ?? cluster.Id,
                ConfigVersion = generation.Envelope.ConfigVersion, DeploymentSequence = generation.Envelope.DeploymentSequence,
                PathTemplate = context.RequestServices.GetRequiredService<TelemetrySanitizer>().Path(route.Path),
                ApplicationKey = mode == AuthenticationMode.Anonymous ? "Anonymous" : "Unknown"
            };
        }
        try { await next(context); }
        finally { context.Items.Remove(ApiKeyMiddleware.GenerationItem); }
    }
}

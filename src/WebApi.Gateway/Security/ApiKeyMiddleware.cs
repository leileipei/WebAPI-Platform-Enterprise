using System.Diagnostics;
using System.Globalization;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Policies;
using WebApi.Infrastructure.Security;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Security;

public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    public static readonly object GenerationItem = new();
    public async Task InvokeAsync(HttpContext ctx, RuntimeGenerationStore store, JwtTokenVerifier verifier)
    {
        var feature = ctx.GetReverseProxyFeature();
        var metadata = feature.Route.Config.Metadata!;
        using var lease = store.Acquire(long.Parse(metadata["deploymentSequence"], CultureInfo.InvariantCulture));
        var generation = lease.Generation;
        ctx.Items[GenerationItem] = generation;
        // Release admission after capturing the complete route/auth generation, before I/O.
        if (ctx.Items.TryGetValue(AdmissionGate.Item, out var ticket)) ((AdmissionGate.Ticket)ticket!).Dispose();
        try
        {
            var route = generation.Snapshot.Routes.Single(r => r.Id == Guid.Parse(metadata["runtimeRouteId"]));
            var execution = TrafficExecutionContext.Attach(ctx, generation, route);
            var authentication = (route.PolicyBindings ?? []).Where(b => generation.Authentication.ContainsKey(b.PolicyId))
                .Select(b => generation.Authentication[b.PolicyId]).SingleOrDefault();
            var mode = authentication?.Mode ?? (route.RequireApiKey ? AuthenticationMode.ApiKey : AuthenticationMode.Anonymous);
            var telemetry = RequestTelemetryState.From(ctx);
            if (telemetry is not null)
            {
                var cluster = generation.Snapshot.Clusters.Single(c => c.Id == route.ClusterId);
                telemetry.Context = telemetry.Context with
                {
                    ApiId = route.ApiId, ApiVersionId = route.ApiVersionId, RouteId = route.Id,
                    RuntimeClusterId = cluster.Id, ClusterId = cluster.SourceId ?? cluster.Id,
                    ConfigVersion = generation.Envelope.ConfigVersion, DeploymentSequence = generation.Envelope.DeploymentSequence,
                    PathTemplate = ctx.RequestServices.GetRequiredService<TelemetrySanitizer>().Path(route.Path),
                    ApplicationKey = mode == AuthenticationMode.Anonymous ? "Anonymous" : "Unknown"
                };
            }
            var raw = ctx.Request.Headers["X-API-Key"].ToString();
            ctx.Request.Headers.Remove("X-API-Key");
            if (mode == AuthenticationMode.JWT)
                foreach (var name in ctx.Request.Headers.Keys.Where(k => k.StartsWith("X-WebApi-", StringComparison.OrdinalIgnoreCase)).ToArray())
                    ctx.Request.Headers.Remove(name);
            ctx.Request.Headers["X-WebApi-Deployment-Sequence"] = generation.Envelope.DeploymentSequence.ToString(CultureInfo.InvariantCulture);
            ctx.Request.Headers["X-WebApi-Trace-Id"] = ctx.TraceIdentifier;
            var now = DateTimeOffset.UtcNow;
            RuntimeApplication? app = null;
            JwtVerifiedClaims? claims = null;
            if (mode == AuthenticationMode.JWT)
            {
                var authorization = ctx.Request.Headers.Authorization;
                var header = authorization.Count == 1 ? authorization[0] : null;
                var jwt = authentication?.Jwt;
                var result = header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && jwt is not null
                    ? verifier.Verify(header[7..], jwt, now) : null;
                if (result?.Success != true || result.Claims is null)
                { await Reject(ctx, telemetry, 401, "invalid_jwt", true); return; }
                claims = result.Claims;
                var mapping = jwt!.ApplicationMappings.SingleOrDefault(m => string.Equals(m.ClaimValue, claims.ApplicationClaimValue, StringComparison.Ordinal));
                if (mapping is null) { await Reject(ctx, telemetry, 401, "invalid_jwt", true); return; }
                app = generation.Snapshot.Applications.SingleOrDefault(a => a.Id == mapping.ApplicationId);
                if (app?.Status != "Active") { await Reject(ctx, telemetry, 403, "api_not_granted"); return; }
                if (jwt.ForwardBearer) ctx.Request.Headers.Authorization = "Bearer " + claims.ValidatedBearer;
                else ctx.Request.Headers.Remove("Authorization");
            }
            else if (mode == AuthenticationMode.ApiKey)
            {
                var parts = raw.Split('.');
                app = parts.Length == 2 ? generation.Snapshot.Applications.SingleOrDefault(a => a.Credentials.Any(k => k.AccessKey == parts[0])) : null;
                var key = app?.Credentials.Single(k => k.AccessKey == parts[0]);
                if (app?.Status != "Active" || key?.Status != "Active" || now < key.ValidFrom || now >= key.ExpiresAt || !ApiKeySecret.Verify(key.Hash, parts[1]))
                { await Reject(ctx, telemetry, 401, "invalid_api_key"); return; }
            }
            if (app is not null)
            {
                if (telemetry is not null) telemetry.Context = telemetry.Context with { ApplicationKey = app.Id.ToString() };
                if (!app.Permissions.Any(p => p.ApiId == route.ApiId && now >= p.ValidFrom && (p.ExpiresAt is null || now < p.ExpiresAt)))
                { await Reject(ctx, telemetry, 403, "api_not_granted"); return; }
                execution.ApplicationId = app.Id;
            }
            execution.VerifiedIdentity = new(mode, app?.Id, claims);
            await next(ctx);
        }
        finally { ctx.Items.Remove(GenerationItem); }
    }
    private static async Task Reject(HttpContext ctx, RequestTelemetryState? telemetry, int status, string code, bool bearer = false)
    {
        ctx.Response.StatusCode = status;
        if (bearer) ctx.Response.Headers.WWWAuthenticate = "Bearer";
        await ctx.Response.WriteAsJsonAsync(new { code, traceId = ctx.TraceIdentifier, w3cTraceId = telemetry?.Context.TraceId ?? Activity.Current?.TraceId.ToHexString() });
    }
}

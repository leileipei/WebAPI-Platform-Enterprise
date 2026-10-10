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
        var indexes = generation.Indexes;
        ctx.Items[GenerationItem] = generation;
        // Release admission after capturing the complete route/auth generation, before I/O.
        if (ctx.Items.TryGetValue(AdmissionGate.Item, out var ticket)) ((AdmissionGate.Ticket)ticket!).Dispose();
        try
        {
            var route = indexes.Routes[Guid.Parse(metadata["runtimeRouteId"])];
            var execution = TrafficExecutionContext.Attach(ctx, generation, route);
            var routeAuthentication = indexes.RouteAuthentication[route.Id];
            var authentication = routeAuthentication.Configuration;
            var authPolicy = routeAuthentication.Policy;
            void AuthDecision(string decision,string? reason=null){if(authentication?.Mode==AuthenticationMode.JWT&&authPolicy?.SourcePolicyId is not null&&authPolicy.SourceRevision is >0)execution.Record(authPolicy,decision,reason);}
            var mode = authentication?.Mode ?? (route.RequireApiKey ? AuthenticationMode.ApiKey : AuthenticationMode.Anonymous);
            var telemetry = RequestTelemetryState.From(ctx);
            if (telemetry is not null)
            {
                var cluster = indexes.Clusters[route.ClusterId];
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
                { AuthDecision("Rejected","invalid_jwt"); await Reject(ctx, telemetry, 401, "invalid_jwt", true); return; }
                claims = result.Claims;
                if (authPolicy is null || !indexes.JwtApplications.TryGetValue((authPolicy.Id, claims.ApplicationClaimValue), out var applicationId))
                { AuthDecision("Rejected","invalid_jwt"); await Reject(ctx, telemetry, 401, "invalid_jwt", true); return; }
                indexes.Applications.TryGetValue(applicationId, out app);
                if (app?.Status != "Active") { AuthDecision("Rejected","api_not_granted"); await Reject(ctx, telemetry, 403, "api_not_granted"); return; }
                if (jwt!.ForwardBearer) ctx.Request.Headers.Authorization = "Bearer " + claims.ValidatedBearer;
                else ctx.Request.Headers.Remove("Authorization");
            }
            else if (mode == AuthenticationMode.ApiKey)
            {
                var parts = raw.Split('.');
                RuntimeCredential? key = null;
                if (parts.Length == 2 && indexes.Credentials.TryGetValue(parts[0], out var credential))
                { app = credential.Application; key = credential.Credential; }
                if (app?.Status != "Active" || key?.Status != "Active" || now < key.ValidFrom || now >= key.ExpiresAt || !ApiKeySecret.Verify(key.Hash, parts[1]))
                { await Reject(ctx, telemetry, 401, "invalid_api_key"); return; }
            }
            if (app is not null)
            {
                if (telemetry is not null) telemetry.Context = telemetry.Context with { ApplicationKey = app.Id.ToString() };
                if (!indexes.PermissionWindows.TryGetValue((app.Id, route.ApiId), out var windows)
                    || !windows.Any(p => now >= p.ValidFrom && (p.ExpiresAt is null || now < p.ExpiresAt)))
                { AuthDecision("Rejected","api_not_granted"); await Reject(ctx, telemetry, 403, "api_not_granted"); return; }
                execution.ApplicationId = app.Id;
            }
            AuthDecision("Allowed");
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

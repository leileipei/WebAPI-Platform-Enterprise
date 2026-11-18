using System.Diagnostics;
using System.Globalization;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Applications;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.CredentialUsage;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Policies;
using WebApi.Infrastructure.Security;

namespace WebApi.Gateway.Security;

public sealed class ApiKeyMiddleware(RequestDelegate next, IGovernanceAdmissionStateProvider? governance = null)
{
    public static readonly object GenerationItem = new();
    private sealed record Rejection(int Status, string Code, bool Bearer = false);
    private static readonly Rejection InvalidApiKey = new(StatusCodes.Status401Unauthorized, "invalid_api_key");
    private static readonly Rejection InvalidJwt = new(StatusCodes.Status401Unauthorized, "invalid_jwt", Bearer: true);
    private static readonly Rejection ApiNotGranted = new(StatusCodes.Status403Forbidden, "api_not_granted");
    private static readonly Rejection SecurityUnavailable = new(StatusCodes.Status503ServiceUnavailable, "security_state_unavailable");

    public async Task InvokeAsync(HttpContext ctx, RuntimeGenerationStore store, JwtTokenVerifier verifier,
        ICredentialUsageRecorder usage, CredentialUsageAccumulator accumulator, TimeProvider clock)
    {
        var execution = TrafficExecutionContext.From(ctx) ?? throw new InvalidOperationException("Routing policy context unavailable.");
        var generation = execution.Generation;
        var route = execution.Route;
        var indexes = generation.Indexes;
        var routeAuthentication = indexes.RouteAuthentication[route.Id];
        var authentication = routeAuthentication.Configuration;
        var authPolicy = routeAuthentication.Policy;
        var mode = authentication?.Mode ?? (route.RequireApiKey ? AuthenticationMode.ApiKey : AuthenticationMode.Anonymous);
        var telemetry = RequestTelemetryState.From(ctx);
        var raw = ctx.Request.Headers["X-API-Key"].ToString();
        ctx.Request.Headers.Remove("X-API-Key");
        ctx.Request.Headers["X-WebApi-Deployment-Sequence"] = generation.Envelope.DeploymentSequence.ToString(CultureInfo.InvariantCulture);
        ctx.Request.Headers["X-WebApi-Trace-Id"] = ctx.TraceIdentifier;

        var snapshot = generation.Snapshot;
        var governanceDialect = snapshot.SchemaVersion == "2.4" || snapshot.RequiredSecurityGeneration is not null ||
            snapshot.RequiredSecurityEpoch is not null || snapshot.RequireEnvironmentCredentials is not null ||
            snapshot.MinimumSnapshotSchema is not null;
        DateTimeOffset now = default;
        RuntimeApplication? app = null;
        RuntimeCredential? usedCredential = null;
        RuntimePermission? usedWindow = null;
        JwtVerifiedClaims? claims = null;

        Rejection? rejection;
        if (mode != AuthenticationMode.Anonymous && (governanceDialect || governance is not null))
        {
            rejection = SecurityUnavailable;
            if (governance is not null && governance.GetActiveState(generation) is { } state)
            {
                generation.PrepareSecurityAdmission();
                // Revocation and security policy publication wait for this decision.
                using var ticket = await state.EnterAdmissionAsync(ctx.RequestAborted);
                var snapshotAllowed = state.CanUseSnapshot(generation);
                now = clock.GetUtcNow();
                rejection = VerifyIdentity();
                var providerCurrent = ReferenceEquals(state, governance.GetActiveState(generation));
                now = clock.GetUtcNow();
                if (rejection is null && snapshotAllowed && providerCurrent)
                {
                    if (usedCredential is { } credential && (!state.CredentialEnvironmentAllowed(credential) ||
                        !WithinWindow(now, credential.ValidFrom, credential.ExpiresAt))) rejection = InvalidApiKey;
                    else if (claims is not null && !claims.IsValidAt(now)) rejection = InvalidJwt;
                    else rejection = VerifyPermission();
                }
                var final = state.CanAdmit(usedCredential?.Id ?? Guid.Empty, now);
                if (final == SecurityAdmissionDecision.KnownRevoked) rejection = InvalidApiKey;
                else if (!snapshotAllowed || !providerCurrent || final != SecurityAdmissionDecision.Allow) rejection = SecurityUnavailable;
            }
        }
        else
        {
            now = clock.GetUtcNow();
            rejection = VerifyIdentity() ?? VerifyPermission();
        }

        if (rejection is not null)
        {
            if (rejection != SecurityUnavailable) RecordAuthDecision("Rejected", rejection.Code);
            await Reject(ctx, telemetry, rejection);
            return;
        }

        if (claims is not null)
        {
            if (authentication!.Jwt!.ForwardBearer) ctx.Request.Headers.Authorization = "Bearer " + claims.ValidatedBearer;
            else ctx.Request.Headers.Remove("Authorization");
        }
        if (app is not null) execution.ApplicationId = app.Id;
        RecordAuthDecision("Allowed");
        execution.VerifiedIdentity = new(mode, app?.Id, claims);
        if (mode == AuthenticationMode.ApiKey && app is not null && usedCredential is not null && usedWindow is not null)
        {
            try
            {
                var utc = now.ToUniversalTime();
                var hour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
                var source = generation.Envelope;
                usage.TryRecord(new(snapshot.EnvironmentId, new(app.Id, usedCredential.Id, route.ApiId,
                    usedWindow.ValidFrom, usedWindow.ExpiresAt,
                    new(source.ReleaseId, source.ConfigVersion, source.DeploymentSequence, source.PayloadHash), hour, 1, utc, utc)));
            }
            catch (Exception) { accumulator.MarkGap(now, "RecorderFailure"); }
        }
        await next(ctx);

        void RecordAuthDecision(string decision, string? reason = null)
        {
            if (mode == AuthenticationMode.JWT && authPolicy?.SourcePolicyId is not null && authPolicy.SourceRevision is > 0)
                execution.Record(authPolicy, decision, reason);
        }

        Rejection? VerifyIdentity()
        {
            if (mode == AuthenticationMode.JWT)
            {
                var authorization = ctx.Request.Headers.Authorization;
                var header = authorization.Count == 1 ? authorization[0] : null;
                var jwt = authentication?.Jwt;
                var result = header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && jwt is not null
                    ? verifier.Verify(header[7..], jwt, now) : null;
                if (result?.Success != true || result.Claims is null) return InvalidJwt;
                claims = result.Claims;
                if (authPolicy is null || !indexes.JwtApplications.TryGetValue((authPolicy.Id, claims.ApplicationClaimValue), out var applicationId))
                    return InvalidJwt;
                indexes.Applications.TryGetValue(applicationId, out app);
                if (app?.Status != "Active") return ApiNotGranted;
            }
            else if (mode == AuthenticationMode.ApiKey)
            {
                var parts = raw.Split('.');
                RuntimeCredential? key = null;
                if (parts.Length == 2 && indexes.Credentials.TryGetValue(parts[0], out var credential))
                {
                    app = credential.Application;
                    key = credential.Credential;
                }
                if (app?.Status != "Active" || key?.Status != "Active" || !WithinWindow(now, key.ValidFrom, key.ExpiresAt) || !ApiKeySecret.Verify(key.Hash, parts[1]))
                    return InvalidApiKey;
                usedCredential = key;
            }
            return null;
        }

        Rejection? VerifyPermission()
        {
            if (app is null) return null;
            if (telemetry is not null) telemetry.Context = telemetry.Context with { ApplicationKey = app.Id.ToString() };
            if (!indexes.PermissionWindows.TryGetValue((app.Id, route.ApiId), out var windows)) return ApiNotGranted;
            if (governanceDialect)
            {
                if (!PermissionEvaluator.IsAllowed(app with { Permissions = windows }, route, ctx.Request.Method, now)) return ApiNotGranted;
                usedWindow = windows[0];
            }
            else
            {
                usedWindow = windows.FirstOrDefault(p => WithinWindow(now, p.ValidFrom, p.ExpiresAt));
                if (usedWindow is null) return ApiNotGranted;
            }
            return null;
        }
    }

    private static bool WithinWindow(DateTimeOffset now, DateTimeOffset validFrom, DateTimeOffset? expiresAt) =>
        now >= validFrom && (expiresAt is null || now < expiresAt);

    private static Task Reject(HttpContext ctx, RequestTelemetryState? telemetry, Rejection rejection)
    {
        ctx.Response.StatusCode = rejection.Status;
        if (rejection.Bearer) ctx.Response.Headers.WWWAuthenticate = "Bearer";
        return ctx.Response.WriteAsJsonAsync(new
        {
            code = rejection.Code,
            traceId = ctx.TraceIdentifier,
            w3cTraceId = telemetry?.Context.TraceId ?? Activity.Current?.TraceId.ToHexString()
        });
    }
}

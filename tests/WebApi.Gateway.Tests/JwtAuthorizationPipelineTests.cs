using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.TestBackend;
using Xunit;
using SigningKey = WebApi.Gateway.Tests.JwtTokenVerifierTests.KeyFixture;

namespace WebApi.Gateway.Tests;
public sealed class JwtAuthorizationPipelineTests
{
    private const string Rate = "{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":1,\"windowMs\":600000,\"burst\":3,\"redisFailureMode\":\"Reject\"}";
    private sealed class Limiter : IRateLimitStore
    {
        internal ConcurrentQueue<RateLimitRequest> Requests = [];
        internal bool Hold;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<RateLimitDecision> TakeAsync(RateLimitRequest request, CancellationToken ct)
        { Requests.Enqueue(request); if (Hold) { Started.TrySetResult(); await Released.Task.WaitAsync(ct); } return new(RateLimitDecisionKind.Allowed); }
    }
    private static string Token(SigningKey key, string? app = null)
    { var claims = key.Claims(); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); claims["iat"] = now - 60; claims["exp"] = now + 1800; if (app is not null) claims["azp"] = app; return key.Token(claims); }
    private static string Config(SigningKey key, Guid app, bool forward = false) => PolicyConfigurationValidator.Normalize("authentication",
        JsonSerializer.Serialize(key.Configuration() with { ApplicationMappings = [new("private-app-canary-8362", app)], ForwardBearer = forward }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    private static async Task<Policy> Bind(GatewayFixture f, SigningKey key, bool forward = false, bool rate = false)
    {
        await using var db = f.Control.Context();
        var old = await db.Set<RoutePolicyBinding>().Where(b => b.RouteId == f.RouteId).ToArrayAsync();
        db.RemoveRange(old);
        var policy = new Policy { OrganizationId = f.Control.Organization.Id, ProjectId = f.Control.Project.Id, Name = "JWT", Type = "authentication", Config = Config(key, f.Control.Application.Id, forward) };
        db.Add(policy); db.Add(new RoutePolicyBinding { RouteId = f.RouteId, PolicyId = policy.Id });
        if (rate) { var r = new Policy { OrganizationId = f.Control.Organization.Id, ProjectId = f.Control.Project.Id, Name = "JWT限流", Type = "rate_limit", Config = Rate }; db.Add(r); db.Add(new RoutePolicyBinding { RouteId = f.RouteId, PolicyId = r.Id }); }
        (await db.Set<ApiRoute>().SingleAsync(r => r.Id == f.RouteId)).Revision++; await db.SaveChangesAsync(); return policy;
    }
    private static async Task AssertCode(HttpResponseMessage response, HttpStatusCode status, string code)
    { Assert.Equal(status, response.StatusCode); Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()); }

    [Fact]
    public async Task JwtValidAppGrantAllowsAndAppRateReceivesIdentity()
    {
        using var key = new SigningKey(); var limiter = new Limiter();
        await using var f = new GatewayFixture { ConfigureGateway = b => b.Services.AddSingleton<IRateLimitStore>(limiter) };
        await f.InitializeAsync(); await Bind(f, key, rate: true); await f.ApplyBothAsync(await f.PublishAsync());
        for (var i = 0; i < 2; i++) { using var response = await f.RequestBearerAsync(Token(key), i); response.EnsureSuccessStatusCode(); }
        Assert.Equal(2, limiter.Requests.Count); Assert.All(limiter.Requests, r => Assert.Equal(f.Control.Application.Id, r.ApplicationId));
    }
    [Fact]
    public async Task WrongTokenCannotFallbackToApiKeyOrAnonymous()
    {
        using var key = new SigningKey(); var hits = 0;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { Interlocked.Increment(ref hits); await next(); }) };
        await f.InitializeAsync(); await Bind(f, key); await f.ApplyBothAsync(await f.PublishAsync());
        foreach (var auth in new string[]?[] { null, ["Bearer "], ["Basic invalid"], ["Bearer invalid"], ["Bearer " + Token(key), "Bearer " + Token(key)], ["Bearer " + Token(key) + ",Bearer " + Token(key)] })
        foreach (var node in new[] { 0, 1 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/orders?access_token=" + Uri.EscapeDataString(Token(key)));
            request.Headers.Add("X-API-Key", f.Credential); request.Headers.Add("Cookie", "access_token=" + Token(key));
            if (auth is not null) request.Headers.TryAddWithoutValidation("Authorization", auth);
            using var response = await f.Clients[node].SendAsync(request);
            await AssertCode(response, HttpStatusCode.Unauthorized, "invalid_jwt"); Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().ToString());
        }
        Assert.Equal(0, hits);
    }
    [Theory] [InlineData("unknown", 401)] [InlineData("disabled", 403)] [InlineData("ungranted", 403)] [InlineData("expired", 403)] [InlineData("future", 403)]
    public async Task UnknownMapping401DisabledOrUnGranted403(string kind, int status)
    {
        using var key = new SigningKey(); var hits = 0;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { Interlocked.Increment(ref hits); await next(); }) };
        await f.InitializeAsync(); await Bind(f, key);
        await using (var db = f.Control.Context())
        {
            if (kind == "disabled") { var app = await db.Set<ApplicationRecord>().SingleAsync(); app.Status = "Disabled"; app.Revision++; }
            var grant = await db.Set<ApplicationApiPermission>().SingleAsync();
            if (kind == "ungranted") db.Remove(grant);
            if (kind == "expired") { grant.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); grant.Revision++; }
            if (kind == "future") { grant.ValidFrom = DateTimeOffset.UtcNow.AddHours(1); grant.Revision++; }
            await db.SaveChangesAsync();
        }
        await f.ApplyBothAsync(await f.PublishAsync());
        foreach (var i in new[] { 0, 1 }) { using var response = await f.RequestBearerAsync(Token(key, kind == "unknown" ? "not-mapped" : null), i); await AssertCode(response, (HttpStatusCode)status, status == 401 ? "invalid_jwt" : "api_not_granted"); }
        Assert.Equal(0, hits);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task JwtStripsSpoofedHeadersAndOnlyExplicitlyForwardsBearer(bool forward)
    {
        using var key = new SigningKey(); var observed = new ConcurrentQueue<Dictionary<string, string>>();
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { observed.Enqueue(ctx.Request.Headers.ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase)); await next(); }) };
        await f.InitializeAsync(); await Bind(f, key, forward); var published = await f.PublishAsync(); await f.ApplyBothAsync(published);
        var token = Token(key); using var request = new HttpRequestMessage(HttpMethod.Get, "/orders"); request.Headers.Add("Authorization", "Bearer " + token);
        foreach (var name in new[] { "X-API-Key", "X-WebApi-Application-Id", "X-WebApi-Subject", "X-WebApi-Fake", "X-WebApi-Trace-Id", "X-WebApi-Deployment-Sequence" }) request.Headers.Add(name, "spoof");
        using var response = await f.Clients[0].SendAsync(request); response.EnsureSuccessStatusCode(); var headers = Assert.Single(observed);
        Assert.False(headers.ContainsKey("X-API-Key")); foreach (var name in new[] { "X-WebApi-Application-Id", "X-WebApi-Subject", "X-WebApi-Fake" }) Assert.False(headers.ContainsKey(name));
        Assert.Equal(published.Envelope.DeploymentSequence.ToString(), headers["X-WebApi-Deployment-Sequence"]); Assert.NotEqual("spoof", headers["X-WebApi-Trace-Id"]);
        if (forward) Assert.True(headers["Authorization"] == "Bearer " + token); else Assert.False(headers.ContainsKey("Authorization"));
    }
    [Fact]
    public async Task OldRequestAcrossActivationUsesOldJwtKeysMappingAndGrant()
    {
        using var oldKey = new SigningKey(); using var newKey = new SigningKey(); var limiter = new Limiter { Hold = true };
        await using var f = new GatewayFixture { ConfigureGateway = b => b.Services.AddSingleton<IRateLimitStore>(limiter) };
        await f.InitializeAsync(); var policy = await Bind(f, oldKey, rate: true); var first = await f.PublishAsync(); await f.ApplyBothAsync(first);
        var waiting = f.RequestBearerAsync(Token(oldKey));
        try
        {
            await limiter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var db = f.Control.Context()) { var p = await db.Set<Policy>().SingleAsync(p => p.Id == policy.Id); p.Config = Config(newKey, f.Control.Application.Id); p.VersionNo++; db.RemoveRange(await db.Set<ApplicationApiPermission>().ToArrayAsync()); await db.SaveChangesAsync(); }
            var second = await f.PublishAsync(1); await f.ApplyBothAsync(second);
            using var badKey = await f.RequestBearerAsync(Token(oldKey)); await AssertCode(badKey, HttpStatusCode.Unauthorized, "invalid_jwt");
            using var noGrant = await f.RequestBearerAsync(Token(newKey)); await AssertCode(noGrant, HttpStatusCode.Forbidden, "api_not_granted");
            limiter.Released.TrySetResult(); using var response = await waiting; response.EnsureSuccessStatusCode();
            Assert.Equal(first.Envelope.DeploymentSequence, (await response.Content.ReadFromJsonAsync<BackendResponse>())!.DeploymentSequence);
            Assert.Equal(f.Control.Application.Id, Assert.Single(limiter.Requests).ApplicationId);
        }
        finally { limiter.Released.TrySetResult(); }
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task LegacyApiKeyAndAnonymousPreserveBusinessAuthorization(bool requireKey)
    {
        var observed = new ConcurrentQueue<string>();
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { observed.Enqueue(ctx.Request.Headers.Authorization.ToString()); await next(); }) };
        await f.InitializeAsync(); await using (var db = f.Control.Context()) { var route = await db.Set<ApiRoute>().SingleAsync(); db.RemoveRange(await db.Set<RoutePolicyBinding>().Where(b => b.RouteId == route.Id).ToArrayAsync()); var auth = new Policy { OrganizationId = f.Control.Organization.Id, ProjectId = f.Control.Project.Id, Name = "旧认证", Type = "authentication", Config = requireKey ? "{\"mode\":\"ApiKey\"}" : "{\"mode\":\"Anonymous\"}" }; db.Add(auth); db.Add(new RoutePolicyBinding { RouteId = route.Id, PolicyId = auth.Id }); route.Revision++; await db.SaveChangesAsync(); }
        await f.ApplyBothAsync(await f.PublishAsync()); using var request = new HttpRequestMessage(HttpMethod.Get, "/orders"); request.Headers.Add("X-API-Key", f.Credential); request.Headers.Add("Authorization", "Basic business-secret");
        using var response = await f.Clients[0].SendAsync(request); response.EnsureSuccessStatusCode(); Assert.Equal("Basic business-secret", Assert.Single(observed));
    }
}

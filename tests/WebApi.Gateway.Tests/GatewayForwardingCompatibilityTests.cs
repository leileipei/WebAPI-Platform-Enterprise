using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Forwarder;
using WebApi.Gateway.Forwarding;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class GatewayForwardingCompatibilityTests
{
    private sealed class ConfiguredFilter(bool passive = false) : IProxyConfigFilter
    {
        public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken ct) => ValueTask.FromResult(passive ? cluster with { HealthCheck = (cluster.HealthCheck ?? new HealthCheckConfig()) with { Passive = new PassiveHealthCheckConfig { Enabled = true, Policy = "recording" } } } : cluster);
        public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken ct) => ValueTask.FromResult(route with { Transforms = new IReadOnlyDictionary<string,string>[] { new Dictionary<string,string> { ["RequestHeader"] = "X-Configured", ["Set"] = "configured" }, new Dictionary<string,string> { ["ResponseHeader"] = "X-Response-Configured", ["Set"] = "configured" } } });
    }
    private sealed class HealthRecorder : IPassiveHealthCheckPolicy
    {
        public string Name => "recording";
        internal ConcurrentQueue<(string Destination,int Status)> Calls = [];
        public void RequestProxied(HttpContext context, ClusterState cluster, DestinationState destination) => Calls.Enqueue((destination.DestinationId, context.Response.StatusCode));
    }
    [Fact]
    public async Task DefaultAndConfiguredTransformsKeepHostForwardedBasePathAndRawQuery()
    {
        var seen = new ConcurrentQueue<(string Host, string Path, string Query, string ForwardedHost, string Configured)>();
        await using var f = new GatewayFixture
        {
            ConfigureGateway = b => b.Services.AddSingleton<IProxyConfigFilter>(new ConfiguredFilter()),
            ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } seen.Enqueue((ctx.Request.Host.Value!, ctx.Request.Path.Value!, ctx.Request.QueryString.Value!, ctx.Request.Headers["X-Forwarded-Host"].ToString(), ctx.Request.Headers["X-Configured"].ToString())); ctx.Response.Headers["X-Backend"] = "actual"; await ctx.Response.WriteAsync("streamed"); })
        };
        await f.InitializeAsync(); await RetryPipelineTests.Bind(f, RetryPipelineTests.Config());
        await f.ApplyBothAsync(await f.PublishAsync(basePath: "/base/"));
        using var response = await f.RequestAsync(path: "/orders?a=1&a=2&x=%2F+%20"); response.EnsureSuccessStatusCode(); Assert.Equal("streamed", await response.Content.ReadAsStringAsync()); Assert.Equal("configured", response.Headers.GetValues("X-Response-Configured").Single());
        var entry = Assert.Single(seen); Assert.Equal(new Uri(f.BackendUrls[0]).Authority, entry.Host); Assert.Equal("/base/orders", entry.Path); Assert.Equal("?a=1&a=2&x=%2F+%20", entry.Query); Assert.Equal(new Uri(f.Clients[0].BaseAddress!.ToString()).Authority, entry.ForwardedHost); Assert.Equal("configured", entry.Configured);
    }
    [Fact]
    public async Task UpstreamRedirectAndCookieAreNotInternallyFollowedOrPersisted()
    {
        var calls = 0; var cookieSeen = false;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } Interlocked.Increment(ref calls); cookieSeen |= ctx.Request.Headers.ContainsKey("Cookie"); ctx.Response.StatusCode = 302; ctx.Response.Headers.Location = "/orders?redirected=1"; ctx.Response.Headers.SetCookie = "upstream-session=synthetic; Path=/"; await ctx.Response.WriteAsync("redirect"); }) };
        await f.InitializeAsync(); await RetryPipelineTests.Bind(f, RetryPipelineTests.Config()); await f.ApplyBothAsync(await f.PublishAsync());
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = f.Clients[0].BaseAddress }; client.DefaultRequestHeaders.Add("X-API-Key", f.Credential);
        for (var i = 0; i < 2; i++) { using var response = await client.GetAsync("/orders"); Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("redirect", await response.Content.ReadAsStringAsync()); }
        Assert.Equal(2, calls); Assert.False(cookieSeen);
    }
    [Fact]
    public async Task EveryActualAttemptNotifiesPassiveHealthAndReleasesOwnConcurrency()
    {
        var calls = 0; var health = new HealthRecorder();
        await using var f = new GatewayFixture { ConfigureGateway = b => { b.Services.AddSingleton<IPassiveHealthCheckPolicy>(health); b.Services.AddSingleton<IProxyConfigFilter>(new ConfiguredFilter(true)); }, ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } ctx.Response.StatusCode = Interlocked.Increment(ref calls) < 3 ? 503 : 200; await ctx.Response.WriteAsync("response"); }) };
        await f.InitializeAsync(); await RetryPipelineTests.Bind(f, RetryPipelineTests.Config()); await f.ApplyBothAsync(await f.PublishAsync());
        using var response = await f.RequestAsync(); response.EnsureSuccessStatusCode(); Assert.Equal(new[] { 503, 503, 200 }, health.Calls.Select(x => x.Status).ToArray());
        Assert.Single(health.Calls.Select(x => x.Destination).Distinct());
        Assert.Equal(0, f.Gateways[0].Services.GetRequiredService<ForwardAttemptRegistry>().ActiveEntryCount);
    }
}

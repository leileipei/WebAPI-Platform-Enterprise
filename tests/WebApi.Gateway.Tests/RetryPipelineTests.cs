using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Gateway.Configuration;
using WebApi.Gateway;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.TestBackend;
using Xunit;

namespace WebApi.Gateway.Tests;
public sealed class RetryPipelineTests
{
    internal static string Config(int attempts = 3, int delay = 0, int perAttempt = 3000) => JsonSerializer.Serialize(new RetryConfiguration(attempts, perAttempt, delay, delay, 0, [502, 503, 504], true), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    internal static async Task Bind(GatewayFixture f, string config, int? timeout = null, bool circuit = false, string[]? methods = null)
    {
        await using var db = f.Control.Context();
        var p = new Policy { OrganizationId = f.Control.Organization.Id, ProjectId = f.Control.Project.Id, Name = "有限重试", Type = "retry", Config = config };
        db.Add(p); db.Add(new RoutePolicyBinding { RouteId = f.RouteId, PolicyId = p.Id });
        var route = await db.Set<ApiRoute>().SingleAsync(r => r.Id == f.RouteId); route.Revision++;
        if (timeout.HasValue) route.TimeoutMs = timeout.Value;
        if (methods is not null) route.Methods = methods;
        if (circuit)
        {
            var c = new Policy { OrganizationId = f.Control.Organization.Id, ProjectId = f.Control.Project.Id, Name = "熔断", Type = "circuit_breaker", Config = "{\"samplingWindowMs\":30000,\"minimumRequests\":2,\"failureRatio\":0.5,\"openDurationMs\":1000,\"halfOpenMaxRequests\":1,\"halfOpenSuccesses\":1,\"failureStatusCodes\":[503],\"countTimeouts\":true,\"countConnectionFailures\":true}" };
            db.Add(c); db.Add(new RoutePolicyBinding { RouteId = f.RouteId, PolicyId = c.Id });
        }
        await db.SaveChangesAsync();
    }
    [Fact]
    public async Task TwoSuppressed503Then200ProducesThreeCallsOneResponse()
    {
        var calls = 0;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } var n = Interlocked.Increment(ref calls); ctx.Response.StatusCode = n <= 2 ? 503 : 200; ctx.Response.Headers["X-Attempt"] = n.ToString(); await ctx.Response.WriteAsync("attempt-" + n); }) };
        await f.InitializeAsync(); await Bind(f, Config()); await f.ApplyBothAsync(await f.PublishAsync());
        using var response = await f.RequestAsync(); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("attempt-3", await response.Content.ReadAsStringAsync()); Assert.Equal("3", response.Headers.GetValues("X-Attempt").Single()); Assert.Equal(3, calls);
    }
    [Fact]
    public async Task ExhaustedFailureForwardsLastCompleteResponse()
    {
        var calls = 0;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } var n = Interlocked.Increment(ref calls); ctx.Response.StatusCode = 503; ctx.Response.Headers["X-Attempt"] = n.ToString(); await ctx.Response.WriteAsync("last-" + n); }) };
        await f.InitializeAsync(); await Bind(f, Config()); await f.ApplyBothAsync(await f.PublishAsync());
        using var response = await f.RequestAsync(); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal("last-3", await response.Content.ReadAsStringAsync()); Assert.Equal("3", response.Headers.GetValues("X-Attempt").Single()); Assert.Equal(3, calls);
    }
    [Fact]
    public async Task RetryUsesDifferentHealthyWeightedTargets()
    {
        var calls = 0;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } Interlocked.Increment(ref calls); ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("A failed"); }) };
        await f.InitializeAsync(); await Bind(f, Config());
        using var added = await f.Control.WriteAsync(HttpMethod.Post, $"/api/v1/clusters/{f.Control.Cluster.Id}/destinations", new { name = "weighted-B", address = f.BackendUrls[1] + "/", weight = 3, enabled = true }); added.EnsureSuccessStatusCode();
        await f.ApplyBothAsync(await f.PublishAsync()); var successful = 0;
        for (var i = 0; i < 4; i++) { using var response = await f.RequestAsync(); response.EnsureSuccessStatusCode(); var result = await response.Content.ReadFromJsonAsync<BackendResponse>(); Assert.Equal("B", result!.BackendId); successful++; }
        Assert.Equal(4, successful); Assert.True(calls > 0);
    }
    [Theory] [InlineData("POST")] [InlineData("PUT")] [InlineData("PATCH")] [InlineData("DELETE")] [InlineData("GET_BODY")] [InlineData("RANGE")] [InlineData("CONDITIONAL")] [InlineData("UPGRADE")] [InlineData("GRPC")]
    public async Task NoRetryUnsafeBodyStartedResponseUncertainSendOrCancel(string kind)
    {
        var calls = 0;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } Interlocked.Increment(ref calls); ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("no replay"); }) };
        await f.InitializeAsync(); await Bind(f, Config(), methods: ["GET", "POST", "PUT", "PATCH", "DELETE"]); await f.ApplyBothAsync(await f.PublishAsync());
        using var request = new HttpRequestMessage(new HttpMethod(kind is "POST" or "PUT" or "PATCH" or "DELETE" ? kind : "GET"), "/orders"); request.Headers.Add("X-API-Key", f.Credential);
        if (kind == "GET_BODY" || kind == "POST") request.Content = new StringContent("body");
        if (kind == "RANGE") request.Headers.Add("Range", "bytes=0-3");
        if (kind == "CONDITIONAL") request.Headers.Add("If-None-Match", "\"v1\"");
        if (kind == "UPGRADE") { request.Headers.Add("Connection", "Upgrade"); request.Headers.Add("Upgrade", "synthetic-protocol"); }
        if (kind == "GRPC") { request.Content = new ByteArrayContent([]); request.Content.Headers.ContentType = new("application/grpc"); }
        using var response = await f.Clients[0].SendAsync(request); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task UncertainDisconnectAndStartedBodyNeverReplay()
    {
        var calls = 0;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } Interlocked.Increment(ref calls); if (ctx.Request.Query.ContainsKey("partial")) { ctx.Response.ContentLength = 100; await ctx.Response.WriteAsync("partial"); await ctx.Response.Body.FlushAsync(); } ctx.Abort(); }) };
        await f.InitializeAsync(); await Bind(f, Config()); await f.ApplyBothAsync(await f.PublishAsync());
        using (var response = await f.RequestAsync()) Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        try { using var response = await f.RequestAsync(path: "/orders?partial=1"); await response.Content.ReadAsStringAsync(); } catch (HttpRequestException) { }
        Assert.Equal(2, calls);
    }
    [Theory] [InlineData("delay")] [InlineData("retryAfter")]
    public async Task OverallBudgetIncludesDelayAndRetryAfter(string reason)
    {
        var calls = 0;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } Interlocked.Increment(ref calls); ctx.Response.StatusCode = 503; if (reason == "retryAfter") ctx.Response.Headers.RetryAfter = "10"; await ctx.Response.WriteAsync("original failure"); }) };
        await f.InitializeAsync(); await Bind(f, Config(delay: reason == "delay" ? 500 : 0, perAttempt: 100), timeout: 200); await f.ApplyBothAsync(await f.PublishAsync());
        var watch = Stopwatch.StartNew(); using var response = await f.RequestAsync(); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal("original failure", await response.Content.ReadAsStringAsync()); Assert.Equal(1, calls); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task PerAttemptTimeoutAfterRequestSentDoesNotReplay()
    {
        var calls = 0;
        await using var f = new GatewayFixture { ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } Interlocked.Increment(ref calls); await Task.Delay(1000, ctx.RequestAborted); await next(); }) };
        await f.InitializeAsync(); await Bind(f, Config(perAttempt: 100), timeout: 300); await f.ApplyBothAsync(await f.PublishAsync());
        using var response = await f.RequestAsync(); Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode); Assert.Equal(1, calls);
    }
    internal sealed class DelayClock : TimeProvider
    {
        private ITimer? captured;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal double Offset;
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp() + (long)(Offset * TimestampFrequency);
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddSeconds(Offset);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == TimeSpan.FromMilliseconds(1000) && period == Timeout.InfiniteTimeSpan)
            { Started.TrySetResult(); captured = TimeProvider.System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, period); _ = Released.Task.ContinueWith(_ => captured.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan), TaskScheduler.Default); return captured; }
            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }
    }
    [Fact]
    public async Task ActivationDuringBackoffKeepsOldGeneration()
    {
        var calls = 0; var clock = new DelayClock();
        await using var f = new GatewayFixture { ConfigureGateway = b => b.Services.AddSingleton<TimeProvider>(clock), ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } if (Interlocked.Increment(ref calls) == 1) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("first"); } else await next(); }) };
        await f.InitializeAsync(); await Bind(f, Config(delay: 1000)); var first = await f.PublishAsync(); await f.ApplyBothAsync(first); var waiting = f.RequestAsync();
        try { await clock.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var second = await f.PublishAsync(1); await f.ApplyBothAsync(second); clock.Released.TrySetResult(); using var response = await waiting; response.EnsureSuccessStatusCode(); var result = await response.Content.ReadFromJsonAsync<BackendResponse>(); Assert.Equal("A", result!.BackendId); Assert.Equal(first.Envelope.DeploymentSequence, result.DeploymentSequence); Assert.Equal(2, calls); }
        finally { clock.Released.TrySetResult(); }
    }
    [Fact]
    public async Task HalfOpenIsOneAttemptAndConcurrentOpenStopsNext()
    {
        var calls = 0; var clock = new DelayClock();
        await using var f = new GatewayFixture { ConfigureGateway = b => b.Services.AddSingleton<TimeProvider>(clock), ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } Interlocked.Increment(ref calls); ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("failure"); }) };
        await f.InitializeAsync(); await Bind(f, Config(), circuit: true); await f.ApplyBothAsync(await f.PublishAsync());
        for (var i = 0; i < 2; i++) { using var response = await f.RequestAsync(); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); }
        Assert.Equal(6, calls); clock.Offset = 2;
        using var probe = await f.RequestAsync(); Assert.Equal(HttpStatusCode.ServiceUnavailable, probe.StatusCode); Assert.Equal(7, calls);
    }
    private sealed class AbortObserver : IRateLimitStore, IDisposable
    {
        private CancellationTokenRegistration registration;
        internal TaskCompletionSource Aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<RateLimitDecision> TakeAsync(RateLimitRequest request, CancellationToken ct) { registration = ct.Register(() => Aborted.TrySetResult()); return ValueTask.FromResult(new RateLimitDecision(RateLimitDecisionKind.Allowed)); }
        public void Dispose() => registration.Dispose();
    }
    [Fact]
    public async Task ClientCancellationDuringBackoffStartsNoAdditionalAttempt()
    {
        var calls = 0; var clock = new DelayClock(); using var abort = new AbortObserver();
        await using var f = new GatewayFixture { ConfigureGateway = b => { b.Services.AddSingleton<TimeProvider>(clock); b.Services.AddSingleton<IRateLimitStore>(abort); }, ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } Interlocked.Increment(ref calls); ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("first"); }) };
        await f.InitializeAsync(); await Bind(f, Config(delay: 1000));
        await using (var db = f.Control.Context()) { var rate = new Policy { OrganizationId = f.Control.Organization.Id, ProjectId = f.Control.Project.Id, Name = "取消信号观测", Type = "rate_limit", Config = "{\"algorithm\":\"TokenBucket\",\"keyBy\":\"Route\",\"refillTokens\":1,\"windowMs\":1000,\"burst\":10,\"redisFailureMode\":\"Reject\"}" }; db.Add(rate); db.Add(new RoutePolicyBinding { RouteId = f.RouteId, PolicyId = rate.Id }); await db.SaveChangesAsync(); }
        await f.ApplyBothAsync(await f.PublishAsync());
        using var cancel = new CancellationTokenSource(); using var request = new HttpRequestMessage(HttpMethod.Get, "/orders"); request.Headers.Add("X-API-Key", f.Credential); var waiting = f.Clients[0].SendAsync(request, cancel.Token);
        try { await clock.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await waiting; }); await abort.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(5)); clock.Released.TrySetResult(); await Task.Delay(100); Assert.Equal(1, calls); }
        finally { clock.Released.TrySetResult(); }
    }
    [Fact]
    public async Task ConcurrentOpenDuringBackoffReturnsOriginalFailureWithoutAnotherRequest()
    {
        var calls = 0; var clock = new DelayClock();
        await using var f = new GatewayFixture { ConfigureGateway = b => b.Services.AddSingleton<TimeProvider>(clock), ConfigureBackendA = a => a.Use(async (ctx, next) => { if (ctx.Request.Path == "/health") { await next(); return; } Interlocked.Increment(ref calls); ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("original failure"); }) };
        await f.InitializeAsync(); await Bind(f, Config(delay: 1000), circuit: true); await f.ApplyBothAsync(await f.PublishAsync());
        var gen = f.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!; var route = gen.Snapshot.Routes.Single(); var policy = gen.Snapshot.Policies.Single(p => p.Type == "circuit_breaker"); var settings = f.Gateways[0].Services.GetRequiredService<GatewaySettings>();
        using var state = f.Gateways[0].Services.GetRequiredService<CircuitStateRegistry>().Acquire(new(settings.InstanceId, policy.Id, route.Id, route.ClusterId), gen.CircuitConfigurations[policy.Id]);
        var waiting = f.RequestAsync();
        try { await clock.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); for (var i = 0; i < 2; i++) state.State.Complete(state.State.TryEnter(), CircuitOutcome.Failure); Assert.Equal(CircuitStatus.Open, state.State.Status); clock.Released.TrySetResult(); using var response = await waiting; Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal("original failure", await response.Content.ReadAsStringAsync()); Assert.Equal(1, calls); }
        finally { clock.Released.TrySetResult(); }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Common;
using WebApi.Contracts.CredentialUsage;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.CredentialSecurity;
using WebApi.Domain.Policies;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.CredentialUsage;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Security;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class FormalRotationRequestProofTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(200, true)]
    [InlineData(204, true)]
    [InlineData(302, true)]
    [InlineData(399, true)]
    [InlineData(400, false)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    public async Task FinalHttpStatusControlsProofButNotAdmissionUsage(int status, bool successful)
    {
        await using var fixture = new ProofFixture(async context =>
        {
            context.Response.StatusCode = status;
            if (status != 204) await context.Response.WriteAsync("upstream response");
        });
        await fixture.StartAsync();
        using var response = await fixture.SendAsync();
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Single(fixture.Usage);
        if (successful) fixture.AssertProof(Assert.Single(fixture.Proofs), status, fixture.Lease, fixture.AdmittedAt);
        else Assert.Empty(fixture.Proofs);
    }

    [Fact]
    public async Task LeaseRenewalWhileTheBodyIsPendingKeepsTheOriginalAdmissionIdentity()
    {
        var awaitingBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ProofFixture(async context =>
        {
            await context.Response.WriteAsync("first");
            awaitingBody.TrySetResult();
            await releaseBody.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("last");
        });
        await fixture.StartAsync();
        var original = fixture.Lease;
        var request = fixture.SendAsync();
        try
        {
            await awaitingBody.Task.WaitAsync(Limit);
            Assert.Empty(fixture.Proofs);
            fixture.AdvanceClock(1);
            await fixture.ApplySecurityAsync(2);
            Assert.NotEqual(original.LeaseId, fixture.Lease.LeaseId);
        }
        finally { releaseBody.TrySetResult(); }

        using var response = await request;
        Assert.Equal("firstlast", await response.Content.ReadAsStringAsync());
        fixture.AssertProof(Assert.Single(fixture.Proofs), 200, original, fixture.AdmittedAt.AddSeconds(1));
    }

    [Fact]
    public async Task CacheHitRequiresFreshAdmissionAndRecordsItsOwnCompletion()
    {
        var calls = 0;
        using var cacheSettings = new CacheEligibilityTests.SettingsFixture();
        var cache = new MemoryCache();
        var policy = Policy("cache", CacheEligibilityTests.Config with { VaryHeaders = [] });
        await using var fixture = new ProofFixture(async context =>
        {
            Interlocked.Increment(ref calls);
            context.Response.Headers.CacheControl = "public,max-age=60";
            await context.Response.WriteAsync("cacheable");
        }, [policy], builder =>
        {
            builder.Configuration["GatewayPolicies:Cache:HmacSecretFile"] = cacheSettings.Secret;
            builder.Services.AddSingleton<IResponseCacheStore>(cache);
        });
        await fixture.StartAsync();
        using var miss = await fixture.SendAsync();
        using var hit = await fixture.SendAsync();
        Assert.Equal("cacheable", await hit.Content.ReadAsStringAsync());
        Assert.Equal(1, calls);
        Assert.Equal(2, fixture.Proofs.Count);
        Assert.Equal(2, fixture.Usage.Count);

        fixture.AdvanceClock(1);
        await fixture.ApplySecurityAsync(1, fixture.CredentialId);
        using var rejected = await fixture.SendAsync();
        Assert.Equal(401, (int)rejected.StatusCode);
        Assert.Equal(2, fixture.Proofs.Count);
        Assert.Equal(2, fixture.Usage.Count);
    }

    [Fact]
    public async Task RetriedUpstreamHttpFailureProducesOneProofForTheFinalResponse()
    {
        var calls = 0;
        var retry = Policy("retry", new RetryConfiguration(2, 3000, 0, 0, 0, [503], true));
        await using var fixture = new ProofFixture(async context =>
        {
            context.Response.StatusCode = Interlocked.Increment(ref calls) == 1 ? 503 : 200;
            await context.Response.WriteAsync("upstream response");
        }, [retry]);
        await fixture.StartAsync();
        using var response = await fixture.SendAsync();

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(2, calls);
        Assert.Single(fixture.Usage);
        fixture.AssertProof(Assert.Single(fixture.Proofs), 200, fixture.Lease, fixture.AdmittedAt);
    }

    [Fact]
    public async Task RetriedConnectionFailureDisqualifiesTheFinalSuccess()
    {
        // A bound socket without Listen reserves the port and refuses connections.
        using var refused = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        refused.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)refused.LocalEndPoint!;
        var retry = Policy("retry", new RetryConfiguration(2, 3000, 0, 0, 0, [503], true));
        await using var fixture = new ProofFixture(context => context.Response.WriteAsync("retried"),
            [retry], refusedUpstream: $"http://127.0.0.1:{endpoint.Port}");
        await fixture.StartAsync();
        using var response = await fixture.SendAsync();

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("retried", await response.Content.ReadAsStringAsync());
        Assert.Empty(fixture.Proofs);
        Assert.Single(fixture.Usage);
    }

    [Fact]
    public async Task UpstreamAbortingAfterTheSuccessStatusProducesNoProof()
    {
        var releaseUpstream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new ProofFixture(async context =>
        {
            context.Response.ContentLength = 50;
            await context.Response.WriteAsync("partial");
            await releaseUpstream.Task.WaitAsync(context.RequestAborted);
            context.Abort();
        });
        await fixture.StartAsync();
        using var request = fixture.Request();
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            Assert.Equal(200, (int)response.StatusCode);
            Assert.Empty(fixture.Proofs);
        }
        finally { releaseUpstream.TrySetResult(); }
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(Stream.Null);
        });
        await fixture.WaitCompletedAsync();
        Assert.Empty(fixture.Proofs);
        Assert.Single(fixture.Usage);
    }

    [Fact]
    public async Task ServerRejectingAnIncompleteResponseAfterForwardingProducesNoProof()
    {
        await using var fixture = new ProofFixture(context => context.Response.WriteAsync("partial"),
            onRequest: context => context.Response.OnStarting(() =>
            {
                context.Response.ContentLength = 50;
                return Task.CompletedTask;
            }));
        await fixture.StartAsync();
        using var request = fixture.Request();
        using var response = await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(200, (int)response.StatusCode);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(Stream.Null);
        });
        await fixture.WaitCompletedAsync();

        Assert.Empty(fixture.Proofs);
        Assert.Single(fixture.Usage);
    }

    [Fact]
    public async Task ClientDisconnectDuringTheBodyProducesNoProof()
    {
        await using var fixture = new ProofFixture(async context =>
        {
            await context.Response.WriteAsync("first");
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        await fixture.StartAsync();
        using var client = new TcpClient { LingerState = new(true, 0) };
        await client.ConnectAsync("127.0.0.1", fixture.Client.BaseAddress!.Port).WaitAsync(Limit);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET /orders HTTP/1.1\r\nHost: localhost\r\nX-API-Key: {fixture.ApiKey}\r\n\r\n"));
        var read = await stream.ReadAsync(new byte[1024]).AsTask().WaitAsync(Limit);
        Assert.True(read > 0);
        client.Dispose();
        await fixture.WaitCompletedAsync();
        Assert.Empty(fixture.Proofs);
        Assert.Single(fixture.Usage);
    }

    [Fact]
    public async Task CredentialAndMethodRejectionsAndKeyBearingPreflightProduceNoProof()
    {
        var cors = Policy("cors", new CorsConfiguration(["https://client.test"], false, ["GET"], ["X-API-Key"], [], 60));
        await using var fixture = new ProofFixture(_ => throw new InvalidOperationException("Unexpected forwarding."), [cors]);
        await fixture.StartAsync();
        using var invalid = fixture.Request();
        invalid.Headers.Remove("X-API-Key");
        invalid.Headers.Add("X-API-Key", "invalid.key");
        using var unauthorized = await fixture.SendAsync(invalid);
        Assert.Equal(401, (int)unauthorized.StatusCode);
        using var post = fixture.Request(HttpMethod.Post);
        using var forbidden = await fixture.SendAsync(post);
        Assert.Equal(403, (int)forbidden.StatusCode);
        using var preflight = fixture.Request(HttpMethod.Options);
        preflight.Headers.Add("Origin", "https://client.test");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "X-API-Key");
        using var corsResponse = await fixture.SendAsync(preflight);
        Assert.Equal(204, (int)corsResponse.StatusCode);
        Assert.Empty(fixture.Proofs);
        Assert.Empty(fixture.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrFaultingSinkPreservesBusinessResponseAndAdmissionUsage(bool throwFromSink)
    {
        await using var fixture = new ProofFixture(context => context.Response.WriteAsync("business"),
            enableSink: throwFromSink, throwFromSink: throwFromSink);
        await fixture.StartAsync();

        using var response = await fixture.SendAsync();
        Assert.Equal("business", await response.Content.ReadAsStringAsync());
        Assert.Single(fixture.Usage);
        Assert.Equal(throwFromSink ? 1 : 0, fixture.Proofs.Count);
    }

    private static RuntimePolicy Policy(string type, object config)
    {
        var source = Guid.NewGuid();
        var normalized = PolicyConfigurationValidator.Normalize(type, JsonSerializer.Serialize(config, CanonicalJson.Options));
        return new(RuntimePolicyIdentity.Create(source, 1, type, normalized), type, normalized, source, 1);
    }

    private sealed class ProofFixture(Func<HttpContext, Task> respond, RuntimePolicy[]? policies = null,
        Action<WebApplicationBuilder>? configure = null, bool enableSink = true, bool throwFromSink = false,
        Action<HttpContext>? onRequest = null, string? refusedUpstream = null) : IAsyncDisposable
    {
        private readonly SecurityFixture security = new();
        private readonly ApiKeyMaterial key = ApiKeySecret.Create();
        private readonly Channel<bool> completions = Channel.CreateUnbounded<bool>();
        private readonly UsageRecorder usage = new();
        private WebApplication? upstream;
        private WebApplication? gateway;
        private SecurityStateStore? state;
        private SnapshotEnvelope envelope = null!;
        private RuntimeRoute route = null!;
        private Guid applicationId;
        public Guid CredentialId { get; } = Guid.NewGuid();
        public string ApiKey => key.AccessKey + "." + key.Secret;
        public HttpClient Client { get; private set; } = null!;
        public DateTimeOffset AdmittedAt { get; private set; }
        public (Guid NodeId, Guid InstanceId, Guid LeaseId, long Epoch, string HeadHash) Lease { get; private set; }
        public ConcurrentQueue<UsageObservation> Usage => usage.Observations;
        public ConcurrentQueue<FormalRotationRequestProof> Proofs { get; } = new();

        public async Task StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            upstream = builder.Build();
            upstream.Run(context => respond(context));
            await upstream.StartAsync();
            var upstreamUrl = GatewayFixture.Url(upstream);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(security.DirectoryPath);
            else Directory.CreateDirectory(security.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var secret = Path.Combine(security.DirectoryPath, "node.secret");
            await File.WriteAllTextAsync(secret, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var provider = new AdmissionProvider();
            gateway = GatewayApp.Build([], builder =>
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Configuration["Gateway:EnvironmentId"] = security.Env.ToString();
                builder.Configuration["Gateway:NodeName"] = "proof-gateway";
                builder.Configuration["Gateway:SecretFile"] = secret;
                builder.Configuration["Gateway:LkgDirectory"] = Path.Combine(security.DirectoryPath, "gateway");
                builder.Configuration["Gateway:ControlPlaneUrl"] = "http://127.0.0.1:1";
                builder.Configuration["Gateway:AutomaticUpdates"] = "false";
                builder.Configuration["Upstream:AllowedOrigins:0"] = upstreamUrl;
                if (refusedUpstream is not null) builder.Configuration["Upstream:AllowedOrigins:1"] = refusedUpstream;
                builder.Configuration["CredentialUsage:Enabled"] = "false";
                builder.Services.AddSingleton<IGovernanceAdmissionStateProvider>(provider);
                builder.Services.AddSingleton<TimeProvider>(security.Clock);
                builder.Services.AddSingleton<ICredentialUsageRecorder>(usage);
                builder.Services.AddSingleton<IStartupFilter>(new ObserveCompletion(completions.Writer, onRequest));
                if (enableSink) builder.Services.AddSingleton<IFormalRotationRequestProofSink>(new ProofSink(Proofs, throwFromSink));
                configure?.Invoke(builder);
            });
            var instance = gateway.Services.GetRequiredService<GatewaySettings>().InstanceId;
            state = new SecurityStateStore(new(security.Env, () => security.Node, instance), security.Disk, security.Clock);
            provider.State = state;
            AdmittedAt = security.Clock.GetUtcNow();
            await ApplySecurityAsync(0);
            await gateway.StartAsync();
            RuntimeDestination[] destinations = refusedUpstream is null
                ? [new(Guid.NewGuid(), upstreamUrl + "/", 1)]
                : [new(new Guid("00000000-0000-0000-0000-000000000001"), refusedUpstream + "/", 1),
                   new(new Guid("00000000-0000-0000-0000-000000000002"), upstreamUrl + "/", 1)];
            var cluster = new RuntimeCluster(Guid.NewGuid(), security.Env, "FirstAlphabetical", false, "/health", 30, destinations);
            route = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cluster.Id, "/orders", ["GET", "POST"], 1, 20000, true,
                policies?.Select((policy, index) => new RuntimePolicyBinding(policy.Id, index + 1)).ToArray(),
                AuthorizationRevision: 12, EnvironmentId: security.Env);
            var expiresAt = AdmittedAt.AddHours(1);
            var grant = new RuntimePermission(route.ApiId, AdmittedAt.AddMinutes(-1), expiresAt, "RouteMethod",
                [new(route.Id, 12, ["GET"])], security.Env);
            var credential = new RuntimeCredential(CredentialId, key.AccessKey, key.Hash, "Active", grant.ValidFrom, expiresAt,
                security.Env, "EnvironmentBound");
            applicationId = Guid.NewGuid();
            var snapshot = new RuntimeSnapshot("2.4", security.Env, 4, AdmittedAt, [route], [cluster], policies ?? [],
                [new(applicationId, "Active", [credential], [grant], security.Env)], security.Generation, 0, false, "2.4");
            var payload = CanonicalJson.Serialize(snapshot);
            envelope = new(Guid.NewGuid(), 9, 4, Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length);
            var activation = await gateway.Services.GetRequiredService<SnapshotActivation>().ApplyAsync(envelope, payload);
            Assert.True(activation.Applied, activation.ErrorCode);
            Client = new(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = new(GatewayFixture.Url(gateway)), Timeout = Limit };
        }

        public void AdvanceClock(double seconds) => security.Clock.Advance(seconds);

        public async Task ApplySecurityAsync(long epoch, params Guid[] revoked)
        {
            var instance = gateway!.Services.GetRequiredService<GatewaySettings>().InstanceId;
            var pair = security.Pair(epoch, revoked, leaseInstance: instance, minimumSchema: "2.4");
            var applied = await state!.ApplyAsync(pair.Checkpoint, pair.Lease);
            Assert.True(applied.Applied, applied.ErrorCode);
            Lease = (security.Node, instance, pair.Lease.Payload.LeaseId, epoch, pair.Checkpoint.Payload.HeadHash);
        }

        public HttpRequestMessage Request(HttpMethod? method = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, "/orders");
            request.Headers.Add("X-API-Key", ApiKey);
            return request;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage? request = null)
        {
            using var defaultRequest = request is null ? Request() : null;
            var response = await Client.SendAsync(request ?? defaultRequest!);
            await WaitCompletedAsync();
            return response;
        }

        public async Task WaitCompletedAsync()
        {
            using var timeout = new CancellationTokenSource(Limit);
            await completions.Reader.ReadAsync(timeout.Token);
        }

        public void AssertProof(FormalRotationRequestProof proof, int status,
            (Guid NodeId, Guid InstanceId, Guid LeaseId, long Epoch, string HeadHash) lease, DateTimeOffset completedAt)
        {
            Assert.Equal(new FormalRotationRequestProof(security.Env, lease.NodeId, lease.InstanceId, CredentialId, applicationId,
                route.Id, "GET", envelope.ReleaseId, 4, 9, envelope.PayloadHash, security.Generation, lease.Epoch,
                lease.HeadHash, lease.LeaseId, AdmittedAt, completedAt, status), proof);
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (gateway is not null) { await gateway.StopAsync(); await gateway.DisposeAsync(); }
            if (upstream is not null) { await upstream.StopAsync(); await upstream.DisposeAsync(); }
            if (state is not null) await state.DisposeAsync();
            security.Dispose();
        }
    }

    private sealed class AdmissionProvider : IGovernanceAdmissionStateProvider
    {
        public SecurityStateStore? State { get; set; }
        public SecurityStateStore? GetActiveState(RuntimeGeneration generation) => State;
    }

    private sealed class UsageRecorder : ICredentialUsageRecorder
    {
        public ConcurrentQueue<UsageObservation> Observations { get; } = new();
        public bool TryRecord(UsageObservation observation) { Observations.Enqueue(observation); return true; }
    }

    private sealed class ProofSink(ConcurrentQueue<FormalRotationRequestProof> proofs, bool fail) : IFormalRotationRequestProofSink
    {
        public bool TryRecord(FormalRotationRequestProof proof)
        {
            proofs.Enqueue(proof);
            if (fail) throw new InvalidOperationException("Test sink failure.");
            return true;
        }
    }

    private sealed class ObserveCompletion(ChannelWriter<bool> completed, Action<HttpContext>? onRequest) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, remaining) =>
            {
                onRequest?.Invoke(context);
                context.Response.OnCompleted(() =>
                {
                    completed.TryWrite(true);
                    return Task.CompletedTask;
                });
                await remaining();
            });
            next(app);
        };
    }

    private sealed class MemoryCache : IResponseCacheStore
    {
        private readonly ConcurrentDictionary<CacheLookupKey, CacheEntry> entries = new();
        public ValueTask<CacheReadResult> GetAsync(CacheLookupKey key, CancellationToken ct) =>
            ValueTask.FromResult(entries.TryGetValue(key, out var entry)
                ? new CacheReadResult(CacheReadKind.Hit, entry) : new CacheReadResult(CacheReadKind.Miss));
        public ValueTask<CacheWriteResult> PutAsync(CacheLookupKey key, CacheEntry entry, CancellationToken ct)
        {
            entries[key] = entry;
            return ValueTask.FromResult(new CacheWriteResult(CacheWriteKind.Stored));
        }
    }
}

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Messaging;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Runtime;
using WebApi.Integration.Tests.Support;
using WebApi.Worker.Workers;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class OutboxFailureIsolationTests
{
    private static async Task<(ReleaseRecord Release, SnapshotEnvelope Envelope, byte[] Payload)> SeedAsync(ApiFixture api, Guid environmentId, bool outbox = false)
    {
        var payload = CanonicalJson.Serialize(new RuntimeSnapshot("2.0", environmentId, 1, DateTimeOffset.UtcNow, [], [], [], []));
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var version = new GatewayConfigVersion { EnvironmentId = environmentId, CreatedBy = api.User.Id, SnapshotHash = hash };
        var release = new ReleaseRecord { EnvironmentId = environmentId, ReleaseNo = "isolation-" + environmentId, RequestedBy = api.User.Id, ToConfigVersion = 1, DeploymentSequence = 1, ReleaseType = "publish", Status = "Succeeded" };
        await using var db = api.Context();
        db.AddRange(version, release, new GatewayConfigSnapshot { ConfigVersionId = version.Id, Payload = Encoding.UTF8.GetString(payload), PayloadBytes = payload, SizeBytes = payload.Length });
        var env = await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == environmentId); env.DesiredConfigVersion = 1; env.DeploymentSequence = 1;
        if (outbox) db.Add(new OutboxMessage { EnvironmentId = environmentId, ReleaseId = release.Id, DeploymentSequence = 1, EventType = "deployment.desired", Payload = JsonSerializer.Serialize(new DeploymentEvent(Guid.NewGuid(), environmentId, 1, release.Id, 1), CanonicalJson.Options) });
        await db.SaveChangesAsync();
        return (release, new SnapshotEnvelope(release.Id, 1, 1, hash, payload.Length), payload);
    }

    [Theory]
    [InlineData("missing-release")]
    [InlineData("bad-hash")]
    [InlineData("cache-conflict")]
    public async Task PoisonedEnvironmentDoesNotBlockHealthyReconciliation(string failure)
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedScopeAsync();
        var healthy = new EnvironmentRecord { ProjectId = api.Project.Id, Code = "HEALTHY", Name = "Healthy" };
        await using (var db = api.Context()) { db.Add(healthy); await db.SaveChangesAsync(); }
        var bad = await SeedAsync(api, api.Environment.Id); var good = await SeedAsync(api, healthy.Id);
        using var redis = new RedisSnapshotStore("redis:6379", "isolation:" + Guid.NewGuid());
        SnapshotEnvelope? conflicting = null;
        if (failure == "cache-conflict") { conflicting = bad.Envelope with { ReleaseId = Guid.NewGuid() }; await redis.PutAsync(api.Environment.Id, conflicting, bad.Payload); }
        else
        {
            await using var db = api.Context();
            if (failure == "missing-release") await db.Set<ReleaseRecord>().Where(x => x.Id == bad.Release.Id).ExecuteDeleteAsync();
            else await db.Set<GatewayConfigVersion>().Where(x => x.EnvironmentId == api.Environment.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SnapshotHash, new string('0', 64)));
        }
        await using (var db = api.Context()) await new OutboxDispatcher(db, redis).ReconcileDesiredAsync();
        Assert.Equal(good.Envelope, (await redis.GetDesiredAsync(healthy.Id))!.Envelope);
        var rejected = await redis.GetDesiredAsync(api.Environment.Id);
        if (conflicting is null) Assert.Null(rejected); else Assert.Equal(conflicting, rejected!.Envelope);
    }

    [Fact]
    public async Task FailedGlobalReconciliationStillDispatchesAndWaitsForNextReconciliationInterval()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedScopeAsync();
        var seeded = await SeedAsync(api, api.Environment.Id, outbox: true);
        var failure = new ReconciliationQueryFailure();
        using var redis = new RedisSnapshotStore("redis:6379", "worker-isolation:" + Guid.NewGuid());
        var services = new ServiceCollection();
        services.AddDbContext<WebApiDbContext>(options => options.UseNpgsql(api.Database.ConnectionString).AddInterceptors(failure));
        services.AddSingleton(redis); services.AddScoped<OutboxDispatcher>();
        using var provider = services.BuildServiceProvider();
        using var worker = new OutboxWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var processed = false;
            while (!processed && !deadline.IsCancellationRequested)
            {
                await using var db = api.Context(); processed = await db.Set<OutboxMessage>().AnyAsync(x => x.ProcessedAt != null);
                if (!processed) await Task.Delay(100);
            }
            Assert.True(processed, "A failed reconciliation must not starve a healthy pending message.");
            Assert.Equal(seeded.Envelope, (await redis.GetDesiredAsync(api.Environment.Id))!.Envelope);
            await Task.Delay(1200);
            Assert.Equal(1, failure.Attempts);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class ReconciliationQueryFailure : DbCommandInterceptor
    {
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM environments", StringComparison.Ordinal)) { Interlocked.Increment(ref attempts); throw new InvalidOperationException("isolated reconciliation query failure"); }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

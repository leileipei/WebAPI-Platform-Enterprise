using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Runtime;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Messaging;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Runtime;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceIngressIsolationTests
{
    [Theory]
    [InlineData("environment")]
    [InlineData("project")]
    [InlineData("organization")]
    public async Task DisabledHierarchyDeniesEnrollmentAndPreviouslyAuthorizedDesiredReads(string disabled)
    {
        await using var database = new PostgresDatabase();
        var scope = await SeedAsync(database);
        await using var db = database.Context();
        var published = await PublishAsync(db, scope, 1);
        const string secret = "isolated-governance-identity-credential";
        var settings = new NodeEnrollmentSettings([new(scope.Environment, "isolated-node", NodeIdentityService.Hash(secret))]);
        var identities = new NodeIdentityService(db, settings);
        var registry = new NodeRegistry(db, identities);
        var request = new RegisterNodeRequest(scope.Environment, "isolated-node", Guid.NewGuid(), "fixture", ["2.0"]);
        var node = await registry.RegisterAsync(request, secret);
        var identity = await identities.AuthenticateAsync(node.NodeId, secret);
        Assert.Equal(published, (await registry.DesiredAsync(identity))!.Envelope);

        await using (var transition = database.Context()) await DisableAsync(transition, scope, disabled);

        var authentication = await Record.ExceptionAsync(() => identities.AuthenticateAsync(node.NodeId, secret));
        var enrollment = await Record.ExceptionAsync(() => registry.RegisterAsync(request, secret));
        var desired = await Record.ExceptionAsync(() => registry.DesiredAsync(identity));
        Assert.Equal(new[] { "node_authentication_required", "node_authentication_required", "node_authentication_required" },
            new[] { authentication, enrollment, desired }.Select(error => (error as ApiException)?.Code));
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("project")]
    [InlineData("organization")]
    [InlineData("cleared-pointer")]
    public async Task DisabledOrClearedDesiredIsNeitherDispatchedNorReconciled(string transition)
    {
        await using var database = new PostgresDatabase();
        var scope = await SeedAsync(database);
        await using var db = database.Context();
        await PublishAsync(db, scope, 1);
        var oldBytes = (await db.Set<GatewayConfigSnapshot>().AsNoTracking().SingleAsync()).PayloadBytes;
        await DisableAsync(db, scope, transition);

        using var dispatchCache = Cache();
        Assert.True(await new OutboxDispatcher(db, dispatchCache).DispatchNextAsync());
        using var reconciliationCache = Cache();
        await new OutboxDispatcher(db, reconciliationCache).ReconcileDesiredAsync();

        Assert.Equal((false, false), (await dispatchCache.GetDesiredAsync(scope.Environment) is not null,
            await reconciliationCache.GetDesiredAsync(scope.Environment) is not null));
        Assert.NotNull((await db.Set<OutboxMessage>().AsNoTracking().SingleAsync()).ProcessedAt);
        Assert.Equal(oldBytes, (await db.Set<GatewayConfigSnapshot>().AsNoTracking().SingleAsync()).PayloadBytes);
    }

    [Fact]
    public async Task DelayedOlderReleaseCannotPopulateAnEmptyCacheAheadOfTheCurrentPointer()
    {
        await using var database = new PostgresDatabase();
        var scope = await SeedAsync(database);
        await using var db = database.Context();
        await PublishAsync(db, scope, 1);
        var current = await PublishAsync(db, scope, 2);
        using var cache = Cache();
        var dispatcher = new OutboxDispatcher(db, cache);

        Assert.True(await dispatcher.DispatchNextAsync());
        Assert.Null(await cache.GetDesiredAsync(scope.Environment));
        await dispatcher.ReconcileDesiredAsync();
        Assert.Equal(current, (await cache.GetDesiredAsync(scope.Environment))!.Envelope);
        Assert.True(await dispatcher.DispatchNextAsync());
        Assert.Equal(current, (await cache.GetDesiredAsync(scope.Environment))!.Envelope);
        Assert.Equal(2, await db.Set<OutboxMessage>().CountAsync(message => message.ProcessedAt != null));
    }

    [Fact]
    public async Task ClearingDesiredAfterTheWorkerClaimsAMessagePreventsItsDispatch()
    {
        await using var database = new PostgresDatabase();
        var scope = await SeedAsync(database);
        await using var transition = database.Context();
        await PublishAsync(transition, scope, 1);
        var gate = new PauseAfterClaim();
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(gate);
        await using var worker = new WebApiDbContext(options.Options);
        using var cache = Cache();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var dispatch = new OutboxDispatcher(worker, cache).DispatchNextAsync(deadline.Token);
        try
        {
            await gate.ClaimCommitted.Task.WaitAsync(deadline.Token);
            await DisableAsync(transition, scope, "cleared-pointer");
        }
        finally { gate.Resume.TrySetResult(); }

        Assert.True(await dispatch);
        Assert.Null(await cache.GetDesiredAsync(scope.Environment));
        Assert.NotNull((await transition.Set<OutboxMessage>().AsNoTracking().SingleAsync()).ProcessedAt);
    }

    private static RedisSnapshotStore Cache() => new(Environment.GetEnvironmentVariable("WEBAPI_TEST_REDIS") ?? "redis:6379",
        "governance-ingress-test:" + Guid.NewGuid());

    private sealed record FixtureScope(Guid Organization, Guid Project, Guid Environment, Guid User);

    private static async Task<FixtureScope> SeedAsync(PostgresDatabase database)
    {
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var organization = new Organization { Code = "INGRESS", Name = "Ingress fixture" };
        var project = new Project { OrganizationId = organization.Id, Code = "PROJECT", Name = "Fixture project" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Fixture environment" };
        var user = new UserRecord { Username = "isolated-publisher", DisplayName = "Fixture publisher" };
        db.AddRange(organization, project, environment, user,
            new EnvironmentSecurityInitialization { EnvironmentId = environment.Id, ProjectId = project.Id,
                OrganizationId = organization.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        return new(organization.Id, project.Id, environment.Id, user.Id);
    }

    private static async Task<SnapshotEnvelope> PublishAsync(WebApiDbContext db, FixtureScope scope, long sequence)
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(sequence);
        var payload = CanonicalJson.Serialize(new RuntimeSnapshot("2.0", scope.Environment, sequence, time, [], [], [], []));
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var version = new GatewayConfigVersion { EnvironmentId = scope.Environment, VersionNo = sequence, CreatedBy = scope.User, SnapshotHash = hash };
        var release = new ReleaseRecord { EnvironmentId = scope.Environment, ReleaseNo = $"FIXTURE-{sequence}", RequestedBy = scope.User,
            ToConfigVersion = sequence, DeploymentSequence = sequence, ReleaseType = "publish", Status = "Succeeded" };
        var outbox = new OutboxMessage { EnvironmentId = scope.Environment, ReleaseId = release.Id, DeploymentSequence = sequence,
            EventType = "desired_config", CreatedAt = time };
        outbox.Payload = JsonSerializer.Serialize(new DeploymentEvent(outbox.Id, scope.Environment, sequence, release.Id, sequence), CanonicalJson.Options);
        db.AddRange(version, release, outbox, new GatewayConfigSnapshot { ConfigVersionId = version.Id,
            Payload = Encoding.UTF8.GetString(payload), PayloadBytes = payload, SizeBytes = payload.Length });
        var environment = await db.Set<EnvironmentRecord>().SingleAsync(item => item.Id == scope.Environment);
        environment.DeploymentSequence = sequence;
        environment.DesiredConfigVersion = sequence;
        await db.SaveChangesAsync();
        return new(release.Id, sequence, sequence, hash, payload.Length);
    }

    private static async Task DisableAsync(WebApiDbContext db, FixtureScope scope, string transition)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
        switch (transition)
        {
            case "environment":
                await db.Set<EnvironmentRecord>().Where(item => item.Id == scope.Environment)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, "Disabled"));
                break;
            case "project":
                await db.Set<Project>().Where(item => item.Id == scope.Project)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, "Disabled"));
                break;
            case "organization":
                await db.Set<Organization>().Where(item => item.Id == scope.Organization)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, "Disabled"));
                break;
            case "cleared-pointer":
                await db.Set<EnvironmentRecord>().Where(item => item.Id == scope.Environment)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.DesiredConfigVersion, (long?)null));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition));
        }
        await transaction.CommitAsync();
    }

    private sealed class PauseAfterClaim : DbTransactionInterceptor
    {
        public TaskCompletionSource ClaimCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) =>
            ClaimCommitted.TrySetResult() ? Resume.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
    }
}

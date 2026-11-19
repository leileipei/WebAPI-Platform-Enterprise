using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[Collection("MigrationIsolation")]
public sealed class LegacyEnvironmentInitializationTests
{
    [Fact]
    public async Task EmptyGovernanceTablesDoNotAuthorizeLegacyInitialization()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedScopeAsync();
        await using (var setup = api.Context())
        {
            var role = await setup.Set<UserRole>().SingleAsync(row => row.UserId == api.User.Id);
            var permission = new Permission { Code = "gateway.config.read", Module = "gateway", Name = "Read gateway configuration" };
            setup.AddRange(permission, new RolePermission { RoleId = role.RoleId, PermissionId = permission.Id });
            await setup.SaveChangesAsync();
        }

        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var path = $"/api/v1/environments/{api.Environment.Id}/legacy-initialization";
        using var preview = await api.Client.GetAsync(path + "/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal("Unknown", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("previewHash").ValueKind);

        using var confirm = await api.WriteAsync(HttpMethod.Post, path + "/confirm",
            new { previewHash = new string('a', 64), reason = "Isolated historical evidence review" }, "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        await using var db = api.Context();
        Assert.Empty(await db.Set<EnvironmentSecurityState>().ToArrayAsync());
        Assert.Empty(await db.Set<EnvironmentSecurityInitialization>().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedHistoryAtomicallyEstablishesTheLegacyOriginAndGenesis(bool published)
    {
        await using var database = new PostgresDatabase();
        var fixture = await SeedMainlineEnvironmentAsync(database, published);
        var reader = new HistoryReader(fixture.Evidence);

        var preview = await PreviewAsync(database, reader, fixture);
        Assert.Equal("Unknown", preview.Value.State);
        Assert.Equal(published ? 3 : 0, preview.Value.ReviewedNodeInstances);
        Assert.Equal(fixture.Evidence.Publication.SnapshotHash, preview.Value.SnapshotHash);
        Assert.NotNull(preview.Value.PreviewHash);
        await AssertUnknownAsync(database, fixture);

        var confirmed = await ConfirmAsync(database, reader, fixture, preview);
        Assert.Equal("VerifiedLegacyNetwork", confirmed.Value.State);
        Assert.Equal("\"2\"", confirmed.ETag);

        await using var db = database.Context();
        var origin = await db.Set<EnvironmentSecurityInitialization>().SingleAsync();
        Assert.Equal("VerifiedLegacyNetwork", origin.State);
        Assert.Equal(preview.Value.EvidenceHash, origin.EvidenceHash);
        Assert.Equal(2, origin.Revision);
        Assert.Equal(fixture.Actor.UserId, origin.ConfirmedBy);
        Assert.Equal("Isolated historical review", origin.ConfirmationReason);
        Assert.Equal(2, (await db.Set<EnvironmentRecord>().SingleAsync()).Revision);
        var state = await new SecurityLedgerReader(db).ReadCompleteAsync(fixture.EnvironmentId);
        Assert.Equal(0, state.Epoch);
        Assert.Equal(SecurityEventHash.Genesis, state.HeadHash);
        Assert.Equal("2.0", state.MinimumSnapshotSchema);
        Assert.False(state.RequireEnvironmentCredentials);
        Assert.Empty(state.Events);
        var audit = await db.Set<AuditLog>().SingleAsync(item => item.Action == LegacyEnvironmentInitializationService.AuditAction);
        Assert.Equal(fixture.Actor.UserId, audit.UserId);
        Assert.Equal(fixture.EnvironmentId, audit.EnvironmentId);
        Assert.Contains(origin.EvidenceHash!, audit.AfterJson);
        Assert.Contains("VerifiedLegacyNetwork", audit.AfterJson);
        Assert.Equal(2, reader.Disposed);
        if (published)
        {
            var node = await db.Set<GatewayNode>().AsNoTracking().FirstAsync();
            var registry = new NodeRegistry(db, new NodeIdentityService(db, new([])));
            var desired = await registry.DesiredAsync(new(node.Id, node.EnvironmentId, node.IdentityHash), instanceId: Guid.Parse(node.InstanceId));
            Assert.Equal(fixture.Evidence.Publication.SnapshotHash, desired?.Envelope.PayloadHash);
        }
    }

    [Theory]
    [InlineData("retired-node", "legacy_initialization_history_unverified")]
    [InlineData("high-watermark", "legacy_initialization_history_unverified")]
    [InlineData("recovery-gap", "legacy_initialization_history_unverified")]
    [InlineData("changed-sha", "legacy_initialization_history_unverified")]
    [InlineData("corrupt-published-bytes", "legacy_initialization_release_unverified")]
    [InlineData("failed-governance-deployment", "legacy_initialization_release_unverified")]
    [InlineData("changed-manifest", "legacy_initialization_preview_changed")]
    [InlineData("changed-node-membership", "legacy_initialization_preview_changed")]
    [InlineData("lost-fence", "legacy_initialization_history_unverified")]
    [InlineData("stale-revision", "stale_revision")]
    [InlineData("stale-session", "authentication_required")]
    [InlineData("lost-gateway-permission", "permission_denied")]
    [InlineData("lost-environment-permission", "permission_denied")]
    public async Task ConfirmationRejectsChangedEvidenceOrAuthorityAndRollsBack(string fault, string code)
    {
        await using var database = new PostgresDatabase();
        var fixture = await SeedMainlineEnvironmentAsync(database, published: true);
        var reader = new HistoryReader(fixture.Evidence);
        var preview = await PreviewAsync(database, reader, fixture);
        Assert.NotNull(preview.Value.PreviewHash);

        var evidence = fixture.Evidence;
        reader.Evidence = fault switch
        {
            "retired-node" => evidence with { Nodes = evidence.Nodes.Take(2).ToArray() },
            "high-watermark" => evidence with { Nodes = evidence.Nodes.Select(node => node with
                { HistoryState = LegacyNodeHistoryState.GovernanceHistoryObserved }).ToArray() },
            "recovery-gap" => evidence with { RecoveryHistoryComplete = false },
            "changed-sha" => evidence with { Publication = evidence.Publication with { SnapshotHash = new string('e', 64) } },
            "changed-manifest" => evidence with { DeploymentManifestHash = new string('e', 64) },
            _ => evidence
        };
        if (fault == "lost-fence") reader.AvailableChecks = 1;
        await using (var setup = database.Context())
        {
            if (fault == "stale-revision")
                await setup.Set<EnvironmentRecord>().ExecuteUpdateAsync(update => update.SetProperty(row => row.Revision, row => row.Revision + 1));
            if (fault == "stale-session")
                await setup.Set<UserRecord>().ExecuteUpdateAsync(update => update.SetProperty(row => row.SecurityStamp, "new-authorization-stamp"));
            if (fault == "corrupt-published-bytes")
                await setup.Set<GatewayConfigSnapshot>().ExecuteUpdateAsync(update => update.SetProperty(row => row.PayloadBytes, new byte[] { 1, 2, 3 }));
            if (fault == "failed-governance-deployment")
            {
                var version = evidence.Publication.ConfigVersion + 1;
                var payload = CanonicalJson.Serialize(new RuntimeSnapshot("2.4", evidence.Source.EnvironmentId, version,
                    DateTimeOffset.UtcNow, [], [], [], [], Guid.NewGuid(), 0, false, "2.0"));
                var config = new GatewayConfigVersion { EnvironmentId = evidence.Source.EnvironmentId, VersionNo = version,
                    Status = "Failed", SnapshotHash = Convert.ToHexStringLower(SHA256.HashData(payload)), CreatedBy = fixture.Actor.UserId };
                setup.AddRange(config, new GatewayConfigSnapshot { ConfigVersionId = config.Id, PayloadBytes = payload,
                    Payload = Encoding.UTF8.GetString(payload), SizeBytes = payload.Length });
                await setup.SaveChangesAsync();
            }
            if (fault == "changed-node-membership")
                await setup.Set<GatewayNode>().Where(row => row.Id == evidence.Nodes[0].NodeId)
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.Enabled, false));
            if (fault is "lost-gateway-permission" or "lost-environment-permission")
            {
                var permission = fault == "lost-gateway-permission" ? "gateway.config.read" : "environment.write";
                var permissionId = await setup.Set<Permission>().Where(row => row.Code == permission).Select(row => row.Id).SingleAsync();
                await setup.Set<RolePermission>().Where(row => row.PermissionId == permissionId).ExecuteDeleteAsync();
            }
        }

        var error = await Assert.ThrowsAsync<ApiException>(() => ConfirmAsync(database, reader, fixture, preview));
        Assert.Equal(code, error.Code);
        await AssertUnknownAsync(database, fixture, fault == "stale-revision" ? 2 : 1);
    }

    [Theory]
    [InlineData("write-failed")]
    [InlineData("fence-check-failed")]
    [InlineData("commit-reply-lost")]
    public async Task StopWriteFenceReleasesOnlyAfterLocalOutcomeIsDefinite(string failure)
    {
        await using var database = new PostgresDatabase();
        var fixture = await SeedMainlineEnvironmentAsync(database, published: false);
        var reader = new HistoryReader(fixture.Evidence);
        var preview = await PreviewAsync(database, reader, fixture);
        bool? localLockFreeAtRelease = null;
        reader.OnDispose = async () =>
        {
            await using var connection = await database.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_try_advisory_lock(8901202)", connection);
            localLockFreeAtRelease = (bool)(await command.ExecuteScalarAsync())!;
        };
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
        if (failure == "commit-reply-lost") options.AddInterceptors(new LoseCommitReply());
        if (failure == "write-failed") options.AddInterceptors(new FailGenesisWrite());
        if (failure == "fence-check-failed") reader.AvailableChecks = 1;
        await using (var db = new WebApiDbContext(options.Options))
        {
            var action = () => Service(db, reader).ConfirmAsync(fixture.EnvironmentId,
                new(preview.Value.PreviewHash!, "Isolated historical review"), preview.ETag, fixture.Actor, CurrentSession(fixture));
            if (failure == "write-failed") await Assert.ThrowsAsync<IOException>(action);
            else
            {
                var error = await Assert.ThrowsAsync<ApiException>(action);
                Assert.Equal(failure == "commit-reply-lost" ? "legacy_initialization_fence_retained" : "legacy_initialization_history_unverified",
                    error.Code);
            }
        }

        if (failure == "commit-reply-lost")
        {
            Assert.Null(localLockFreeAtRelease);
            Assert.Equal(1, reader.Disposed);
            await using var verify = database.Context();
            Assert.Equal("VerifiedLegacyNetwork", (await verify.Set<EnvironmentSecurityInitialization>().SingleAsync()).State);
        }
        else
        {
            Assert.True(localLockFreeAtRelease);
            Assert.Equal(2, reader.Disposed);
            await AssertUnknownAsync(database, fixture);
        }
    }

    private sealed class LoseCommitReply : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => throw new IOException("Isolated commit confirmation lost.");
    }

    private sealed class FailGenesisWrite : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<EnvironmentSecurityState>().Any(row => row.State == EntityState.Added))
                throw new IOException("Isolated genesis write failed.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task ACloneCannotReuseTheOriginalDatabaseReceiptAndEvidence()
    {
        await using var database = new PostgresDatabase();
        var fixture = await SeedMainlineEnvironmentAsync(database, published: false);
        NpgsqlConnection.ClearAllPools();
        await using var clone = new PostgresDatabase();
        await clone.InitializeFromTemplateAsync(database.Name);
        var reader = new HistoryReader(fixture.Evidence);

        var preview = await PreviewAsync(clone, reader, fixture);

        Assert.Equal("Unknown", preview.Value.State);
        Assert.Equal("legacy_initialization_source_unverified", preview.Value.StatusCode);
        Assert.Null(preview.Value.PreviewHash);
        Assert.Equal(0, reader.Disposed);
        await AssertUnknownAsync(clone, fixture);
    }

    [Fact]
    public async Task VerifiedLegacyGenesisDoesNotAuthorizeAnUnwitnessedLease()
    {
        await using var database = new PostgresDatabase();
        var fixture = await SeedMainlineEnvironmentAsync(database, published: true);
        var reader = new HistoryReader(fixture.Evidence);
        var preview = await PreviewAsync(database, reader, fixture);
        Assert.Equal("VerifiedLegacyNetwork", (await ConfirmAsync(database, reader, fixture, preview)).Value.State);

        await using var db = database.Context();
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        await IsolatedGovernanceJournalSchema.InstallAsync(db, journal);
        var witness = new UnenrolledHistory();
        var continuity = new PostgresGovernanceRecoveryContinuity(journal, witness, witness);
        var issuer = new SecurityLeaseIssuer(db, new NodeIdentityService(db, new([])), new SecurityLedgerReader(db), new(null, null),
            continuity, witness, new(null!, null!, new("time", "time-key", "isolated-time-key", "history", "history-key", "isolated-history-key")),
            TimeProvider.System);
        var node = await db.Set<GatewayNode>().AsNoTracking().FirstAsync();

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            issuer.IssueAsync(new(node.Id, node.EnvironmentId, node.IdentityHash), Guid.Parse(node.InstanceId)));

        Assert.Equal("security_evidence_unavailable", error.Code);
        Assert.Equal(1, witness.HistoryReads);
        Assert.Empty(await db.Set<GatewaySecurityLease>().ToArrayAsync());
        Assert.Empty(await db.Set<GatewaySecurityLeaseIssuance>().ToArrayAsync());
    }

    private static async Task<Fixture> SeedMainlineEnvironmentAsync(PostgresDatabase database, bool published)
    {
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        var project = Guid.NewGuid();
        var environment = Guid.NewGuid();
        var user = new UserRecord { Username = "legacy-fixture", DisplayName = "Legacy fixture", SecurityStamp = "initial-authorization-stamp" };
        var publication = LegacyEnvironmentPublication.NeverPublished;
        var nodeProofs = new List<LegacyEnvironmentNodeHistory>();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'OLD','Old organization','Active',now(),now(),1)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at,revision) VALUES({project},{organization},'OLD','Old project','Active',now(),now(),1)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status,revision) VALUES({environment},{project},'OLD','Old environment',false,0,'Active',1)");
            db.Add(user);
            await db.SaveChangesAsync();
            if (published)
            {
                var release = new ReleaseRecord { EnvironmentId = environment, ReleaseNo = "LEGACY-TEST-1", ReleaseType = "publish",
                    ToConfigVersion = 4, DeploymentSequence = 1, Status = "Succeeded", RequestedBy = user.Id, CompletedAt = DateTimeOffset.UtcNow };
                var payload = CanonicalJson.Serialize(new RuntimeSnapshot("2.3", environment, release.ToConfigVersion, DateTimeOffset.UtcNow, [], [], [], []));
                var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
                var config = new GatewayConfigVersion { EnvironmentId = environment, VersionNo = release.ToConfigVersion,
                    Status = "Published", PublishedAt = release.CompletedAt, SnapshotHash = hash, CreatedBy = user.Id };
                db.AddRange(release, config, new GatewayConfigSnapshot { ConfigVersionId = config.Id,
                    PayloadBytes = payload, Payload = Encoding.UTF8.GetString(payload), SizeBytes = payload.Length });
                GatewayNode NewNode(string name) => new() { EnvironmentId = environment, NodeName = name, AppVersion = "legacy-fixture",
                    InstanceId = Guid.NewGuid().ToString("D"), CurrentConfigVersion = release.ToConfigVersion,
                    CurrentDeploymentSequence = release.DeploymentSequence!.Value, IdentityHash = Guid.NewGuid().ToString("N") };
                var first = NewNode("old-gateway-a");
                var second = NewNode("old-gateway-b");
                var retired = Guid.NewGuid().ToString("D");
                db.AddRange(first, second, new GatewayNodeEvent { GatewayNodeId = first.Id, EventType = "registered",
                    Detail = JsonSerializer.Serialize(new { instanceId = retired }) });
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE environments SET desired_config_version={release.ToConfigVersion}, deployment_sequence={release.DeploymentSequence} WHERE id={environment}");
                publication = new(release.Id, config.VersionNo, release.DeploymentSequence!.Value, payload.Length, hash);
                nodeProofs.AddRange(new[] { first, second }.Select(node => new LegacyEnvironmentNodeHistory(node.Id, node.InstanceId, new string('a', 64),
                    LegacyNodeHistoryState.StoppedWithoutGovernanceHistory, config.VersionNo, publication.DeploymentSequence, hash)));
                nodeProofs.Add(new(first.Id, retired, new string('b', 64), LegacyNodeHistoryState.StoppedWithoutGovernanceHistory, 0, 0, null));
            }
        }

        NpgsqlConnection.ClearAllPools();
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
        GovernanceMigrationRunner.Configure(options);
        await using (var runner = new WebApiDbContext(options.Options))
            await MigrationRunnerAssertions.RunAsync(runner, new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864));

        await using var current = database.Context();
        var role = new Role { Code = "LegacyReviewer", Name = "Legacy reviewer", OrganizationId = organization };
        current.AddRange(role, new UserRole { UserId = user.Id, RoleId = role.Id },
            new UserProjectScope { UserId = user.Id, OrganizationId = organization, ProjectId = project,
                EnvironmentId = environment, AccessMode = "read_write" });
        foreach (var code in new[] { "environment.write", "gateway.config.read" })
        {
            var permission = new Permission { Code = code, Module = code.Split('.')[0], Name = code };
            current.AddRange(permission, new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
        }
        await current.SaveChangesAsync();
        var receipt = await current.Set<EnvironmentSecuritySourceReceipt>().SingleAsync();
        var source = new LegacyEnvironmentSource(environment, project, organization,
            new(receipt.SourceClusterId, receipt.SourceDatabase, receipt.SourceDatabaseOid), receipt.SourceMigration,
            receipt.SourceSchemaHash, receipt.SourceHistoryHash, receipt.CapturedAt);
        return new(environment, new(user.Id, "legacy-initialization-fixture"), user.SecurityStamp,
            new(source, new string('a', 64), new string('b', 64), new string('c', 64), true, true, true, publication, nodeProofs));
    }

    private static async Task<CommandResult<LegacyEnvironmentInitializationPreview>> PreviewAsync(PostgresDatabase database,
        HistoryReader reader, Fixture fixture)
    {
        await using var db = database.Context();
        return await Service(db, reader).PreviewAsync(fixture.EnvironmentId, fixture.Actor, CurrentSession(fixture));
    }

    private static async Task<CommandResult<LegacyEnvironmentInitializationPreview>> ConfirmAsync(PostgresDatabase database,
        HistoryReader reader, Fixture fixture, CommandResult<LegacyEnvironmentInitializationPreview> preview)
    {
        await using var db = database.Context();
        return await Service(db, reader).ConfirmAsync(fixture.EnvironmentId,
            new(preview.Value.PreviewHash!, "Isolated historical review"), preview.ETag, fixture.Actor, CurrentSession(fixture));
    }

    private static LegacyEnvironmentInitializationService Service(WebApiDbContext db, HistoryReader reader) =>
        new(db, new AuthorizationService(db), new ScopeResolver(db), new AuditedCommandExecutor(db), new HistoricalSnapshotService(db, new ScopeResolver(db)), reader);

    private static Func<WebApiDbContext, CancellationToken, Task> CurrentSession(Fixture fixture) => async (db, ct) =>
    {
        if (!await db.Set<UserRecord>().AnyAsync(row => row.Id == fixture.Actor.UserId && row.SecurityStamp == fixture.Stamp, ct))
            throw new ApiException(401, "authentication_required", "Session expired.");
    };

    private static async Task AssertUnknownAsync(PostgresDatabase database, Fixture fixture, long revision = 1)
    {
        await using var db = database.Context();
        var origin = await db.Set<EnvironmentSecurityInitialization>().SingleAsync(row => row.EnvironmentId == fixture.EnvironmentId);
        Assert.Equal("Unknown", origin.State);
        Assert.Equal(1, origin.Revision);
        Assert.Null(origin.EvidenceHash);
        Assert.Equal(revision, (await db.Set<EnvironmentRecord>().SingleAsync()).Revision);
        Assert.Empty(await db.Set<EnvironmentSecurityState>().ToArrayAsync());
        Assert.Empty(await db.Set<AuditLog>().Where(item => item.Action == LegacyEnvironmentInitializationService.AuditAction).ToArrayAsync());
    }

    private sealed record Fixture(Guid EnvironmentId, ActorContext Actor, string Stamp, LegacyEnvironmentHistoryEvidence Evidence);

    private sealed class HistoryReader(LegacyEnvironmentHistoryEvidence evidence) : ILegacyEnvironmentHistoryReader
    {
        public LegacyEnvironmentHistoryEvidence Evidence { get; set; } = evidence;
        public int AvailableChecks { get; set; } = int.MaxValue;
        public int Disposed { get; private set; }
        public Func<Task>? OnDispose { get; set; }

        public Task<ILegacyEnvironmentHistoryFence?> OpenAsync(Guid environmentId, CancellationToken ct)
        {
            Assert.Equal(Evidence.Source.EnvironmentId, environmentId);
            return Task.FromResult<ILegacyEnvironmentHistoryFence?>(new Fence(this));
        }

        private sealed class Fence(HistoryReader reader) : ILegacyEnvironmentHistoryFence
        {
            private int checks;
            public LegacyEnvironmentHistoryEvidence Evidence => reader.Evidence;
            public Task<bool> IsHeldAsync(CancellationToken ct) => Task.FromResult(++checks <= reader.AvailableChecks);
            public async ValueTask DisposeAsync()
            {
                reader.Disposed++;
                if (reader.OnDispose is not null) await reader.OnDispose();
            }
        }
    }

    private sealed class UnenrolledHistory : IGovernanceJournalWitness, IGovernanceEnvironmentHistoryWitness, IGovernanceLeaseReservationWitness
    {
        internal int HistoryReads { get; private set; }

        public Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId journal, Guid? operationId, CancellationToken ct) =>
            Task.FromResult(new GovernanceWitnessState(GovernanceJournalProtocol.Genesis, null, null));

        public Task<GovernanceEnvironmentHistory> ObserveAsync(GovernanceJournalId journal, GovernanceJournalPosition position,
            Guid environmentId, CancellationToken ct)
        {
            HistoryReads++;
            return Task.FromResult(new GovernanceEnvironmentHistory(journal, position, environmentId, "Unknown", null));
        }

        public Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<VerifiedGovernanceLeaseReservation> ReserveAllowLeaseAsync(GovernanceJournalId journal,
            GovernanceJournalPosition position, GovernanceLeaseReservation reservation, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}

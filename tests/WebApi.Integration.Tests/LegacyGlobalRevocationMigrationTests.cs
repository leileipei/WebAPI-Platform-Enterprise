using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Persistence.Migrations;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[Collection("MigrationIsolation")]
public sealed class LegacyGlobalRevocationMigrationTests
{
    private const string Archived = "CredentialRevokedArchived";
    private const string Inherited = "CredentialRevocationInherited";
    private static readonly string Seal = "\"" + new string('a', 64) + "\"";

    [Fact]
    public async Task AnArchivedOriginAndItsInheritedTombstoneCanCommitInOneTransaction()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.GetService<IMigrator>().MigrateAsync(LegacyGlobalRevocationInheritance.Id);
        var fixture = await SeedAsync(db);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await TakeGovernanceFenceAsync(db);
            await AddFactAsync(db, fixture, fixture.Archive, Archived, Seal);
            await AddFactAsync(db, fixture, fixture.Target, Inherited);
            await tx.CommitAsync();
        }

        var events = await db.Set<SecurityEvent>().AsNoTracking().ToArrayAsync();
        Assert.Equal(2, events.Length);
        Assert.Contains(events, item => item.EventType == Archived && item.EnvironmentId == fixture.Archive.EnvironmentId && item.PolicyJson == Seal);
        Assert.Contains(events, item => item.EventType == Inherited && item.EnvironmentId == fixture.Target.EnvironmentId && item.PolicyJson is null);
        Assert.All(events, item => Assert.Equal(fixture.Operation.Id, item.OperationId));
        var tombstones = await db.Set<CredentialRevocation>().AsNoTracking().ToArrayAsync();
        Assert.Equal(2, tombstones.Length);
        Assert.All(tombstones, item => Assert.Equal((fixture.Operation.Id, fixture.Credential.Id, fixture.Operation.CommittedAt),
            (item.OperationId, item.CredentialId, item.RevokedAt)));

        var mutation = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.security_events SET policy_json=NULL WHERE environment_id={fixture.Archive.EnvironmentId}
            """));
        Assert.Equal("security_fact_immutable", mutation.MessageText);
        var deletion = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM public.credential_revocations WHERE environment_id={fixture.Target.EnvironmentId}
            """));
        Assert.Equal("security_fact_immutable", deletion.MessageText);
        var downgrade = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(CredentialGovernanceJournal.Id));
        Assert.Equal("legacy_global_revocation_inheritance_down_blocked", downgrade.MessageText);
        Assert.Equal(LegacyGlobalRevocationInheritance.Id, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(2, await db.Set<SecurityEvent>().CountAsync());
        Assert.Equal(2, await db.Set<CredentialRevocation>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothInstallerUpgradesPreserveAnOriginalRevocationAndPermitItsInheritance(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        LedgerFixture fixture;
        string originalHash;
        await using (var seed = database.Context())
        {
            await seed.GetService<IMigrator>().MigrateAsync(CredentialGovernanceJournal.Id);
            var predecessor = await seed.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync();
            Assert.Equal(GovernanceSchemaFingerprints.Expected[CredentialGovernanceJournal.Id], predecessor);
            fixture = await SeedAsync(seed);
            await AddFactAsync(seed, fixture, fixture.Archive, "CredentialRevoked");
            originalHash = (await seed.Set<SecurityEvent>().AsNoTracking().SingleAsync()).EventHash;
        }
        await InstallUpgradeAsync(database, sqlEntry);

        await using var db = database.Context();
        Assert.Equal(GovernanceSchemaFingerprints.Expected[FormalCredentialRotationSchedule.Id],
            await db.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        Assert.Equal(originalHash, (await db.Set<SecurityEvent>().AsNoTracking().SingleAsync()).EventHash);
        Assert.True(await db.Set<CredentialRevocation>().AnyAsync(item => item.CredentialId == fixture.Credential.Id &&
            item.EnvironmentId == fixture.Archive.EnvironmentId && item.OperationId == fixture.Operation.Id));

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await TakeGovernanceFenceAsync(db);
            var target = await db.Set<EnvironmentSecurityState>().SingleAsync(state => state.EnvironmentId == fixture.Target.EnvironmentId);
            await AddFactAsync(db, fixture, target, Inherited);
            await tx.CommitAsync();
        }
        Assert.Equal(2, await db.Set<CredentialRevocation>().CountAsync(item => item.OperationId == fixture.Operation.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradedGatewayRetirementHistoryIsAppendOnly(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        Guid nodeId;
        long retainedId;
        long ordinaryId;
        long registrationId;
        await using (var seed = database.Context())
        {
            await seed.GetService<IMigrator>().MigrateAsync(CredentialGovernanceJournal.Id);
            var fixture = await SeedAsync(seed);
            var node = new GatewayNode { EnvironmentId = fixture.Archive.EnvironmentId, NodeName = "archived-gateway",
                InstanceId = Guid.NewGuid().ToString(), AppVersion = "fixture", IdentityHash = new string('c', 64), Enabled = false };
            var retained = new GatewayNodeEvent { GatewayNodeId = node.Id, EventType = "environment_archive_retired", Message = "Retained retirement" };
            var ordinary = new GatewayNodeEvent { GatewayNodeId = node.Id, EventType = "status", Message = "Ordinary event" };
            var registration = new GatewayNodeEvent { GatewayNodeId = node.Id, EventType = "registered", Message = "Registration" };
            seed.AddRange(node, retained, ordinary, registration);
            await seed.SaveChangesAsync();
            (nodeId, retainedId, ordinaryId, registrationId) = (node.Id, retained.Id, ordinary.Id, registration.Id);
        }
        await InstallUpgradeAsync(database, sqlEntry);
        await using var db = database.Context();
        var appended = new GatewayNodeEvent { GatewayNodeId = nodeId, EventType = "environment_archive_retired", Message = "New retirement" };
        db.Add(appended);
        await db.SaveChangesAsync();

        await AssertGuardAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.gateway_node_events SET message='changed' WHERE id={retainedId}"),
            "gateway_archive_retirement_immutable");
        await AssertGuardAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM public.gateway_node_events WHERE id={appended.Id}"),
            "gateway_archive_retirement_immutable");
        await AssertGuardAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.gateway_node_events SET event_type='environment_archive_retired' WHERE id={ordinaryId}"),
            "gateway_archive_retirement_immutable");
        await AssertGuardAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM public.gateway_node_events WHERE id={registrationId}"),
            "gateway_registration_history_immutable");
        await AssertGuardAsync(() => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE public.gateway_node_events"),
            "gateway_registration_history_immutable");

        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.gateway_node_events SET message='Allowed' WHERE id={ordinaryId}"));
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM public.gateway_node_events WHERE id={ordinaryId}"));
        Assert.Equal("Retained retirement", await db.Set<GatewayNodeEvent>().Where(row => row.Id == retainedId).Select(row => row.Message).SingleAsync());
        Assert.Equal(2, await db.Set<GatewayNodeEvent>().CountAsync(row => row.EventType == "environment_archive_retired"));
    }

    [Theory]
    [InlineData("no-fence", "security_legacy_global_revocation_fence_required")]
    [InlineData("no-origin", "security_legacy_global_revocation_origin_missing")]
    [InlineData("active", "security_archived_revocation_ingress_unsealed")]
    [InlineData("enabled-node", "security_archived_revocation_ingress_unsealed")]
    [InlineData("stale-isolation", "security_archived_revocation_requires_read_committed")]
    [InlineData("null-policy", "ck_security_event")]
    [InlineData("object-policy", "ck_security_event")]
    [InlineData("uppercase-policy", "ck_security_event")]
    [InlineData("old-revocation-policy", "ck_security_event")]
    public async Task DirectEventWritesRejectInvalidInheritanceOrArchiveEvidence(string failure, string expected)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var fixture = await SeedAsync(db);
        if (failure == "enabled-node")
        {
            db.Add(new GatewayNode { EnvironmentId = fixture.Archive.EnvironmentId, NodeName = "registered", InstanceId = Guid.NewGuid().ToString(),
                AppVersion = "fixture", IdentityHash = new string('c', 64), Enabled = true });
            await db.SaveChangesAsync();
        }
        var isolation = failure == "stale-isolation" ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted;
        await using (var tx = await db.Database.BeginTransactionAsync(isolation))
        {
            if (failure != "no-fence") await TakeGovernanceFenceAsync(db);
            var state = failure is "active" or "no-origin" ? fixture.Target : fixture.Archive;
            var kind = failure switch { "no-origin" => Inherited, "old-revocation-policy" => "CredentialRevoked", _ => Archived };
            var policy = failure switch
            {
                "no-origin" or "null-policy" => null,
                "object-policy" => "{}",
                "uppercase-policy" => Seal.ToUpperInvariant(),
                _ => Seal
            };
            var eventRow = fixture.Event(state, kind, policy);
            var result = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public.security_events (environment_id, organization_id, project_id, security_generation, epoch,
                    operation_id, event_type, credential_id, policy_json, previous_hash, event_hash, occurred_at)
                VALUES ({eventRow.EnvironmentId}, {eventRow.OrganizationId}, {eventRow.ProjectId}, {eventRow.SecurityGeneration}, {eventRow.Epoch},
                    {eventRow.OperationId}, {eventRow.EventType}, {eventRow.CredentialId}, CAST({eventRow.PolicyJson} AS jsonb),
                    {eventRow.PreviousHash}, {eventRow.EventHash}, {eventRow.OccurredAt})
                """));
            Assert.Equal(PostgresErrorCodes.CheckViolation, result.SqlState);
            Assert.Equal(expected, result.ConstraintName ?? result.MessageText);
            await tx.RollbackAsync();
        }
        Assert.False(await db.Set<SecurityEvent>().AnyAsync());
        Assert.False(await db.Set<CredentialRevocation>().AnyAsync());
    }

    private static Task TakeGovernanceFenceAsync(WebApiDbContext db) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");

    private static async Task AssertGuardAsync(Func<Task> action, string expected)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(expected, error.MessageText);
    }

    private static async Task InstallUpgradeAsync(PostgresDatabase database, bool sqlEntry)
    {
        NpgsqlConnection.ClearAllPools();
        if (sqlEntry)
        {
            var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
            var script = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "initial-core.sql"));
            await using var connection = await database.OpenAsync();
            await using var configure = new NpgsqlCommand("SELECT pg_catalog.set_config('webapi.migration_transaction_seconds','900',false),pg_catalog.set_config('webapi.migration_receipt_max_rows','100000',false)", connection);
            await configure.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync();
            return;
        }
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
        GovernanceMigrationRunner.Configure(options);
        await using var runner = new WebApiDbContext(options.Options);
        await MigrationRunnerAssertions.RunAsync(runner, new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864));
    }

    private static async Task AddFactAsync(WebApiDbContext db, LedgerFixture fixture, EnvironmentSecurityState state,
        string eventType, string? evidence = null)
    {
        var item = fixture.Event(state, eventType, evidence);
        db.AddRange(item, new CredentialRevocation { EnvironmentId = item.EnvironmentId, OrganizationId = item.OrganizationId,
            ProjectId = item.ProjectId, SecurityGeneration = item.SecurityGeneration, Epoch = item.Epoch, OperationId = item.OperationId,
            CredentialId = fixture.Credential.Id, ApplicationId = fixture.Credential.ApplicationId, RevokedAt = item.OccurredAt });
        state.Epoch = item.Epoch;
        state.HeadHash = item.EventHash;
        state.Revision++;
        await db.SaveChangesAsync();
    }

    private static async Task<LedgerFixture> SeedAsync(WebApiDbContext db)
    {
        var org = new Organization { Code = "MIG", Name = "Migration fixture" };
        var project = new Project { OrganizationId = org.Id, Code = "PROJECT", Name = "Migration fixture" };
        var archive = new EnvironmentRecord { ProjectId = project.Id, Code = "ARCHIVE", Name = "Archive", Status = "Disabled" };
        var target = new EnvironmentRecord { ProjectId = project.Id, Code = "TARGET", Name = "Target" };
        var app = new ApplicationRecord { OrganizationId = org.Id, ProjectId = project.Id, Code = "APP", Name = "Migration fixture" };
        var actor = new UserRecord { Username = "migration_fixture", DisplayName = "Migration fixture" };
        var at = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT date_trunc('second', clock_timestamp()) AS \"Value\"").SingleAsync();
        var credential = new ApplicationCredential { ApplicationId = app.Id, OrganizationId = org.Id, AccessKey = "migration-fixture-key",
            SecretHash = new string('b', 64), ValidFrom = at.AddHours(-1), ExpiresAt = at.AddHours(1), Status = "Revoked", RevokedAt = at };
        var operation = new SecurityOperation { OrganizationId = org.Id, ApplicationId = app.Id, CredentialId = credential.Id,
            RequestedBy = actor.Id, Reason = "Migration fixture", CommittedAt = at };
        db.AddRange(org, project, archive, target, app, actor, credential, operation);
        db.AddRange(new[] { archive, target }.Select(env => new EnvironmentSecurityInitialization { EnvironmentId = env.Id,
            OrganizationId = org.Id, ProjectId = project.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() }));
        await db.SaveChangesAsync();

        EnvironmentSecurityState State(EnvironmentRecord env) => new() { EnvironmentId = env.Id, ProjectId = project.Id,
            OrganizationId = org.Id, UpdatedAt = at };
        var archiveState = State(archive);
        var targetState = State(target);
        db.AddRange(archiveState, targetState);
        await db.SaveChangesAsync();
        return new(operation, credential, archiveState, targetState);
    }

    private sealed record LedgerFixture(SecurityOperation Operation, ApplicationCredential Credential,
        EnvironmentSecurityState Archive, EnvironmentSecurityState Target)
    {
        internal SecurityEvent Event(EnvironmentSecurityState state, string kind, string? evidence = null)
        {
            var item = new SecurityEvent { EnvironmentId = state.EnvironmentId, OrganizationId = state.OrganizationId,
                ProjectId = state.ProjectId, SecurityGeneration = state.SecurityGeneration, Epoch = state.Epoch + 1,
                OperationId = Operation.Id, EventType = kind, CredentialId = Credential.Id, PolicyJson = evidence,
                PreviousHash = state.HeadHash, OccurredAt = Operation.CommittedAt };
            item.EventHash = SecurityEventHash.Compute(item);
            return item;
        }
    }
}

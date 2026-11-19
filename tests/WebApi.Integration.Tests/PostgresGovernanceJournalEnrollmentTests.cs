using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PostgresGovernanceJournalEnrollmentTests
{
    [Fact]
    public async Task AuthorizedGenesisEnrollmentAndReplayVerifyTheWitnessUnderTheWriterFence()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        await using var contender = await database.OpenAsync();
        var journal = NewJournal();
        var witness = new GenesisWitness(journal);
        var authorizations = 0;
        witness.OnObserve = async ct =>
        {
            Assert.Equal(witness.Observations, authorizations);
            Assert.False(await CanTakeWriterFenceAsync(contender, ct));
        };

        async Task Authorize(WebApiDbContext fenced, CancellationToken ct)
        {
            Assert.NotNull(fenced.Database.CurrentTransaction);
            Assert.False(await CanTakeWriterFenceAsync(contender, ct));
            authorizations++;
        }

        var enrollment = new PostgresGovernanceJournalEnrollment(database.ConnectionString, journal, witness);
        Assert.True(await enrollment.EnrollAsync(Authorize));
        await db.Database.MigrateAsync();
        Assert.False(await enrollment.EnrollAsync(Authorize));

        Assert.Equal(2, witness.Observations);
        Assert.Equal(1L, await RootCountAsync(db));
        Assert.Equal(journal.AuthorityId, await db.Database.SqlQueryRaw<Guid>(
            "SELECT authority_id AS \"Value\" FROM credential_governance.journal_root").SingleAsync());
        Assert.Equal(journal.Generation, await db.Database.SqlQueryRaw<Guid>(
            "SELECT generation AS \"Value\" FROM credential_governance.journal_root").SingleAsync());
        Assert.Equal(GovernanceJournalProtocol.Genesis.Hash, await db.Database.SqlQueryRaw<string>(
            "SELECT genesis_hash AS \"Value\" FROM credential_governance.journal_root").SingleAsync());
        Assert.True(await CanTakeWriterFenceAsync(contender, default));
    }

    [Fact]
    public async Task PermissionDenialNeverObservesTheWitnessOrWritesTheRoot()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var journal = NewJournal();
        var witness = new GenesisWitness(journal);
        var enrollment = new PostgresGovernanceJournalEnrollment(database.ConnectionString, journal, witness);

        var denied = new ApiException(403, "permission_denied", "denied");
        var actual = await Assert.ThrowsAsync<ApiException>(() => enrollment.EnrollAsync((_, _) => Task.FromException(denied)));

        Assert.Same(denied, actual);
        Assert.Equal(0, witness.Observations);
        Assert.Equal(0L, await RootCountAsync(db));
    }

    [Fact]
    public async Task DriftedJournalSchemaDoesNotObserveTheWitness()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE credential_governance.journal_root DISABLE TRIGGER root_requires_fence");
        var journal = NewJournal();
        var witness = new GenesisWitness(journal);
        var enrollment = new PostgresGovernanceJournalEnrollment(database.ConnectionString, journal, witness);

        var error = await Assert.ThrowsAsync<ApiException>(() => enrollment.EnrollAsync(Allow));

        Assert.Equal(503, error.Status);
        Assert.Equal("credential_governance_schema_unavailable", error.Code);
        Assert.Equal(0, witness.Observations);
        Assert.Equal(0L, await RootCountAsync(db));
    }

    [Theory]
    [InlineData("advanced")]
    [InlineData("pending")]
    [InlineData("confirmation")]
    [InlineData("wrong-genesis")]
    [InlineData("unavailable")]
    public async Task NoninitialOrUnavailableWitnessDoesNotEnroll(string state)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var journal = NewJournal();
        var witness = new GenesisWitness(journal);
        var genesis = GovernanceJournalProtocol.Genesis;
        var intent = new GovernanceWriteIntent(Guid.NewGuid(), genesis, "YQ");
        var next = GovernanceJournalProtocol.NextPosition(journal, intent);
        witness.State = state switch
        {
            "advanced" => new(next, null, null),
            "pending" => new(genesis, intent, null),
            "confirmation" => new(genesis, null, new(intent.OperationId, next)),
            "wrong-genesis" => new(genesis with { Hash = next.Hash }, null, null),
            _ => witness.State
        };
        if (state == "unavailable")
            witness.OnObserve = _ => Task.FromException(new InvalidDataException("unverified witness response"));

        var enrollment = new PostgresGovernanceJournalEnrollment(database.ConnectionString, journal, witness);
        var error = await Assert.ThrowsAsync<ApiException>(() => enrollment.EnrollAsync(Allow));

        Assert.Equal(state == "unavailable" ? 503 : 409, error.Status);
        Assert.Equal(0L, await RootCountAsync(db));
        Assert.Equal(1, witness.Observations);
    }

    [Fact]
    public async Task ExistingRootIsNeverReboundAndAdvancedJournalNeverReenrolled()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var journal = NewJournal();
        var witness = new GenesisWitness(journal);
        var enrollment = new PostgresGovernanceJournalEnrollment(database.ConnectionString, journal, witness);
        Assert.True(await enrollment.EnrollAsync(Allow));

        var different = journal with { Generation = Guid.NewGuid() };
        var otherWitness = new GenesisWitness(different);
        var conflicting = new PostgresGovernanceJournalEnrollment(database.ConnectionString, different, otherWitness);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => conflicting.EnrollAsync(Allow))).Status);
        Assert.Equal(0, otherWitness.Observations);

        var intent = new GovernanceWriteIntent(Guid.NewGuid(), GovernanceJournalProtocol.Genesis, "YQ");
        var next = GovernanceJournalProtocol.NextPosition(journal, intent);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO credential_governance.journal_entries
                    (sequence, is_confirmation, operation_id, previous_hash, position_hash, recovery_material)
                VALUES ({next.Sequence}, false, {intent.OperationId}, {intent.Previous.Hash}, {next.Hash}, {intent.RecoveryMaterial})
                """);
            await tx.CommitAsync();
        }

        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => enrollment.EnrollAsync(Allow))).Status);
        Assert.Equal(1, witness.Observations);
        Assert.Equal(1L, await RootCountAsync(db));
        Assert.Equal(1L, await db.Database.SqlQueryRaw<long>(
            "SELECT pg_catalog.count(*) AS \"Value\" FROM credential_governance.journal_entries").SingleAsync());
    }

    [Fact]
    public async Task ExistingSecurityFactWithoutJournalPreventsEnrollment()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var organization = new Organization { Code = "old-org", Name = "Old organization" };
        var user = new UserRecord { Username = "old-owner", DisplayName = "Old owner" };
        var operation = new SecurityOperation
        {
            Kind = "EnvironmentPolicyCutover", OrganizationId = organization.Id, RequestedBy = user.Id,
            Reason = "Existing operation under a different history", CommittedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(organization, user, operation);
        await db.SaveChangesAsync();
        var journal = NewJournal();
        var witness = new GenesisWitness(journal);
        var enrollment = new PostgresGovernanceJournalEnrollment(database.ConnectionString, journal, witness);

        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => enrollment.EnrollAsync(Allow))).Status);
        Assert.Equal(0, witness.Observations);
        Assert.Equal(0L, await RootCountAsync(db));
        Assert.True(await db.Set<SecurityOperation>().AnyAsync(x => x.Id == operation.Id));
    }

    private static GovernanceJournalId NewJournal() => new(Guid.NewGuid(), Guid.NewGuid());

    private static Task Allow(WebApiDbContext db, CancellationToken ct) => Task.CompletedTask;

    private static Task<long> RootCountAsync(WebApiDbContext db) => db.Database.SqlQueryRaw<long>(
        "SELECT pg_catalog.count(*) AS \"Value\" FROM credential_governance.journal_root").SingleAsync();

    private static async Task<bool> CanTakeWriterFenceAsync(NpgsqlConnection contender, CancellationToken ct)
    {
        await using var tx = await contender.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_try_advisory_xact_lock(8901202)", contender, tx);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private sealed class GenesisWitness(GovernanceJournalId journal) : IGovernanceJournalWitness
    {
        public GovernanceWitnessState State { get; set; } = new(GovernanceJournalProtocol.Genesis, null, null);
        public Func<CancellationToken, Task>? OnObserve { get; set; }
        public int Observations { get; private set; }

        public async Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId observed, Guid? operationId, CancellationToken ct)
        {
            Assert.Equal(journal, observed);
            Assert.Null(operationId);
            Observations++;
            if (OnObserve is { } onObserve) await onObserve(ct);
            return State;
        }

        public Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId observed, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId observed, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}

using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PostgresGovernanceCommitStoreTests
{
    [Fact]
    public async Task EmptyJournalRequiresAnInstalledMatchingRoot()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var store = new PostgresGovernanceCommitStore(database.ConnectionString, new NoCommittedMutation());

        await using (var session = await store.OpenLockedAsync(journal, default))
            await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadAsync(default));

        await IsolatedGovernanceJournalSchema.InstallAsync(db, journal);

        await using (var session = await store.OpenLockedAsync(journal, default))
            Assert.Equal(new GovernanceLocalJournalState(GovernanceJournalProtocol.Genesis, null), await session.ReadAsync(default));

        await using (var session = await store.OpenLockedAsync(journal with { Generation = Guid.NewGuid() }, default))
            await Assert.ThrowsAsync<InvalidDataException>(() => session.ReadAsync(default));
    }

    [Theory]
    [InlineData("UPDATE credential_governance.journal_root SET generation = generation")]
    [InlineData("DELETE FROM credential_governance.journal_root")]
    [InlineData("TRUNCATE credential_governance.journal_root")]
    [InlineData("TRUNCATE credential_governance.journal_entries")]
    public async Task InstalledJournalBlocksRewritingProtectedFacts(string command)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        await IsolatedGovernanceJournalSchema.InstallAsync(db, new(Guid.NewGuid(), Guid.NewGuid()));

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(command));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task RootEnrollmentRequiresTheJournalWriterFence()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO credential_governance.journal_root (singleton, authority_id, generation, genesis_hash)
            VALUES (true, {journal.AuthorityId}, {journal.Generation}, {GovernanceJournalProtocol.Genesis.Hash})
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("credential_governance_journal_fence_required", error.MessageText);
        await IsolatedGovernanceJournalSchema.InstallAsync(db, journal);
    }

    private sealed class NoCommittedMutation : IPostgresGovernanceMutation
    {
        public Task AuthorizeAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct) => throw new NotSupportedException();
        public Task AuthorizeRecoveryAsync(WebApiDbContext db, CancellationToken ct) => throw new NotSupportedException();
        public Task ValidateNewAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct) => throw new NotSupportedException();
        public Task ApplyAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> VerifyAsync(WebApiDbContext db, GovernanceWriteIntent intent, CancellationToken ct) => throw new NotSupportedException();
    }
}

using Npgsql;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PostgresGovernanceWriterFenceTests
{
    [Fact]
    public async Task FenceSurvivesCommitAndRollbackAndSharesTheExistingTransactionLock()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var contender = await database.OpenAsync();

        await using (var fence = await PostgresGovernanceWriterFence.AcquireAsync(database.ConnectionString))
        {
            Assert.False(await TryTakeCommandLockAsync(contender));
            await using (var commit = await fence.BeginTransactionAsync()) await commit.CommitAsync();
            Assert.False(await TryTakeCommandLockAsync(contender));
            await using (var rollback = await fence.BeginTransactionAsync()) await rollback.RollbackAsync();
            Assert.False(await TryTakeCommandLockAsync(contender));
        }

        Assert.True(await TryTakeCommandLockAsync(contender));
    }

    [Fact]
    public async Task ReopenedConnectionCannotBeginAnotherFencedTransaction()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var contender = await database.OpenAsync();
        await using var original = await PostgresGovernanceWriterFence.AcquireAsync(database.ConnectionString);

        NpgsqlConnection connection;
        await using (var tx = await original.BeginTransactionAsync())
        {
            connection = tx.Connection!;
            await tx.CommitAsync();
        }

        await connection.CloseAsync();
        await connection.OpenAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => original.BeginTransactionAsync());
        Assert.Equal("credential_governance_database_fence_lost", error.Message);
        Assert.True(await TryTakeCommandLockAsync(contender));
    }

    [Fact]
    public async Task CancelledContenderLeavesNoFenceAfterTheOwnerCloses()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var contender = await database.OpenAsync();

        await using (var owner = await PostgresGovernanceWriterFence.AcquireAsync(database.ConnectionString))
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PostgresGovernanceWriterFence
                .AcquireAsync(database.ConnectionString, cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(await TryTakeCommandLockAsync(contender));
        }

        Assert.True(await TryTakeCommandLockAsync(contender));
    }

    private static async Task<bool> TryTakeCommandLockAsync(NpgsqlConnection contender)
    {
        await using var tx = await contender.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_try_advisory_xact_lock(8901202)", contender, tx);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}

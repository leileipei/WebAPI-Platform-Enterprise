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
            var waiting = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                ApplicationName = $"fence-cancel-{Guid.NewGuid():N}"
            };
            using var cancel = new CancellationTokenSource();
            var acquisition = PostgresGovernanceWriterFence.AcquireAsync(waiting.ConnectionString, cancel.Token);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!await HasCandidateLockAsync(contender, waiting.ApplicationName, waitingOnly: true, deadline.Token))
                    await Task.Delay(10, deadline.Token);
            }
            finally
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            Assert.False(await HasCandidateLockAsync(contender, waiting.ApplicationName, waitingOnly: false));
            Assert.False(await TryTakeCommandLockAsync(contender));
        }

        Assert.True(await TryTakeCommandLockAsync(contender));
    }

    private static async Task<bool> HasCandidateLockAsync(NpgsqlConnection observer, string applicationName,
        bool waitingOnly, CancellationToken ct = default)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT FROM pg_catalog.pg_locks l JOIN pg_catalog.pg_stat_activity a ON a.pid = l.pid
                WHERE a.datname = pg_catalog.current_database() AND a.application_name = @application
                    AND l.locktype = 'advisory' AND l.mode = 'ExclusiveLock'
                    AND l.classid = 0 AND l.objid = 8901202 AND l.objsubid = 1
                    AND (NOT @waiting_only OR NOT l.granted))
            """, observer);
        command.Parameters.AddWithValue("application", applicationName);
        command.Parameters.AddWithValue("waiting_only", waitingOnly);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<bool> TryTakeCommandLockAsync(NpgsqlConnection contender)
    {
        await using var tx = await contender.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_try_advisory_xact_lock(8901202)", contender, tx);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}

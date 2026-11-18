using System.Data;
using Npgsql;

namespace WebApi.Infrastructure.CredentialSecurity;

// Use a dedicated physical session so the same advisory lock survives the
// commit/confirm phases. Existing protected commands take the transaction
// form of this lock. Database roles must separately exclude other writers.
internal sealed class PostgresGovernanceWriterFence : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly int backendPid;
    private int disposed;

    private PostgresGovernanceWriterFence(NpgsqlConnection connection, int backendPid)
    {
        this.connection = connection;
        this.backendPid = backendPid;
    }

    internal static async Task<PostgresGovernanceWriterFence> AcquireAsync(string connectionString, CancellationToken ct = default)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Multiplexing = false,
            Enlist = false,
            CancellationTimeout = 2_000
        };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        PostgresGovernanceWriterFence? acquired = null;
        try
        {
            await connection.OpenAsync(ct);
            using var acquire = new NpgsqlCommand("SELECT pg_catalog.pg_backend_pid(), pg_catalog.pg_advisory_lock(8901202)", connection);
            var backendPid = (int)(await acquire.ExecuteScalarAsync(ct))!;
            acquired = new(connection, backendPid);
            ct.ThrowIfCancellationRequested();
            return acquired;
        }
        catch (Exception error)
        {
            // A known acquisition can be released explicitly. For an ambiguous
            // result, give up the physical session without trusting its state.
            await DisposeAfterFailureAsync(acquired is null ? connection : acquired, error);
            throw;
        }
    }

    internal async Task<NpgsqlTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        NpgsqlTransaction? transaction = null;
        try
        {
            if (Volatile.Read(ref disposed) != 0 || connection.State != ConnectionState.Open) throw Lost();
            transaction = await connection.BeginTransactionAsync(ct);
            using var check = new NpgsqlCommand("""
                SELECT pg_catalog.pg_backend_pid() = @backend AND EXISTS (
                    SELECT 1 FROM pg_catalog.pg_locks WHERE pid = @backend AND locktype = 'advisory'
                     AND mode = 'ExclusiveLock' AND classid = 0 AND objid = 8901202 AND objsubid = 1 AND granted)
                """, connection, transaction);
            check.Parameters.AddWithValue("backend", backendPid);
            if (await check.ExecuteScalarAsync(ct) is not true) throw Lost();
            return transaction;
        }
        catch (Exception error)
        {
            if (transaction is not null) await DisposeAfterFailureAsync(transaction, error);
            await DisposeAfterFailureAsync(error);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            // Socket close alone does not acknowledge backend exit or lock release.
            // Never issue unlock on a backend that replaced the pinned owner.
            if (connection.State == ConnectionState.Open)
            {
                using var release = new NpgsqlCommand("""
                    SELECT CASE WHEN pg_catalog.pg_backend_pid() = @backend
                        THEN pg_catalog.pg_advisory_unlock(8901202) ELSE false END
                    """, connection) { CommandTimeout = 5 };
                release.Parameters.AddWithValue("backend", backendPid);
                if (await release.ExecuteScalarAsync() is not true) throw Lost();
            }
        }
        catch (Exception error)
        {
            await DisposeAfterFailureAsync(connection, error);
            throw;
        }
        await connection.DisposeAsync();
    }

    internal ValueTask DisposeAfterFailureAsync(Exception error) => DisposeAfterFailureAsync(this, error);

    private static async ValueTask DisposeAfterFailureAsync(IAsyncDisposable resource, Exception primary)
    {
        try { await resource.DisposeAsync(); }
        catch (Exception cleanup) { primary.Data["credential_governance_fence_cleanup_error"] = cleanup.GetType().Name; }
    }

    private static InvalidOperationException Lost() => new("credential_governance_database_fence_lost");
}

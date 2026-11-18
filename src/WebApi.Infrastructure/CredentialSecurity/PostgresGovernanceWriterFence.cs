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
            Enlist = false
        };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            using var acquire = new NpgsqlCommand("SELECT pg_catalog.pg_backend_pid(), pg_catalog.pg_advisory_lock(8901202)", connection);
            var backendPid = (int)(await acquire.ExecuteScalarAsync(ct))!;
            ct.ThrowIfCancellationRequested();
            return new(connection, backendPid);
        }
        catch
        {
            // Physically close even if the lock result is ambiguous.
            await connection.DisposeAsync();
            throw;
        }
    }

    internal async Task<NpgsqlTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        NpgsqlTransaction? transaction = null;
        try
        {
            if (connection.State != ConnectionState.Open) throw Lost();
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
        catch
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { await DisposeAsync(); }
            throw;
        }
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();

    private static InvalidOperationException Lost() => new("credential_governance_database_fence_lost");
}

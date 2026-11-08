using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Captures source shape for review; it does not attest deployment history or gateway state.</summary>
public static class GovernanceMigrationSourceCapture
{
    private const int LastSupportedMainlineIndex = 21;

    public static async Task CaptureAsync(WebApiDbContext db, CancellationToken ct = default)
    {
        if (db.Database.GetDbConnection().State != ConnectionState.Open)
            throw new InvalidOperationException("Source capture requires the validated migration connection to stay open.");
        GovernanceMigrationConnection.RequireNoAmbientOrEnlistedTransaction(db);
        if (db.Database.CurrentTransaction is null)
        {
            // Establish an EF-tracked transaction before any probe. If a caller
            // has an unregistered database transaction, the provider rejects
            // the nested begin instead of silently leaving that transaction failed.
            await using var owned = await GovernanceMigrationConnection.BeginOnOpenConnectionAsync(db, ct);
            await CaptureAsync(db, ct);
            await owned.CommitAsync(ct);
            return;
        }
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.set_config('webapi.gov24_source', '', false)", ct);
        var known = db.Database.GetMigrations().ToArray();
        var applied = await GovernanceMigrationHistory.ReadAppliedAsync(db, ct);
        // A resumed, partially integrated database cannot recreate pre-migration evidence.
        if (applied.Length == 0 || applied.Length > LastSupportedMainlineIndex) return;
        if (!applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal) ||
            !GovernanceSchemaFingerprints.Expected.TryGetValue(applied[^1], out var expected))
            throw new InvalidOperationException("governance_migration_preflight_failed: source history changed");
        var actual = await db.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync(ct);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("governance_migration_preflight_failed: source schema changed");

        // PostgreSQL marks an explicit transaction failed after a permission
        // error. Isolate this optional identity read so a denied lookup can
        // leave provenance Unknown without poisoning the migration transaction.
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Source capture requires an EF-tracked transaction.");
        var savepoint = "webapi_source_" + Guid.NewGuid().ToString("N");
        if (!transaction.SupportsSavepoints)
            throw new InvalidOperationException("Source capture requires transaction savepoints.");
        await transaction.CreateSavepointAsync(savepoint, ct);
        string cluster;
        try
        {
            cluster = await db.Database.SqlQueryRaw<string>("SELECT system_identifier::text AS \"Value\" FROM pg_catalog.pg_control_system()").SingleAsync(ct);
        }
        catch (Exception error) when (error is DbException or OperationCanceledException)
        {
            // The caller's token may already be cancelled. Give transaction
            // recovery its own bound and preserve both errors if it fails.
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await transaction.RollbackToSavepointAsync(savepoint, recovery.Token);
                await transaction.ReleaseSavepointAsync(savepoint, recovery.Token);
            }
            catch (Exception recoveryError) when (recoveryError is DbException or InvalidOperationException or OperationCanceledException)
            {
                throw new InvalidOperationException("source_capture_savepoint_recovery_failed: transaction requires rollback", new AggregateException(error, recoveryError));
            }
            if (error is PostgresException { SqlState: "42501" })
            {
                ct.ThrowIfCancellationRequested();
                // Insufficient identity evidence leaves all migrated environments Unknown.
                return;
            }
            throw;
        }
        using (var release = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try { await transaction.ReleaseSavepointAsync(savepoint, release.Token); }
            catch (Exception releaseError) when (releaseError is DbException or InvalidOperationException or OperationCanceledException)
            {
                throw new InvalidOperationException("source_capture_savepoint_release_failed: transaction requires rollback", releaseError);
            }
        }
        ct.ThrowIfCancellationRequested();
        var database = await db.Database.SqlQueryRaw<string>("SELECT pg_catalog.current_database()::text AS \"Value\"").SingleAsync(ct);
        var oid = await db.Database.SqlQueryRaw<long>("SELECT oid::bigint AS \"Value\" FROM pg_database WHERE datname=pg_catalog.current_database()").SingleAsync(ct);
        var historyHash = await db.Database.SqlQueryRaw<string>("""
            SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(COALESCE(pg_catalog.json_agg(pg_catalog.json_build_array("MigrationId","ProductVersion") ORDER BY "MigrationId")::text,'[]'),'UTF8')),'hex') AS "Value"
            FROM public."__EFMigrationsHistory"
            """).SingleAsync(ct);
        var capturedAt = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT pg_catalog.clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var table = "webapi_gov24_" + Guid.NewGuid().ToString("N");
        // The name is a locally generated identifier; a fresh session-scoped table
        // holds the exact environment set observed before any product migration.
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            // Identifiers cannot be SQL parameters; quote the locally generated GUID name.
            using var identifierBuilder = new NpgsqlCommandBuilder();
            var identifier = identifierBuilder.QuoteIdentifier(table);
            command.CommandText = "CREATE TEMP TABLE " + identifier + " ON COMMIT PRESERVE ROWS AS SELECT e.id AS environment_id,e.project_id,p.organization_id FROM public.environments e JOIN public.projects p ON p.id=e.project_id";
            await command.ExecuteNonQueryAsync(ct);
        }
        var payload = JsonSerializer.Serialize(new { table, database, databaseOid = oid, clusterId = cluster,
            migration = applied[^1], schemaHash = actual, historyHash, capturedAt });
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_catalog.set_config('webapi.gov24_source', {payload}, false)", ct);
    }
}

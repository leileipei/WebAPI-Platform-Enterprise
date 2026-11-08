using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebApi.Infrastructure.Persistence;

internal static class MigrationReceiptIdentityCapture
{
    public static async Task<MigrationDatabaseIdentity?> CaptureAsync(WebApiDbContext db, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("migration_receipt_identity_requires_transaction");
        if (!transaction.SupportsSavepoints) throw new InvalidOperationException("migration_receipt_identity_requires_savepoint");
        var savepoint = "webapi_identity_" + Guid.NewGuid().ToString("N");
        await transaction.CreateSavepointAsync(savepoint, ct);
        try
        {
            var cluster = await db.Database.SqlQueryRaw<string>("SELECT system_identifier::text AS \"Value\" FROM pg_catalog.pg_control_system()").SingleAsync(ct);
            var name = await db.Database.SqlQueryRaw<string>("SELECT current_database()::text AS \"Value\"").SingleAsync(ct);
            var oid = await db.Database.SqlQueryRaw<long>("SELECT oid::bigint AS \"Value\" FROM pg_catalog.pg_database WHERE datname=current_database()").SingleAsync(ct);
            await transaction.ReleaseSavepointAsync(savepoint, ct);
            return new MigrationDatabaseIdentity(cluster, name, oid);
        }
        catch (Exception error) when (error is DbException or OperationCanceledException)
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await transaction.RollbackToSavepointAsync(savepoint, recovery.Token);
                await transaction.ReleaseSavepointAsync(savepoint, recovery.Token);
            }
            catch (Exception recoveryError) when (recoveryError is DbException or InvalidOperationException or OperationCanceledException)
            {
                throw new InvalidOperationException("migration_receipt_identity_recovery_failed", new AggregateException(error, recoveryError));
            }
            if (error is PostgresException { SqlState: "42501" })
            {
                ct.ThrowIfCancellationRequested();
                return null;
            }
            throw;
        }
    }
}

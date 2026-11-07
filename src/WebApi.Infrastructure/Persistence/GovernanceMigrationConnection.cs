using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace WebApi.Infrastructure.Persistence;

internal static class GovernanceMigrationConnection
{
    public static void RequireNoAmbientOrEnlistedTransaction(WebApiDbContext db)
    {
        var enlistment = db.GetService<IRelationalConnection>() as ITransactionEnlistmentManager;
        if (enlistment?.CurrentAmbientTransaction is not null || enlistment?.EnlistedTransaction is not null)
            throw new InvalidOperationException("Migration transaction does not support ambient or enlisted transactions.");
    }

    public static async Task<OwnedMigrationTransaction> BeginOnOpenConnectionAsync(WebApiDbContext db, CancellationToken ct,
        Func<CancellationToken, Task>? beforeRegistration = null)
    {
        if (db.Database.GetDbConnection().State != ConnectionState.Open)
            throw new InvalidOperationException("Migration transaction requires an open connection.");
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Migration transaction must not replace a caller transaction.");
        RequireNoAmbientOrEnlistedTransaction(db);

        // Retain the provider transaction handle before EF invokes any
        // registration callbacks. If one fails, only this owned transaction
        // can be rolled back; an unregistered caller transaction is untouched.
        var provider = await db.Database.GetDbConnection().BeginTransactionAsync(ct);
        IDbContextTransaction? tracked = null;
        try
        {
            // Begin first so an unregistered caller-owned provider transaction
            // is rejected. Persist evidence before EF registration callbacks
            // can execute application code on the newly owned transaction.
            if (beforeRegistration is not null) await beforeRegistration(ct);
            tracked = await db.Database.UseTransactionAsync(provider, ct);
            if (tracked is null || !ReferenceEquals(tracked.GetDbTransaction(), provider))
                throw new InvalidOperationException("Migration transaction registration changed the owned transaction.");
            return new OwnedMigrationTransaction(tracked, provider);
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            try
            {
                if (tracked is null) await db.Database.CloseConnectionAsync();
                else await tracked.DisposeAsync();
            }
            catch (Exception cleanupError) { failures.Add(cleanupError); }
            try { await provider.DisposeAsync(); }
            catch (Exception cleanupError) { failures.Add(cleanupError); }
            if (failures.Count != 1)
                throw new InvalidOperationException("migration_connection_recovery_failed", new AggregateException(failures));
            throw;
        }
    }
}

internal sealed class OwnedMigrationTransaction(IDbContextTransaction tracked, DbTransaction provider) : IAsyncDisposable
{
    public Task CommitAsync(CancellationToken ct) => tracked.CommitAsync(ct);
    public Task RollbackAsync(CancellationToken ct) => tracked.RollbackAsync(ct);

    public async ValueTask DisposeAsync()
    {
        try { await tracked.DisposeAsync(); }
        finally { await provider.DisposeAsync(); }
    }
}

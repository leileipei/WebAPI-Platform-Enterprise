using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebApi.Infrastructure.Persistence;

public sealed record MigrationRestoreVerification(int Attempts, int CommittedAttempts, string ContentHash);

/// <summary>Checks one restored snapshot; it is not an independent anti-rollback witness.</summary>
public static class MigrationRestoreVerifier
{
    public static async Task<MigrationRestoreVerification> VerifyAsync(WebApiDbContext db, MigrationEvidenceJournal journal,
        MigrationEvidenceArchiveInspection archive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(archive);
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("migration_restore_transaction_unsupported");
        GovernanceMigrationConnection.RequireNoAmbientOrEnlistedTransaction(db);
        if (archive.Attempts is < 1 or > 131072 || archive.PendingAttempts != 0 || archive.ContentHash.Length != 64 ||
            archive.ContentHash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("migration_restore_evidence_unverified");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var before = await journal.InspectDirectoryAsync(deadline.Token);
        if (before.Attempts != archive.Attempts || before.PendingAttempts != 0 || before.ContentHash != archive.ContentHash)
            throw new InvalidOperationException("migration_restore_evidence_unverified");
        var committed = await journal.ReadCommittedAttemptsAsync(deadline.Token);
        if (committed.Count == 0) throw new InvalidOperationException("migration_restore_evidence_unverified");

        await db.Database.OpenConnectionAsync(deadline.Token);
        try
        {
            await using var transaction = await GovernanceMigrationConnection.BeginOnOpenConnectionAsync(db, deadline.Token, async token =>
            {
                if (db.Database.GetDbConnection() is not NpgsqlConnection connection)
                    throw new InvalidOperationException("migration_restore_evidence_unverified");
                await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection);
                await readOnly.ExecuteNonQueryAsync(token);
            });
            try
            {
                await db.Database.ExecuteSqlRawAsync("""
                    SELECT pg_catalog.set_config('statement_timeout',
                      (CASE WHEN (SELECT setting::int FROM pg_catalog.pg_settings WHERE name='statement_timeout')=0
                        THEN 30000 ELSE LEAST(30000,(SELECT setting::int FROM pg_catalog.pg_settings WHERE name='statement_timeout')) END)::text || 'ms',true),
                      pg_catalog.set_config('lock_timeout',
                      (CASE WHEN (SELECT setting::int FROM pg_catalog.pg_settings WHERE name='lock_timeout')=0
                        THEN 5000 ELSE LEAST(5000,(SELECT setting::int FROM pg_catalog.pg_settings WHERE name='lock_timeout')) END)::text || 'ms',true)
                    """, deadline.Token);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(1464156498,1196381253)", deadline.Token);
                await MigrationEvidenceContinuity.VerifyInOwnedTransactionAsync(db, committed, deadline.Token);
                var after = await journal.InspectDirectoryAsync(deadline.Token);
                if (after != before) throw new InvalidOperationException("migration_restore_evidence_unverified");
                await transaction.RollbackAsync(deadline.Token);
                return new MigrationRestoreVerification(before.Attempts, committed.Count, before.ContentHash);
            }
            catch
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await transaction.RollbackAsync(recovery.Token); }
                catch (Exception) { throw new InvalidOperationException("migration_restore_recovery_unverified"); }
                throw;
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}

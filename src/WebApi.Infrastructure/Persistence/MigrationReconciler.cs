using Microsoft.EntityFrameworkCore;

namespace WebApi.Infrastructure.Persistence;

public enum MigrationReceiptObservation { Unavailable, Missing, Present }
public sealed record MigrationReconciliationReport(MigrationEvidenceOutcome Outcome, MigrationEvidenceReadState? Journal,
    MigrationReceiptObservation Receipt);

/// <summary>Read-only reconciliation; absence of a database row is never proof of rollback.</summary>
public static class MigrationReconciler
{
    public static async Task<MigrationEvidenceOutcome> CheckAsync(WebApiDbContext db, IMigrationEvidenceJournal journal,
        Guid attemptId, CancellationToken ct = default) => (await InspectAsync(db, journal, attemptId, ct)).Outcome;

    public static async Task<MigrationReconciliationReport> InspectAsync(WebApiDbContext db, IMigrationEvidenceJournal journal,
        Guid attemptId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(journal);
        if (attemptId == Guid.Empty) throw new ArgumentException("Attempt ID is required.", nameof(attemptId));
        MigrationEvidenceReadState? evidence = null;
        try { evidence = (await journal.ReadAsync(attemptId, ct)).State; }
        catch (Exception) { /* Unavailable evidence cannot prove a result. */ }

        var observed = MigrationReceiptObservation.Unavailable;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("reconciliation_requires_own_transaction");
            GovernanceMigrationConnection.RequireNoAmbientOrEnlistedTransaction(db);
            await using var tx = await db.Database.BeginTransactionAsync(deadline.Token);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", deadline.Token);
            var exists = await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.migration_validation_receipts') IS NOT NULL AS \"Value\"").SingleAsync(deadline.Token);
            if (!exists) observed = MigrationReceiptObservation.Missing;
            else
            {
                var found = await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={attemptId}) AS \"Value\"").SingleAsync(deadline.Token);
                observed = found ? MigrationReceiptObservation.Present : MigrationReceiptObservation.Missing;
            }
            await tx.RollbackAsync(deadline.Token);
        }
        catch (Exception) { observed = MigrationReceiptObservation.Unavailable; }

        // A present row and the local journal are observations, not proof that
        // this is the original database without a backup rollback. This system
        // currently has no independently trusted continuity witness.
        return new MigrationReconciliationReport(MigrationEvidenceOutcome.Unknown, evidence, observed);
    }
}

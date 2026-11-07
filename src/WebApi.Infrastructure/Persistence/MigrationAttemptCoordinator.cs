using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebApi.Infrastructure.Persistence;

public sealed record MigrationAttemptResult(Guid AttemptId, MigrationEvidenceOutcome Outcome,
    MigrationEvidenceOutcome? BootstrapOutcome = null, MigrationEvidenceOutcome? SeedOutcome = null)
{
    public MigrationEvidenceReason Reason { get; init; } = MigrationEvidenceReason.None;
    public MigrationBudgetCause? BudgetCause { get; init; }
    // Available only to existing internal regression helpers; never persisted or emitted by the CLI.
    internal Exception? Cause { get; init; }
}

/// <summary>Coordinates a database transaction and a separate, non-atomic evidence store.</summary>
public static class MigrationAttemptCoordinator
{
    public static Task<MigrationAttemptResult> RunAsync(WebApiDbContext db, MigrationBudget budget,
        IMigrationEvidenceJournal journal, CancellationToken ct = default) =>
        RunWithPostActionsAsync(db, budget, journal, null, null, ct);

    public static async Task<MigrationAttemptResult> RunWithPostActionsAsync(WebApiDbContext db, MigrationBudget budget,
        IMigrationEvidenceJournal journal, Func<CancellationToken, Task>? bootstrap, Func<CancellationToken, Task>? seed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(journal);
        budget.Validate();
        var maxRows = ReadReceiptLimit();
        var id = Guid.NewGuid();
        string? buildHash;
        try { buildHash = await GovernanceMigrationRunner.ObservedAssemblyHashAsync(ct); }
        catch (OperationCanceledException error)
        {
            return new MigrationAttemptResult(id, MigrationEvidenceOutcome.Unknown) { Cause = error, Reason = MigrationEvidenceReason.Cancelled };
        }
        var start = new MigrationAttemptStart(id, MigrationReceiptEntry.Managed, DateTimeOffset.UtcNow, budget,
            BootstrapRequested: bootstrap is not null, SeedRequested: seed is not null, BuildHash: buildHash);
        IReadOnlyList<Guid> checkedHistory;
        try
        {
            if (db.Database.CurrentTransaction is not null)
                throw new InvalidOperationException("The migration runner must own its transaction.");
            GovernanceMigrationConnection.RequireNoAmbientOrEnlistedTransaction(db);
            if (db.Database.GetDbConnection() is not NpgsqlConnection)
                throw new InvalidOperationException("migration_evidence_history_unverified");
            checkedHistory = await journal.ReadCommittedAttemptsAsync(ct);
        }
        catch (Exception error)
        {
            return new MigrationAttemptResult(id, MigrationEvidenceOutcome.Unknown)
            { Cause = error, Reason = MigrationEvidenceContinuity.FailureReason(error) };
        }
        var state = new MigrationRunState(id, maxRows, buildHash, journal, start, checkedHistory);
        try
        {
            await GovernanceMigrationRunner.MigrateWithReceiptAsync(db, budget, state, ct);
        }
        catch (Exception error)
        {
            if (!state.EvidenceStarted)
                return new MigrationAttemptResult(id, MigrationEvidenceOutcome.Unknown)
                { Cause = error, Reason = MigrationEvidenceContinuity.FailureReason(error) };
            var confirmed = !state.CommitInvoked && (!state.BeginAttempted || state.RollbackConfirmed);
            var record = MigrationEvidenceRecord.FromException(MigrationEvidencePhase.Migration, error, confirmed, state.TransactionDeadlineExpired);
            var persisted = await TryAppendAsync(journal, id, record);
            return new MigrationAttemptResult(id, persisted ? record.Outcome : MigrationEvidenceOutcome.Unknown)
            { Cause = error, Reason = persisted ? record.Reason : MigrationEvidenceReason.Storage, BudgetCause = persisted ? record.BudgetCause : null };
        }
        var committed = new MigrationEvidenceRecord(MigrationEvidencePhase.Migration,
            MigrationEvidenceOutcome.Committed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow);
        if (!await TryAppendAsync(journal, id, committed))
            return new MigrationAttemptResult(id, MigrationEvidenceOutcome.Unknown) { Reason = MigrationEvidenceReason.Storage };

        MigrationEvidenceOutcome? bootstrapOutcome = null;
        MigrationEvidenceOutcome? seedOutcome = null;
        if (bootstrap is not null)
            bootstrapOutcome = await RunPostActionAsync(journal, id, MigrationEvidencePhase.Bootstrap, bootstrap, ct);
        if (seed is not null)
        {
            if (bootstrap is not null && bootstrapOutcome != MigrationEvidenceOutcome.Completed)
            {
                var skipped = new MigrationEvidenceRecord(MigrationEvidencePhase.Seed, MigrationEvidenceOutcome.Skipped,
                    MigrationEvidenceReason.None, DateTimeOffset.UtcNow);
                seedOutcome = await TryAppendAsync(journal, id, skipped) ? MigrationEvidenceOutcome.Skipped : MigrationEvidenceOutcome.Unknown;
            }
            else seedOutcome = await RunPostActionAsync(journal, id, MigrationEvidencePhase.Seed, seed, ct);
        }
        return new MigrationAttemptResult(id, MigrationEvidenceOutcome.Committed, bootstrapOutcome, seedOutcome);
    }

    private static async Task<MigrationEvidenceOutcome> RunPostActionAsync(IMigrationEvidenceJournal journal, Guid id,
        MigrationEvidencePhase phase, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        MigrationEvidenceRecord record;
        try
        {
            await action(ct);
            record = new MigrationEvidenceRecord(phase, MigrationEvidenceOutcome.Completed, MigrationEvidenceReason.None, DateTimeOffset.UtcNow);
        }
        catch (Exception error)
        {
            // These actions have separate side effects; an exception alone does not prove rollback.
            record = MigrationEvidenceRecord.FromException(phase, error, rollbackConfirmed: false);
        }
        return await TryAppendAsync(journal, id, record) ? record.Outcome : MigrationEvidenceOutcome.Unknown;
    }

    private static async Task<bool> TryAppendAsync(IMigrationEvidenceJournal journal, Guid id, MigrationEvidenceRecord record)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await journal.AppendAsync(id, record, deadline.Token); return true; }
        catch (Exception) { return false; }
    }

    private static int ReadReceiptLimit()
    {
        var value = Environment.GetEnvironmentVariable("WEBAPI_MIGRATION_RECEIPT_MAX_ROWS");
        if (string.IsNullOrEmpty(value) || value.Length > 7 || !value.All(c => c is >= '0' and <= '9') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result is < 1 or > 1000000)
            throw new InvalidOperationException("migration_receipt_configuration_invalid");
        return result;
    }
}

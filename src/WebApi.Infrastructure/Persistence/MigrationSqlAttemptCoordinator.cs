using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Executes verified SQL bytes once on a dedicated connection, with separate durable evidence.</summary>
public static class MigrationSqlAttemptCoordinator
{
    private const int MaximumSqlBytes = 16 * 1024 * 1024;

    public static async Task<MigrationAttemptResult> RunAsync(string connectionString, MigrationSqlBudget budget,
        IMigrationEvidenceJournal journal, string sqlFile, string expectedHash, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(journal);
        budget.Validate();
        var sql = await ReadVerifiedSqlAsync(sqlFile, expectedHash, ct);
        var id = Guid.NewGuid();
        var buildHash = await GovernanceMigrationRunner.ObservedAssemblyHashAsync(ct);
        var start = new MigrationAttemptStart(id, MigrationReceiptEntry.Sql, DateTimeOffset.UtcNow, null,
            BuildHash: buildHash, SqlHash: expectedHash, SqlBudget: budget);
        IReadOnlyList<Guid> checkedHistory;
        try
        {
            checkedHistory = await journal.ReadCommittedAttemptsAsync(ct);
        }
        catch (Exception error)
        {
            return new MigrationAttemptResult(id, MigrationEvidenceOutcome.Unknown)
            { Cause = error, Reason = MigrationEvidenceContinuity.FailureReason(error) };
        }
        try { await journal.StartCheckedAsync(start, checkedHistory, ct); }
        catch (InvalidOperationException error) when (error.Message == "migration_evidence_pending_review")
        { return new MigrationAttemptResult(id, MigrationEvidenceOutcome.Unknown) { Cause = error, Reason = MigrationEvidenceReason.PendingReview }; }
        catch (Exception error) { return new MigrationAttemptResult(id, MigrationEvidenceOutcome.Unknown) { Cause = error, Reason = MigrationEvidenceReason.Storage }; }

        var scriptStarted = false;
        var transactionDeadlineExpired = false;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            using (var opening = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                opening.CancelAfter(TimeSpan.FromSeconds(30));
                await connection.OpenAsync(opening.Token);
            }
            using var timer = new CancellationTokenSource();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
            timer.CancelAfter(budget.TransactionTimeout);
            try
            {
                await using (var configure = new NpgsqlCommand("SELECT pg_catalog.set_config('webapi.migration_transaction_seconds',@seconds,false),pg_catalog.set_config('webapi.migration_receipt_max_rows',@rows,false),pg_catalog.set_config('webapi.migration_attempt_id',@attempt,false),pg_catalog.set_config('webapi.migration_journal_history',@history,false)", connection))
                {
                    configure.Parameters.AddWithValue("seconds", ((int)budget.TransactionTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture));
                    configure.Parameters.AddWithValue("rows", budget.ReceiptMaxRows.ToString(CultureInfo.InvariantCulture));
                    configure.Parameters.AddWithValue("attempt", id.ToString("D"));
                    configure.Parameters.AddWithValue("history", JsonSerializer.Serialize(checkedHistory));
                    await configure.ExecuteNonQueryAsync(deadline.Token);
                }
                await using (var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 })
                {
                    scriptStarted = true;
                    await command.ExecuteNonQueryAsync(deadline.Token);
                }
                await using (var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id=@attempt AND entry_point='Sql' AND receipt_max_rows=@rows)", connection))
                {
                    check.Parameters.AddWithValue("attempt", id);
                    check.Parameters.AddWithValue("rows", budget.ReceiptMaxRows);
                    if (await check.ExecuteScalarAsync(deadline.Token) is not true)
                        throw new InvalidOperationException("migration_sql_receipt_not_observed");
                }
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                transactionDeadlineExpired = true;
                throw;
            }
        }
        catch (Exception error)
        {
            // Once script execution starts, even an exception may follow COMMIT.
            var record = MigrationEvidenceRecord.FromException(MigrationEvidencePhase.Migration, error, rollbackConfirmed: !scriptStarted, transactionDeadlineExpired: transactionDeadlineExpired);
            var saved = await TryAppendAsync(journal, id, record);
            return new MigrationAttemptResult(id, saved ? record.Outcome : MigrationEvidenceOutcome.Unknown)
            { Cause = error, Reason = saved ? record.Reason : MigrationEvidenceReason.Storage, BudgetCause = saved ? record.BudgetCause : null };
        }
        var committed = new MigrationEvidenceRecord(MigrationEvidencePhase.Migration, MigrationEvidenceOutcome.Committed,
            MigrationEvidenceReason.None, DateTimeOffset.UtcNow);
        return await TryAppendAsync(journal, id, committed)
            ? new MigrationAttemptResult(id, MigrationEvidenceOutcome.Committed)
            : new MigrationAttemptResult(id, MigrationEvidenceOutcome.Unknown) { Reason = MigrationEvidenceReason.Storage };
    }

    private static async Task<string> ReadVerifiedSqlAsync(string path, string expectedHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || expectedHash is null || expectedHash.Length != 64 ||
            expectedHash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("migration_sql_configuration_invalid");
        byte[] bytes;
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            using var contents = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await source.ReadAsync(buffer, ct)) != 0)
            {
                if (contents.Length + count > MaximumSqlBytes) throw new InvalidOperationException("migration_sql_file_too_large");
                await contents.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            bytes = contents.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("migration_sql_file_unavailable");
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expectedHash)))
            throw new InvalidOperationException("migration_sql_hash_mismatch");
        try
        {
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException) { throw new InvalidOperationException("migration_sql_encoding_invalid"); }
    }

    private static async Task<bool> TryAppendAsync(IMigrationEvidenceJournal journal, Guid id, MigrationEvidenceRecord record)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await journal.AppendAsync(id, record, deadline.Token); return true; }
        catch (Exception) { return false; }
    }
}

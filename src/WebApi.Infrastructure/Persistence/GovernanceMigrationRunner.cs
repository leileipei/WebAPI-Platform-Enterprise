using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WebApi.Infrastructure.Notifications;

namespace WebApi.Infrastructure.Persistence;

/// <summary>Runs the official migration checks and migration on one connection and transaction.</summary>
public static class GovernanceMigrationRunner
{
    private const string HistoricalSqlHash = "60f4b634d790ee601fd7c42aec1f56fbe28b0f3307b030cd12374bce9bfdffe6";

    public static void Configure(DbContextOptionsBuilder<WebApiDbContext> options) =>
        options.ReplaceService<IMigrationsSqlGenerator, ManagedNotificationMigrationsSqlGenerator>();

    internal static Task MigrateWithReceiptAsync(WebApiDbContext db, MigrationBudget budget, MigrationRunState state, CancellationToken ct) =>
        MigrateCoreAsync(db, budget, state, ct);

    private static async Task MigrateCoreAsync(WebApiDbContext db, MigrationBudget budget, MigrationRunState? state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(budget);
        budget.Validate();
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("The migration runner must own its transaction.");
        GovernanceMigrationConnection.RequireNoAmbientOrEnlistedTransaction(db);

        using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectionDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        await db.Database.OpenConnectionAsync(connectionDeadline.Token);
        try
        {
            using var scope = MigrationBudgetScope.Start(budget, ct);
            var migrationToken = scope.Token;
            OwnedMigrationTransaction? transaction = null;
            try
            {
                if (state is not null) state.BeginAttempted = true;
                transaction = await GovernanceMigrationConnection.BeginOnOpenConnectionAsync(db, migrationToken,
                    state is null ? null : async token =>
                    {
                        await state.Journal.StartCheckedAsync(state.Start, state.CheckedHistory, token);
                        state.EvidenceStarted = true;
                    });
                await scope.ConfigureTransactionAsync(db, migrationToken);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(1464156498,1196381253)", migrationToken);
                if (state is not null) await MigrationEvidenceContinuity.VerifyInOwnedTransactionAsync(db, state.CheckedHistory, migrationToken);
                await GovernanceMigrationWriteFence.AcquireHistoryAsync(db, migrationToken);
                var known = db.Database.GetMigrations().ToArray();
                var applied = await GovernanceMigrationHistory.ReadAppliedAsync(db, migrationToken);
                if (applied.SequenceEqual(known, StringComparer.Ordinal))
                {
                    await GovernanceMigrationPreflight.ValidateAsync(db, migrationToken);
                    if (!(await GovernanceMigrationHistory.ReadAppliedAsync(db, migrationToken)).SequenceEqual(known, StringComparer.Ordinal))
                        throw new InvalidOperationException("governance_migration_preflight_failed: migration history changed");
                    if (state is not null)
                    {
                        var sourceHash = await db.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync(migrationToken);
                        await InsertReceiptAsync(db, budget, state, known, applied, sourceHash, null, migrationToken);
                        state.CommitInvoked = true;
                    }
                    await transaction.CommitAsync(migrationToken);
                    return;
                }
                await GovernanceMigrationWriteFence.AcquireAsync(db, migrationToken);
                await GovernanceMigrationPreflight.ValidateAsync(db, migrationToken);
                var sourceSchemaHash = state is null ? null : await db.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync(migrationToken);
                var summary = await NotificationMigrationPreflight.ValidateAsync(db, budget, migrationToken);
                await GovernanceMigrationSourceCapture.CaptureAsync(db, migrationToken);
                // Npgsql's absent-history probe leaves an enclosing transaction aborted.
                // Create the provider's history table only after the empty schema passes
                // preflight, within the same transaction as the migrations.
                if (!await GovernanceMigrationHistory.ExistsAsync(db, migrationToken))
                    await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetCreateScript(), migrationToken);
                var generator = db.GetService<IMigrationsSqlGenerator>() as ManagedNotificationMigrationsSqlGenerator
                    ?? throw new InvalidOperationException("notification_migration_generator_not_configured");
                ManagedNotificationMigrationsSqlGenerator.VerifyHistoricalMigration(
                    db.GetService<IMigrationsAssembly>(), db.Database.ProviderName
                        ?? throw new InvalidOperationException("notification_migration_provider_missing"));
                generator.EnableManagedValidation();
                try { await db.Database.MigrateAsync(migrationToken); }
                finally { generator.DisableManagedValidation(); }
                if (state is not null)
                {
                    await InsertReceiptAsync(db, budget, state, known, applied, sourceSchemaHash, summary, migrationToken);
                    state.CommitInvoked = true;
                }
                await transaction.CommitAsync(migrationToken);
            }
            catch (Exception error)
            {
                if (state is not null && error is OperationCanceledException && scope.TransactionDeadlineExpired)
                    state.TransactionDeadlineExpired = true;
                if (state is not null && transaction is not null && !state.CommitInvoked)
                {
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await transaction.RollbackAsync(recovery.Token); state.RollbackConfirmed = true; }
                    catch { /* An unconfirmed rollback remains unknown. */ }
                }
                throw;
            }
            finally
            {
                if (transaction is not null) await transaction.DisposeAsync();
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task InsertReceiptAsync(WebApiDbContext db, MigrationBudget budget, MigrationRunState state,
        string[] known, string[] applied, string? sourceSchemaHash, NotificationValidationSummary? summary, CancellationToken ct)
    {
        if (known.Length == 0) throw new InvalidOperationException("migration_manifest_empty");
        ManagedNotificationMigrationsSqlGenerator.VerifyHistoricalMigration(db.GetService<IMigrationsAssembly>(),
            db.Database.ProviderName ?? throw new InvalidOperationException("notification_migration_provider_missing"));
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', known))));
        var validation = summary is null ? MigrationReceiptValidation.Unknown : summary.Status == NotificationValidationStatus.Passed
            ? MigrationReceiptValidation.Passed : MigrationReceiptValidation.NotApplicable;
        var identity = await MigrationReceiptIdentityCapture.CaptureAsync(db, ct);
        var receipt = new MigrationReceipt(state.AttemptId, MigrationReceiptEntry.Managed, applied.LastOrDefault(), known[^1], identity,
            sourceSchemaHash, manifestHash, validation, summary?.SettingsPresent, summary?.RuleCount, summary?.TotalBytes,
            budget, HistoricalSqlHash, state.BuildHash, DateTimeOffset.UtcNow, state.MaxRows);
        await MigrationReceiptStore.InsertAsync(db, receipt, ct);
    }

    internal static async Task<string?> ObservedAssemblyHashAsync(CancellationToken ct)
    {
        var path = typeof(GovernanceMigrationRunner).Assembly.Location;
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}

internal sealed class MigrationRunState(Guid attemptId, int maxRows, string? buildHash,
    IMigrationEvidenceJournal journal, MigrationAttemptStart start, IReadOnlyList<Guid> checkedHistory)
{
    public Guid AttemptId { get; } = attemptId;
    public int MaxRows { get; } = maxRows;
    public string? BuildHash { get; } = buildHash;
    public IMigrationEvidenceJournal Journal { get; } = journal;
    public MigrationAttemptStart Start { get; } = start;
    public IReadOnlyList<Guid> CheckedHistory { get; } = checkedHistory;
    public bool EvidenceStarted { get; set; }
    public bool BeginAttempted { get; set; }
    public bool CommitInvoked { get; set; }
    public bool RollbackConfirmed { get; set; }
    public bool TransactionDeadlineExpired { get; set; }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Reflection;
using System.Runtime.Versioning;
using WebApi.Infrastructure.Persistence;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[SupportedOSPlatform("linux")]
[Collection("MigrationIsolation")]
public sealed class MigrationAttemptTraceTests
{
    private static readonly MigrationBudget Budget = new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864);

    private static async Task<PostgresDatabase> NewDatabaseAsync()
    {
        var database = new PostgresDatabase();
        Console.WriteLine("isolated-test-db=" + new Npgsql.NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        await database.InitializeAsync();
        return database;
    }

    private static WebApiDbContext Context(PostgresDatabase database)
    {
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
        GovernanceMigrationRunner.Configure(options);
        return new WebApiDbContext(options.Options);
    }

    private static string RetainedEvidenceDirectory()
    {
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var path = Path.Combine(root, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Console.WriteLine("isolated-evidence-retained=" + path);
        return path;
    }

    [Fact]
    public async Task EvidenceIsDurableBeforeTransactionRegistrationCallbacks()
    {
        var database = await NewDatabaseAsync();
        var path = RetainedEvidenceDirectory();
        var check = new CheckEvidenceBeforeRegistration(path);
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(check);
        GovernanceMigrationRunner.Configure(options);
        try
        {
            await using var db = new WebApiDbContext(options.Options);
            var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(path, 8192, 16777216, 1048576));
            var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal);
            Assert.True(check.Reached);
            Assert.Equal(MigrationEvidenceOutcome.Committed, result.Outcome);
        }
        finally { Npgsql.NpgsqlConnection.ClearAllPools(); Console.WriteLine("isolated-registration-db-and-evidence-retained"); }
    }

    [Fact]
    public async Task CallerTransactionWithPriorHistoryIsRejectedWithoutBeingPoisoned()
    {
        var database = await NewDatabaseAsync();
        var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(RetainedEvidenceDirectory(), 8192, 16777216, 1048576));
        var role = "gov24_txcheck_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-role-retained=" + role);
        try
        {
            await using (var initial = Context(database))
                Assert.Equal(MigrationEvidenceOutcome.Committed, (await MigrationAttemptCoordinator.RunAsync(initial, Budget, journal)).Outcome);
            await using (var admin = await database.OpenAsync())
            await using (var create = new Npgsql.NpgsqlCommand($"CREATE ROLE \"{role}\" NOLOGIN", admin)) await create.ExecuteNonQueryAsync();
            await using var db = Context(database);
            await db.Database.OpenConnectionAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await using (var assume = db.Database.GetDbConnection().CreateCommand())
            {
                // The identifier is generated locally from a fixed prefix and GUID.
                assume.CommandText = "SET LOCAL ROLE \"" + role + "\"";
                await assume.ExecuteNonQueryAsync();
            }
            var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal);
            Assert.NotEqual(MigrationEvidenceOutcome.Committed, result.Outcome);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync());
            Assert.Single(await journal.ReadCommittedAttemptsAsync(CancellationToken.None));
            await transaction.RollbackAsync();
        }
        finally { Npgsql.NpgsqlConnection.ClearAllPools(); Console.WriteLine("isolated-caller-transaction-db-role-and-evidence-retained"); }
    }

    [Fact]
    public async Task UnregisteredCallerTransactionWithPriorHistoryIsRejectedWithoutJournalWrite()
    {
        var database = await NewDatabaseAsync();
        var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(RetainedEvidenceDirectory(), 8192, 16777216, 1048576));
        try
        {
            await using (var initial = Context(database))
                Assert.Equal(MigrationEvidenceOutcome.Committed, (await MigrationAttemptCoordinator.RunAsync(initial, Budget, journal)).Outcome);
            await using var db = Context(database);
            await db.Database.OpenConnectionAsync();
            await using var transaction = await db.Database.GetDbConnection().BeginTransactionAsync();
            Assert.Null(db.Database.CurrentTransaction);
            var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal);
            Assert.NotEqual(MigrationEvidenceOutcome.Committed, result.Outcome);
            await using (var check = db.Database.GetDbConnection().CreateCommand())
            {
                check.CommandText = "SELECT 1";
                Assert.Equal(1, (int)(await check.ExecuteScalarAsync())!);
            }
            Assert.Single(await journal.ReadCommittedAttemptsAsync(CancellationToken.None));
            await transaction.RollbackAsync();
        }
        finally { Npgsql.NpgsqlConnection.ClearAllPools(); Console.WriteLine("isolated-unregistered-transaction-db-and-evidence-retained"); }
    }

    [Fact]
    public async Task ConnectionSwitchedAfterJournalCheckCannotMigrateDifferentDatabase()
    {
        var original = await NewDatabaseAsync();
        var changed = await NewDatabaseAsync();
        var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(RetainedEvidenceDirectory(), 8192, 16777216, 1048576));
        try
        {
            await using (var initial = Context(original))
                Assert.Equal(MigrationEvidenceOutcome.Committed, (await MigrationAttemptCoordinator.RunAsync(initial, Budget, journal)).Outcome);
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var migrating = Context(original);
            var switching = new SwitchOnStartJournal(journal, migrating, changed.ConnectionString);
            var result = await MigrationAttemptCoordinator.RunAsync(migrating, Budget, switching);
            Assert.Equal(MigrationEvidenceOutcome.Unknown, result.Outcome);
            Assert.Contains(result.Reason, new[] { MigrationEvidenceReason.PendingReview, MigrationEvidenceReason.Storage });
            await using var check = changed.Context();
            Assert.True(await check.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NULL AS \"Value\"").SingleAsync());
        }
        finally { Npgsql.NpgsqlConnection.ClearAllPools(); Console.WriteLine("isolated-switched-databases-and-evidence-retained"); }
    }

    [Fact]
    public async Task RealConfiguredTransactionDeadlineIsRecordedWithoutClaimingRollback()
    {
        var database = await NewDatabaseAsync();
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var path = Path.Combine(root, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Console.WriteLine("isolated-timeout-evidence-retained=" + path);
        var blocker = new DelayUntilTransactionDeadline();
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(blocker);
        GovernanceMigrationRunner.Configure(options);
        try
        {
            await using var db = new WebApiDbContext(options.Options);
            var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(path, 8192, 16777216, 1048576));
            var result = await MigrationAttemptCoordinator.RunAsync(db, Budget with { TransactionTimeout = TimeSpan.FromSeconds(60) }, journal);
            Assert.True(blocker.Reached);
            Assert.NotEqual(MigrationEvidenceOutcome.Committed, result.Outcome);
            Assert.Equal(MigrationEvidenceReason.Budget, result.Reason);
            Assert.Equal(MigrationBudgetCause.TransactionDeadline, result.BudgetCause);
            var record = Assert.Single((await journal.ReadAsync(result.AttemptId, CancellationToken.None)).Records);
            Assert.Equal(MigrationBudgetCause.TransactionDeadline, record.BudgetCause);
            Assert.Equal(result.Outcome, record.Outcome);
        }
        finally { Npgsql.NpgsqlConnection.ClearAllPools(); Console.WriteLine("isolated-timeout-db-and-evidence-retained"); }
    }

    [Fact]
    public async Task StartedBeforeDatabaseMutation()
    {
        var database = await NewDatabaseAsync();
        await using var db = Context(database);
        var journal = new TestJournal { FailStart = true };
        var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, result.Outcome);
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task UnsafeRealEvidenceDirectoryStopsBeforeDatabaseMutation()
    {
        var database = await NewDatabaseAsync();
        await using var db = Context(database);
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var path = Path.Combine(root, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(path, 8192, 16777216, 1048576));
        var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, result.Outcome);
        Assert.Equal(MigrationEvidenceReason.Storage, result.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task JournalFailureAfterCommitIsUnknown()
    {
        var database = await NewDatabaseAsync();
        await using var db = Context(database);
        var journal = new TestJournal { FailAppend = true };
        var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, result.Outcome);
        Assert.NotNull(journal.Start);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={result.AttemptId}) AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task CommitInterruptionRemainsUnknownDespiteAbsentReceipt()
    {
        var database = await NewDatabaseAsync();
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
        GovernanceMigrationRunner.Configure(options);
        options.AddInterceptors(new CommitInterruption());
        await using var db = new WebApiDbContext(options.Options);
        var journal = new TestJournal();
        var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, result.Outcome);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, Assert.Single(journal.Records).Outcome);
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.migration_validation_receipts') IS NOT NULL AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task RollbackConfirmedProducesFailure()
    {
        var database = await NewDatabaseAsync();
        await using var db = Context(database);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE public.unexpected_trace_fixture (id integer PRIMARY KEY)");
        var journal = new TestJournal();
        var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Failed, result.Outcome);
        Assert.Equal(MigrationEvidenceReason.GovernancePreflight, result.Reason);
        Assert.Equal(MigrationEvidenceOutcome.Failed, Assert.Single(journal.Records).Outcome);
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.migration_validation_receipts') IS NOT NULL AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task CommitUnknownNeverBecomesFailureFromMissingReceipt()
    {
        var database = await NewDatabaseAsync();
        await using var db = Context(database);
        var journal = new TestJournal();
        var id = Guid.NewGuid();
        await journal.StartAsync(new MigrationAttemptStart(id, MigrationReceiptEntry.Managed, DateTimeOffset.UtcNow, Budget), CancellationToken.None);
        var result = await MigrationReconciler.CheckAsync(db, journal, id, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, result);
        var report = await MigrationReconciler.InspectAsync(db, journal, id, CancellationToken.None);
        Assert.Equal(MigrationReceiptObservation.Missing, report.Receipt);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, report.Outcome);
        Assert.Empty(journal.Records);
        await database.DisposeAsync();
    }

    [Fact]
    public async Task RealJournalAndReceiptAreBothObservedButNotContinuityProof()
    {
        var database = await NewDatabaseAsync();
        await using var db = Context(database);
        var sourceRoot = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var path = Path.Combine(sourceRoot, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(path, 8192, 16777216, 1048576));
        var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Committed, result.Outcome);
        var report = await MigrationReconciler.InspectAsync(db, journal, result.AttemptId, CancellationToken.None);
        Assert.Equal(MigrationEvidenceReadState.Complete, report.Journal);
        Assert.Equal(MigrationReceiptObservation.Present, report.Receipt);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, report.Outcome);
        Assert.Equal(MigrationEvidenceReadState.Complete, (await journal.ReadAsync(result.AttemptId, CancellationToken.None)).State);
        await database.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulAttemptRecordsMatchingReceipt()
    {
        var database = await NewDatabaseAsync();
        await using var db = Context(database);
        var journal = new TestJournal();
        var result = await MigrationAttemptCoordinator.RunAsync(db, Budget, journal, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Committed, result.Outcome);
        Assert.Equal(result.AttemptId, journal.Start!.AttemptId);
        Assert.Equal(MigrationEvidenceOutcome.Committed, Assert.Single(journal.Records).Outcome);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={result.AttemptId} AND entry_point='Managed' AND validation_state='NotApplicable') AS \"Value\"").SingleAsync());
        var canReadIdentity = await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.has_function_privilege(current_user, 'pg_catalog.pg_control_system()', 'EXECUTE') AS \"Value\"").SingleAsync();
        if (canReadIdentity)
            Assert.True(await db.Database.SqlQuery<bool>($"SELECT identity_state='Observed' AND cluster_identifier=(SELECT system_identifier::text FROM pg_catalog.pg_control_system()) AND database_name=current_database() AND database_oid=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database()) AS \"Value\" FROM public.migration_validation_receipts WHERE attempt_id={result.AttemptId}").SingleAsync());
        else
            Assert.True(await db.Database.SqlQuery<bool>($"SELECT identity_state='Unknown' AS \"Value\" FROM public.migration_validation_receipts WHERE attempt_id={result.AttemptId}").SingleAsync());
        await using var assemblyFile = File.OpenRead(typeof(GovernanceMigrationRunner).Assembly.Location);
        var assemblyHash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(assemblyFile));
        Assert.Equal(assemblyHash, journal.Start!.BuildHash);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT build_hash={assemblyHash} AS \"Value\" FROM public.migration_validation_receipts WHERE attempt_id={result.AttemptId}").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task BootstrapUncertaintyDoesNotEraseCommittedMigrationOrRunSeed()
    {
        var database = await NewDatabaseAsync();
        await using var db = Context(database);
        var journal = new TestJournal();
        var seedCalled = false;
        var result = await MigrationAttemptCoordinator.RunWithPostActionsAsync(db, Budget, journal,
            _ => throw new InvalidOperationException("synthetic bootstrap failure"),
            _ => { seedCalled = true; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Committed, result.Outcome);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, result.BootstrapOutcome);
        Assert.Equal(MigrationEvidenceOutcome.Skipped, result.SeedOutcome);
        Assert.False(seedCalled);
        Assert.True(journal.Start!.BootstrapRequested && journal.Start.SeedRequested);
        Assert.Equal(new[] { MigrationEvidenceOutcome.Committed, MigrationEvidenceOutcome.Unknown, MigrationEvidenceOutcome.Skipped }, journal.Records.Select(r => r.Outcome));
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={result.AttemptId}) AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulPostActionsAreRecordedSeparately()
    {
        var database = await NewDatabaseAsync();
        await using var db = Context(database);
        var journal = new TestJournal();
        var order = new List<string>();
        var result = await MigrationAttemptCoordinator.RunWithPostActionsAsync(db, Budget, journal,
            _ => { order.Add("bootstrap"); return Task.CompletedTask; },
            _ => { order.Add("seed"); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(MigrationEvidenceOutcome.Committed, result.Outcome);
        Assert.Equal(MigrationEvidenceOutcome.Completed, result.BootstrapOutcome);
        Assert.Equal(MigrationEvidenceOutcome.Completed, result.SeedOutcome);
        Assert.Equal(new[] { "bootstrap", "seed" }, order);
        Assert.Equal(new[] { MigrationEvidencePhase.Migration, MigrationEvidencePhase.Bootstrap, MigrationEvidencePhase.Seed }, journal.Records.Select(r => r.Phase));
        await database.DisposeAsync();
    }

    [Fact]
    public void OldUnjournaledRunnerIsNotPublic()
    {
        Assert.Null(typeof(GovernanceMigrationRunner).GetMethod("MigrateAsync", BindingFlags.Public | BindingFlags.Static));
    }

    [Fact]
    public void ExpectedFailureCategoriesContainNoRawDetails()
    {
        var id = Guid.NewGuid();
        var invalid = MigrationEvidenceRecord.FromException(MigrationEvidencePhase.Migration,
            new InvalidOperationException("Invalid legacy rule notification: " + id.ToString("D") + "."), true);
        Assert.Equal(MigrationEvidenceReason.InvalidNotification, invalid.Reason);
        Assert.Equal(id, invalid.RuleId);
        var unsafeMessage = MigrationEvidenceRecord.FromException(MigrationEvidencePhase.Migration,
            new InvalidOperationException("Invalid legacy rule notification: password=synthetic"), true);
        Assert.Equal(MigrationEvidenceReason.Unexpected, unsafeMessage.Reason);
        Assert.Null(unsafeMessage.RuleId);
        var oldWriter = MigrationEvidenceRecord.FromException(MigrationEvidencePhase.Migration,
            new InvalidOperationException("governance_migration_preflight_failed: old_writer_unfenced"), true);
        Assert.Equal(MigrationEvidenceReason.OldWriterUnfenced, oldWriter.Reason);
        Assert.DoesNotContain("password", unsafeMessage.ToString());
    }

    private sealed class TestJournal : IMigrationEvidenceJournal
    {
        public Task<IReadOnlyList<Guid>> ReadCommittedAttemptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task StartCheckedAsync(MigrationAttemptStart start, IReadOnlyList<Guid> checkedCommittedAttempts, CancellationToken ct) => StartAsync(start, ct);
        public bool FailStart { get; init; }
        public bool FailAppend { get; init; }
        public MigrationAttemptStart? Start { get; private set; }
        public List<MigrationEvidenceRecord> Records { get; } = [];
        public Task StartAsync(MigrationAttemptStart start, CancellationToken ct)
        {
            if (FailStart) throw new IOException("synthetic-start-failure");
            Start = start;
            return Task.CompletedTask;
        }
        public Task AppendAsync(Guid attemptId, MigrationEvidenceRecord record, CancellationToken ct)
        {
            if (FailAppend) throw new IOException("synthetic-append-failure");
            Assert.Equal(Start?.AttemptId, attemptId);
            Records.Add(record);
            return Task.CompletedTask;
        }
        public Task<MigrationEvidenceRead> ReadAsync(Guid attemptId, CancellationToken ct) =>
            Task.FromResult(new MigrationEvidenceRead(Start?.AttemptId == attemptId ? MigrationEvidenceReadState.Incomplete : MigrationEvidenceReadState.Missing, Start, Records.ToArray()));
    }

    private sealed class SwitchOnStartJournal(MigrationEvidenceJournal inner, WebApiDbContext context, string newConnectionString) : IMigrationEvidenceJournal
    {
        public Task<IReadOnlyList<Guid>> ReadCommittedAttemptsAsync(CancellationToken ct) => inner.ReadCommittedAttemptsAsync(ct);
        public Task StartAsync(MigrationAttemptStart start, CancellationToken ct) => inner.StartAsync(start, ct);
        public async Task StartCheckedAsync(MigrationAttemptStart start, IReadOnlyList<Guid> history, CancellationToken ct)
        {
            await inner.StartCheckedAsync(start, history, ct);
            context.Database.GetDbConnection().ConnectionString = newConnectionString;
        }
        public Task AppendAsync(Guid id, MigrationEvidenceRecord record, CancellationToken ct) => inner.AppendAsync(id, record, ct);
        public Task<MigrationEvidenceRead> ReadAsync(Guid id, CancellationToken ct) => inner.ReadAsync(id, ct);
    }

    private sealed class CommitInterruption : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new IOException("synthetic commit interruption");
    }

    private sealed class CheckEvidenceBeforeRegistration(string path) : DbTransactionInterceptor
    {
        public bool Reached { get; private set; }
        public override ValueTask<DbTransaction> TransactionUsedAsync(DbConnection connection, TransactionEventData eventData,
            DbTransaction result, CancellationToken cancellationToken = default)
        {
            Reached = true;
            if (Directory.EnumerateFiles(path, "*.start.json").Count() != 1)
                throw new InvalidOperationException("fixture_evidence_not_durable_before_registration");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DelayUntilTransactionDeadline : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("$WEBAPI_HISTORY_FENCE$", StringComparison.Ordinal))
            {
                Reached = true;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return result;
        }
    }
}

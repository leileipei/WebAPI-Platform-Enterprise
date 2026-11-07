using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using WebApi.Infrastructure.Persistence;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[SupportedOSPlatform("linux")]
[Collection("MigrationIsolation")]
public sealed class MigrationSqlTraceTests
{
    private static readonly MigrationSqlBudget SqlBudget = new(TimeSpan.FromMinutes(15), 100000);
    private static async Task<PostgresDatabase> NewDatabaseAsync()
    {
        var database = new PostgresDatabase();
        Console.WriteLine("isolated-test-db=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        await database.InitializeAsync();
        return database;
    }

    private static Task<string> ScriptAsync()
    {
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        return File.ReadAllTextAsync(Path.Combine(root, "deploy", "initial-core.sql"));
    }

    private static string ScriptPath() => Path.Combine(Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT")
        ?? throw new InvalidOperationException("Isolated source root is required."), "deploy", "initial-core.sql");

    private static string NewEvidenceDirectory()
    {
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var path = Path.Combine(root, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static MigrationEvidenceJournal NewJournal() => new(new MigrationEvidenceOptions(NewEvidenceDirectory(), 8192, 16777216, 1048576));

    private static async Task<string> HashAsync(string path) => Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static async Task ConfigureAsync(NpgsqlConnection connection, string seconds = "900", string rows = "100000", Guid? id = null)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('webapi.migration_transaction_seconds',@seconds,false),set_config('webapi.migration_receipt_max_rows',@rows,false),set_config('webapi.migration_attempt_id',@attempt,false)", connection);
        command.Parameters.AddWithValue("seconds", seconds);
        command.Parameters.AddWithValue("rows", rows);
        command.Parameters.AddWithValue("attempt", id?.ToString("D") ?? "");
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task StandaloneSqlRequiresBoundedTransactionAndKeepsEntryContract()
    {
        var database = await NewDatabaseAsync();
        var script = await ScriptAsync();
        // This negative case must remain unconfigured even when the surrounding
        // legacy regression explicitly opts into isolated SQL test settings.
        var unconfigured = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        unconfigured.Remove("Options");
        await using (var connection = new NpgsqlConnection(unconfigured.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(script, connection);
            await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        }
        await using (var check = database.Context())
            Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task DirectSqlCanRepeatButUnmatchedSqlReceiptsBlockControlledEntries()
    {
        var database = await NewDatabaseAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            {
                for (var index = 0; index < 2; index++)
                {
                    await ConfigureAsync(connection);
                    await using var install = new NpgsqlCommand(await ScriptAsync(), connection);
                    await install.ExecuteNonQueryAsync();
                }
            }
            var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
            GovernanceMigrationRunner.Configure(options);
            await using (var managed = new WebApiDbContext(options.Options))
            {
                var result = await MigrationAttemptCoordinator.RunAsync(managed,
                    new MigrationBudget(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864), NewJournal());
                Assert.Equal(MigrationEvidenceOutcome.Unknown, result.Outcome);
                Assert.Equal(MigrationEvidenceReason.PendingReview, result.Reason);
            }
            var controlledSql = await MigrationSqlAttemptCoordinator.RunAsync(database.ConnectionString,
                new MigrationSqlBudget(TimeSpan.FromMinutes(15), 100000), NewJournal(), ScriptPath(), await HashAsync(ScriptPath()));
            Assert.Equal(MigrationEvidenceOutcome.Unknown, controlledSql.Outcome);
            Assert.Equal(MigrationEvidenceReason.PendingReview, controlledSql.Reason);
            await using var check = database.Context();
            Assert.Equal(2, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts").SingleAsync());
        }
        finally { NpgsqlConnection.ClearAllPools(); Console.WriteLine("isolated-direct-sql-db-and-evidence-retained"); }
    }

    [Fact]
    public async Task SqlSuccessReceiptIsNotAClaimOfManagedValidation()
    {
        var database = await NewDatabaseAsync();
        var id = Guid.NewGuid();
        await using (var connection = await database.OpenAsync())
        {
            await ConfigureAsync(connection, id: id);
            await using var command = new NpgsqlCommand(await ScriptAsync(), connection);
            await command.ExecuteNonQueryAsync();
        }
        await using (var check = database.Context())
            Assert.True(await check.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={id} AND entry_point='Sql' AND validation_state='NotApplicable' AND settings_present=false AND rule_count=0 AND total_bytes=0 AND settings_max_bytes IS NULL AND rule_max_bytes IS NULL AND rule_max_count IS NULL AND total_max_bytes IS NULL) AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task SqlFailureWithoutCollectorIsUnknown()
    {
        var database = await NewDatabaseAsync();
        var id = Guid.NewGuid();
        await using (var connection = await database.OpenAsync())
        {
            await ConfigureAsync(connection, seconds: "0", id: id);
            await using var command = new NpgsqlCommand(await ScriptAsync(), connection);
            await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        }
        await using (var check = database.Context())
        {
            var report = await MigrationReconciler.InspectAsync(check, new MissingJournal(), id);
            Assert.Equal(MigrationReceiptObservation.Missing, report.Receipt);
            Assert.Equal(MigrationEvidenceOutcome.Unknown, report.Outcome);
        }
        await database.DisposeAsync();
    }

    [Fact]
    public async Task DirectSqlCanRunTwiceOnSameConnectionWithDistinctAttempts()
    {
        var database = await NewDatabaseAsync();
        var script = await ScriptAsync();
        await using (var connection = await database.OpenAsync())
        {
            await ConfigureAsync(connection);
            await using (var first = new NpgsqlCommand(script, connection)) await first.ExecuteNonQueryAsync();
            await using (var second = new NpgsqlCommand(script, connection)) await second.ExecuteNonQueryAsync();
        }
        await using (var check = database.Context())
        {
            Assert.Equal(2, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts WHERE entry_point='Sql'").SingleAsync());
            Assert.Equal(1, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts WHERE entry_point='Sql' AND validation_state='NotApplicable'").SingleAsync());
            Assert.Equal(1, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts WHERE entry_point='Sql' AND validation_state='Unknown'").SingleAsync());
        }
        await database.DisposeAsync();
    }

    [Fact]
    public async Task DirectSqlReportsGeneratedAttemptIdAfterCommit()
    {
        var database = await NewDatabaseAsync();
        Guid? id = null;
        await using (var connection = await database.OpenAsync())
        {
            await ConfigureAsync(connection);
            await using var command = new NpgsqlCommand(await ScriptAsync(), connection);
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                if (reader.FieldCount == 1 && reader.GetName(0) == "migration_attempt_id" && await reader.ReadAsync())
                    id = Guid.Parse(reader.GetString(0));
            } while (await reader.NextResultAsync());
        }
        Assert.NotNull(id);
        await using (var check = database.Context())
            Assert.True(await check.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={id!.Value}) AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task StandaloneSqlPreservesShorterTransactionTimeout()
    {
        var database = await NewDatabaseAsync();
        var script = await ScriptAsync();
        var commit = script.LastIndexOf("\nCOMMIT;", StringComparison.Ordinal);
        Assert.True(commit > 0);
        var delayed = script.Insert(commit, "\nSELECT pg_catalog.pg_sleep(3);\n");
        await using (var connection = await database.OpenAsync())
        {
            await ConfigureAsync(connection);
            await using (var timeout = new NpgsqlCommand("SET transaction_timeout='1s'", connection)) await timeout.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(delayed, connection) { CommandTimeout = 10 };
            var error = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
            Assert.NotNull(error);
            Assert.True(error is NpgsqlException or IOException or TimeoutException);
        }
        await using (var check = database.Context())
            Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task SqlReceiptQuotaRejectsAnotherAttemptWithoutOverwriting()
    {
        var database = await NewDatabaseAsync();
        var script = await ScriptAsync();
        await using (var connection = await database.OpenAsync())
        {
            await ConfigureAsync(connection, rows: "1");
            await using (var first = new NpgsqlCommand(script, connection)) await first.ExecuteNonQueryAsync();
            await using var second = new NpgsqlCommand(script, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
            Assert.Contains("migration_receipt_capacity_exceeded", error.MessageText);
        }
        await using (var check = database.Context())
            Assert.Equal(1, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts WHERE entry_point='Sql'").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task ReceiptSchemaFingerprintMatchesFourIndependentInstalls()
    {
        var databases = new List<PostgresDatabase>();
        var fingerprints = new List<string>();
        foreach (var sqlEntry in new[] { false, false, true, true })
        {
            var database = await NewDatabaseAsync();
            databases.Add(database);
            if (sqlEntry)
            {
                await using var connection = await database.OpenAsync();
                await ConfigureAsync(connection);
                await using var command = new NpgsqlCommand(await ScriptAsync(), connection);
                await command.ExecuteNonQueryAsync();
            }
            await using var check = database.Context();
            if (!sqlEntry) await check.Database.MigrateAsync();
            fingerprints.Add(await check.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
            Console.WriteLine("receipt-schema-fingerprint=" + fingerprints[^1] + "; installer=" + (sqlEntry ? "Sql" : "EF"));
        }
        Assert.All(fingerprints, value => Assert.Equal(fingerprints[0], value));
        Assert.True(GovernanceSchemaFingerprints.Expected.TryGetValue("20261104090000_Governance24MigrationReceipts", out var registered));
        Assert.Equal(registered, fingerprints[0]);
        foreach (var database in databases) await database.DisposeAsync();
    }

    [Fact]
    public async Task ControlledSqlUsesExactBytesAndOneEvidenceAttempt()
    {
        var database = await NewDatabaseAsync();
        var journal = NewJournal();
        var path = ScriptPath();
        var hash = await HashAsync(path);
        var result = await MigrationSqlAttemptCoordinator.RunAsync(database.ConnectionString, SqlBudget, journal, path, hash);
        Assert.Equal(MigrationEvidenceOutcome.Committed, result.Outcome);
        var evidence = await journal.ReadAsync(result.AttemptId, CancellationToken.None);
        Assert.Equal(MigrationEvidenceReadState.Complete, evidence.State);
        Assert.Equal(MigrationReceiptEntry.Sql, evidence.Start!.EntryPoint);
        Assert.Null(evidence.Start.Budget);
        Assert.Equal(SqlBudget, evidence.Start.SqlBudget);
        Assert.Equal(hash, evidence.Start.SqlHash);
        await using (var check = database.Context())
            Assert.True(await check.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={result.AttemptId} AND entry_point='Sql') AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task ControlledSqlHashMismatchStopsBeforeEvidenceAndDatabaseWrites()
    {
        var database = await NewDatabaseAsync();
        var journal = new TrackingJournal();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationSqlAttemptCoordinator.RunAsync(database.ConnectionString, SqlBudget, journal, ScriptPath(), new string('0', 64)));
        Assert.Equal(0, journal.Starts);
        await using (var check = database.Context())
            Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task ControlledSqlUnsafeEvidenceStopsBeforeDatabaseWrites()
    {
        var database = await NewDatabaseAsync();
        var path = NewEvidenceDirectory();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        var journal = new MigrationEvidenceJournal(new MigrationEvidenceOptions(path, 8192, 16777216, 1048576));
        var sqlPath = ScriptPath();
        var result = await MigrationSqlAttemptCoordinator.RunAsync(database.ConnectionString, SqlBudget, journal, sqlPath, await HashAsync(sqlPath));
        Assert.Equal(MigrationEvidenceOutcome.Unknown, result.Outcome);
        Assert.Equal(MigrationEvidenceReason.Storage, result.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        await using (var check = database.Context())
            Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task ControlledSqlErrorAfterCommitRemainsUnknown()
    {
        var database = await NewDatabaseAsync();
        var journal = NewJournal();
        var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var path = Path.Combine(root, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "sql-failure-" + Guid.NewGuid().ToString("N") + ".sql");
        await File.WriteAllTextAsync(path, (await ScriptAsync()) + "\nSELECT 1/0;\n");
        var result = await MigrationSqlAttemptCoordinator.RunAsync(database.ConnectionString, SqlBudget, journal, path, await HashAsync(path));
        Assert.Equal(MigrationEvidenceOutcome.Unknown, result.Outcome);
        var evidence = await journal.ReadAsync(result.AttemptId, CancellationToken.None);
        Assert.Equal(MigrationEvidenceReadState.Incomplete, evidence.State);
        Assert.Equal(MigrationEvidenceOutcome.Unknown, Assert.Single(evidence.Records).Outcome);
        await using (var check = database.Context())
            Assert.True(await check.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={result.AttemptId} AND entry_point='Sql') AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task CommandLineSqlModeUsesOnlyItsTwoExplicitBudgets()
    {
        var database = await NewDatabaseAsync();
        var path = ScriptPath();
        var (exitCode, output) = await RunSqlCommandAsync(database, path, await HashAsync(path));
        Assert.Equal(0, exitCode);
        Assert.Contains("Committed", output);
        Assert.DoesNotContain("password", output, StringComparison.OrdinalIgnoreCase);
        await using (var check = database.Context())
            Assert.Equal(1, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts WHERE entry_point='Sql'").SingleAsync());
        await database.DisposeAsync();
    }

    [Fact]
    public async Task CommandLineSqlHashMismatchDoesNotChangeDatabase()
    {
        var database = await NewDatabaseAsync();
        var (exitCode, output) = await RunSqlCommandAsync(database, ScriptPath(), new string('0', 64));
        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("password", output, StringComparison.OrdinalIgnoreCase);
        await using (var check = database.Context())
            Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"").SingleAsync());
        await database.DisposeAsync();
    }

    private static async Task<(int ExitCode, string Output)> RunSqlCommandAsync(PostgresDatabase database, string path, string hash)
    {
        var dll = Environment.GetEnvironmentVariable("WEBAPI_MIGRATOR_DLL") ?? throw new InvalidOperationException("Isolated migrator is required.");
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--run-sql");
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/share/dotnet";
        start.Environment["WEBAPI_DB_HOST"] = connection.Host!;
        start.Environment["WEBAPI_DB_NAME"] = connection.Database!;
        start.Environment["WEBAPI_DB_USER"] = connection.Username!;
        start.Environment["WEBAPI_DB_PASSWORD_FILE"] = "/run/secrets/postgres-password";
        start.Environment["WEBAPI_MIGRATION_TRANSACTION_SECONDS"] = "900";
        start.Environment["WEBAPI_MIGRATION_RECEIPT_MAX_ROWS"] = "100000";
        start.Environment["WEBAPI_MIGRATION_EVIDENCE_DIR"] = NewEvidenceDirectory();
        start.Environment["WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES"] = "8192";
        start.Environment["WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES"] = "16777216";
        start.Environment["WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES"] = "1048576";
        start.Environment["WEBAPI_MIGRATION_SQL_FILE"] = path;
        start.Environment["WEBAPI_MIGRATION_SQL_SHA256"] = hash;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Isolated migrator could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, (await output) + (await error));
    }

    private sealed class MissingJournal : IMigrationEvidenceJournal
    {
        public Task<IReadOnlyList<Guid>> ReadCommittedAttemptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task StartCheckedAsync(MigrationAttemptStart start, IReadOnlyList<Guid> checkedCommittedAttempts, CancellationToken ct) => StartAsync(start, ct);
        public Task StartAsync(MigrationAttemptStart start, CancellationToken ct) => throw new InvalidOperationException();
        public Task AppendAsync(Guid attemptId, MigrationEvidenceRecord record, CancellationToken ct) => throw new InvalidOperationException();
        public Task<MigrationEvidenceRead> ReadAsync(Guid attemptId, CancellationToken ct) =>
            Task.FromResult(new MigrationEvidenceRead(MigrationEvidenceReadState.Missing, null, []));
    }

    private sealed class TrackingJournal : IMigrationEvidenceJournal
    {
        public Task<IReadOnlyList<Guid>> ReadCommittedAttemptsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task StartCheckedAsync(MigrationAttemptStart start, IReadOnlyList<Guid> checkedCommittedAttempts, CancellationToken ct) => StartAsync(start, ct);
        public int Starts { get; private set; }
        public Task StartAsync(MigrationAttemptStart start, CancellationToken ct) { Starts++; return Task.CompletedTask; }
        public Task AppendAsync(Guid attemptId, MigrationEvidenceRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task<MigrationEvidenceRead> ReadAsync(Guid attemptId, CancellationToken ct) =>
            Task.FromResult(new MigrationEvidenceRead(MigrationEvidenceReadState.Missing, null, []));
    }
}

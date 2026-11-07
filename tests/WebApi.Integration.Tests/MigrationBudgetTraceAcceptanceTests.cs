using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Infrastructure.Persistence;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[SupportedOSPlatform("linux")]
[Collection("MigrationIsolation")]
public sealed class MigrationBudgetTraceAcceptanceTests
{
    private const string Legacy = "20261015020000_CredentialUsageTracking";
    private const string FixturePassword = "isolated-owned-writer-only-20261105";
    private static string SourceRoot => Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
    private static string Migrator => Environment.GetEnvironmentVariable("WEBAPI_MIGRATOR_DLL") ?? throw new InvalidOperationException("Isolated migrator is required.");

    private static async Task<PostgresDatabase> NewDatabaseAsync()
    {
        var database = new PostgresDatabase();
        Console.WriteLine("isolated-test-db=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        await database.InitializeAsync();
        return database;
    }

    private static string NewEvidenceDirectory()
    {
        var directory = Path.Combine(SourceRoot, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Console.WriteLine("isolated-evidence=" + directory);
        return directory;
    }

    private static MigrationEvidenceJournal Journal(string directory) => new(new MigrationEvidenceOptions(directory, 8192, 16777216, 1048576));

    private static Process StartMigrator(PostgresDatabase database, string evidence, bool sql)
    {
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        var command = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        command.ArgumentList.Add(Migrator);
        if (sql) command.ArgumentList.Add("--run-sql");
        command.Environment.Clear();
        command.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
        command.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/share/dotnet";
        command.Environment["WEBAPI_DB_HOST"] = settings.Host!;
        command.Environment["WEBAPI_DB_NAME"] = settings.Database!;
        command.Environment["WEBAPI_DB_USER"] = settings.Username!;
        command.Environment["WEBAPI_DB_PASSWORD_FILE"] = "/run/secrets/postgres-password";
        command.Environment["WEBAPI_MIGRATION_TRANSACTION_SECONDS"] = "900";
        command.Environment["WEBAPI_MIGRATION_RECEIPT_MAX_ROWS"] = "100000";
        command.Environment["WEBAPI_MIGRATION_EVIDENCE_DIR"] = evidence;
        command.Environment["WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES"] = "8192";
        command.Environment["WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES"] = "16777216";
        command.Environment["WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES"] = "1048576";
        if (sql)
        {
            var script = Path.Combine(SourceRoot, "deploy", "initial-core.sql");
            command.Environment["WEBAPI_MIGRATION_SQL_FILE"] = script;
            command.Environment["WEBAPI_MIGRATION_SQL_SHA256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(script)));
        }
        else
        {
            command.Environment["WEBAPI_MIGRATION_SETTINGS_MAX_BYTES"] = "65536";
            command.Environment["WEBAPI_MIGRATION_RULE_MAX_BYTES"] = "16384";
            command.Environment["WEBAPI_MIGRATION_RULE_MAX_COUNT"] = "100000";
            command.Environment["WEBAPI_MIGRATION_TOTAL_MAX_BYTES"] = "67108864";
        }
        var process = Process.Start(command) ?? throw new InvalidOperationException("Cannot start isolated migrator.");
        Console.WriteLine("isolated-migrator-pid=" + process.Id + "; entry=" + (sql ? "Sql" : "Managed"));
        return process;
    }

    private static async Task<(int ExitCode, string Output)> FinishAsync(Process process, TimeSpan timeout)
    {
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, (await output) + (await error));
    }

    private static HashSet<Guid> AttemptIds(string directory) => Directory.EnumerateFiles(directory, "*.start.json")
        .Select(file => Guid.ParseExact(Path.GetFileName(file).Split('.')[0], "N")).ToHashSet();

    private static async Task<NpgsqlConnection> MonitorAsync(PostgresDatabase database)
    {
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres", Pooling = false };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> ActivityAsync(NpgsqlConnection monitor, string database, string condition)
    {
        // Condition is selected only from these local test constants, never user input.
        if (condition is not ("waiting" or "transaction" or "none")) throw new InvalidOperationException("Invalid test condition.");
        var predicate = condition switch
        {
            "waiting" => "wait_event_type='Lock'",
            "transaction" => "xact_start IS NOT NULL",
            _ => "TRUE"
        };
        await using var query = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE datname=@database AND backend_type='client backend' AND " + predicate + ")", monitor);
        query.Parameters.AddWithValue("database", database);
        var exists = (bool)(await query.ExecuteScalarAsync())!;
        return condition == "none" ? !exists : exists;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, Process process, TimeSpan timeout)
    {
        var end = Stopwatch.StartNew();
        while (end.Elapsed < timeout)
        {
            if (await condition()) return;
            if (process.HasExited) throw new InvalidOperationException("Isolated migrator exited before the synchronized test point.");
            await Task.Delay(25);
        }
        throw new TimeoutException("Isolated migration did not reach the synchronized test point.");
    }

    private static async Task WaitUntilLockHeldAsync(string lockPath, Process holder)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            var command = new ProcessStartInfo("flock") { UseShellExecute = false };
            foreach (var arg in new[] { "-n", "-x", lockPath, "/usr/bin/true" }) command.ArgumentList.Add(arg);
            using var probe = Process.Start(command) ?? throw new InvalidOperationException("Cannot inspect isolated evidence lock.");
            await probe.WaitForExitAsync();
            if (probe.ExitCode == 1) return;
            Assert.Equal(0, probe.ExitCode);
            if (holder.HasExited) throw new InvalidOperationException("Isolated evidence lock holder exited.");
            await Task.Delay(25);
        }
        throw new TimeoutException("Isolated evidence lock was not acquired.");
    }

    private static async Task<bool> ReceiptVisibleAsync(PostgresDatabase database, Guid attempt)
    {
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using (var table = new NpgsqlCommand("SELECT pg_catalog.to_regclass('public.migration_validation_receipts') IS NOT NULL", connection))
            if (!(bool)(await table.ExecuteScalarAsync())!) return false;
        await using var receipt = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id=@id)", connection);
        receipt.Parameters.AddWithValue("id", attempt);
        return (bool)(await receipt.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledWriterStaysFencedAcrossManagedAndSqlRecovery(bool sql)
    {
        var database = await NewDatabaseAsync();
        var evidence = NewEvidenceDirectory();
        var roleName = "gov24_accept_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-test-role=" + roleName);
        using var identifier = new NpgsqlCommandBuilder();
        var role = identifier.QuoteIdentifier(roleName);
        var organization = Guid.NewGuid();
        var passed = false;
        try
        {
            await using (var setup = database.Context())
            {
                await setup.GetService<IMigrator>().MigrateAsync(Legacy);
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({organization},'TRACE','Preserve','Active',now(),now())");
            }
            await using (var admin = await database.OpenAsync())
            await using (var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{FixturePassword}'; GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT,UPDATE ON public.organizations TO {role}", admin))
                await create.ExecuteNonQueryAsync();
            var oldSettings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = roleName, Password = FixturePassword, Pooling = false };
            async Task RejectedLoginAsync()
            {
                await using var attempt = new NpgsqlConnection(oldSettings.ConnectionString);
                var error = await Assert.ThrowsAsync<PostgresException>(() => attempt.OpenAsync());
                Assert.Equal("28000", error.SqlState);
            }
            await using (var old = new NpgsqlConnection(oldSettings.ConnectionString))
            {
                await old.OpenAsync();
                await using (var before = await old.BeginTransactionAsync())
                {
                    await using var write = new NpgsqlCommand("UPDATE public.organizations SET name=name WHERE id=@id", old, before);
                    write.Parameters.AddWithValue("id", organization);
                    Assert.Equal(1, await write.ExecuteNonQueryAsync());
                    await before.RollbackAsync();
                }
                await using (var admin = await database.OpenAsync())
                await using (var disable = new NpgsqlCommand($"ALTER ROLE {role} NOLOGIN", admin)) await disable.ExecuteNonQueryAsync();
                await RejectedLoginAsync();
                // NOLOGIN alone does not revoke an already established session.
                await using (var existing = await old.BeginTransactionAsync())
                {
                    await using var write = new NpgsqlCommand("UPDATE public.organizations SET name=name WHERE id=@id", old, existing);
                    write.Parameters.AddWithValue("id", organization);
                    Assert.Equal(1, await write.ExecuteNonQueryAsync());
                    await existing.RollbackAsync();
                }
                NpgsqlConnection.ClearAllPools();
                using var blocked = StartMigrator(database, evidence, sql);
                var result = await FinishAsync(blocked, TimeSpan.FromSeconds(45));
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("OldWriterUnfenced", result.Output);
            }
            await RejectedLoginAsync();
            NpgsqlConnection.ClearAllPools();
            var previousAttempts = AttemptIds(evidence);
            Assert.Single(previousAttempts);
            if (sql)
            {
                // SQL execution may have failed after COMMIT, so the controlled
                // entry conservatively keeps that first attempt unknown.
                Assert.Equal(MigrationEvidenceReadState.Incomplete,
                    (await Journal(evidence).ReadAsync(Assert.Single(previousAttempts), CancellationToken.None)).State);
                using var retry = StartMigrator(database, evidence, sql: true);
                var blocked = await FinishAsync(retry, TimeSpan.FromSeconds(60));
                Assert.NotEqual(0, blocked.ExitCode);
                Assert.Contains("PendingReview", blocked.Output);
                Assert.Single(AttemptIds(evidence));
                await RejectedLoginAsync();
                passed = true;
                return;
            }
            var dbName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
            await using var monitor = await MonitorAsync(database);
            Guid interrupted;
            await using (var barrier = await database.OpenAsync())
            await using (var transaction = await barrier.BeginTransactionAsync())
            {
                await using (var hold = new NpgsqlCommand("LOCK TABLE public.organizations IN ACCESS EXCLUSIVE MODE", barrier, transaction)) await hold.ExecuteNonQueryAsync();
                using var process = StartMigrator(database, evidence, sql);
                try
                {
                    await WaitUntilAsync(() => ActivityAsync(monitor, dbName, "waiting"), process, TimeSpan.FromSeconds(20));
                    await RejectedLoginAsync();
                    process.Kill(entireProcessTree: true);
                    var killed = await FinishAsync(process, TimeSpan.FromSeconds(10));
                    Assert.NotEqual(0, killed.ExitCode);
                }
                finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
                var newAttempts = AttemptIds(evidence).Except(previousAttempts).ToArray();
                interrupted = Assert.Single(newAttempts);
                Assert.Equal(MigrationEvidenceReadState.Incomplete, (await Journal(evidence).ReadAsync(interrupted, CancellationToken.None)).State);
                await transaction.RollbackAsync();
            }
            NpgsqlConnection.ClearAllPools();
            await RejectedLoginAsync();
            await using (var check = database.Context())
            {
                Assert.Equal(21, (await check.Database.GetAppliedMigrationsAsync()).Count());
                Assert.Equal("Preserve", await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM public.organizations WHERE id={organization}").SingleAsync());
            }
            NpgsqlConnection.ClearAllPools();
            // A process interruption remains unresolved and cannot trigger a new attempt.
            using (var retry = StartMigrator(database, evidence, sql))
            {
                var result = await FinishAsync(retry, TimeSpan.FromSeconds(60));
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("PendingReview", result.Output);
            }
            Assert.Equal(MigrationEvidenceReadState.Incomplete, (await Journal(evidence).ReadAsync(interrupted, CancellationToken.None)).State);
            await RejectedLoginAsync();
            await using (var check = database.Context())
            {
                Assert.NotEmpty(await check.Database.GetPendingMigrationsAsync());
                Assert.Equal("Preserve", await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM public.organizations WHERE id={organization}").SingleAsync());
            }
            passed = true;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Console.WriteLine("isolated-writer-db-role-and-evidence-retained; assertions-passed=" + passed);
        }
    }

    [Fact]
    public async Task CrashAfterCommitBeforeEvidenceRemainsUnknown()
    {
        var database = await NewDatabaseAsync();
        var evidence = NewEvidenceDirectory();
        var passed = false;
        Process? process = null;
        Process? holder = null;
        try
        {
            NpgsqlConnection.ClearAllPools();
            process = StartMigrator(database, evidence, sql: false);
            await WaitUntilAsync(() => Task.FromResult(AttemptIds(evidence).Count == 1), process, TimeSpan.FromSeconds(20));
            var id = Assert.Single(AttemptIds(evidence));
            var lockPath = Path.Combine(evidence, ".journal.lock");
            var holderCommand = new ProcessStartInfo("flock") { UseShellExecute = false };
            foreach (var arg in new[] { "--no-fork", "-x", lockPath, "/bin/sleep", "30" }) holderCommand.ArgumentList.Add(arg);
            holder = Process.Start(holderCommand) ?? throw new InvalidOperationException("Cannot start isolated lock holder.");
            await WaitUntilLockHeldAsync(lockPath, holder);
            // This is a fresh empty database: its preflight does not require all
            // other client sessions to be absent. Poll the actual committed row
            // rather than inferring COMMIT from a backend's transient idle state.
            await WaitUntilAsync(() => ReceiptVisibleAsync(database, id), process, TimeSpan.FromSeconds(20));
            Assert.False(process.HasExited);
            await using (var check = database.Context())
                Assert.True(await check.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={id}) AS \"Value\"").SingleAsync());
            Assert.False(process.HasExited);
            process.Kill(entireProcessTree: true);
            var killed = await FinishAsync(process, TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, killed.ExitCode);
            holder.Kill();
            await holder.WaitForExitAsync();
            holder.Dispose();
            holder = null;
            var read = await Journal(evidence).ReadAsync(id, CancellationToken.None);
            Assert.Equal(MigrationEvidenceReadState.Incomplete, read.State);
            Assert.Empty(read.Records);
            await using (var check = database.Context())
            {
                var report = await MigrationReconciler.InspectAsync(check, Journal(evidence), id);
                Assert.Equal(MigrationReceiptObservation.Present, report.Receipt);
                Assert.Equal(MigrationEvidenceOutcome.Unknown, report.Outcome);
            }
            Assert.Single(AttemptIds(evidence));
            using (var retry = StartMigrator(database, evidence, sql: false))
            {
                var blocked = await FinishAsync(retry, TimeSpan.FromSeconds(60));
                Assert.NotEqual(0, blocked.ExitCode);
                Assert.Contains("PendingReview", blocked.Output);
                Assert.Single(AttemptIds(evidence));
            }
            passed = true;
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                if (!passed)
                {
                    var output = (await process.StandardOutput.ReadToEndAsync()) + (await process.StandardError.ReadToEndAsync());
                    var safe = output.All(c => char.IsAsciiLetterOrDigit(c) || " -_:;=.\r\n".Contains(c));
                    var outputPath = Path.Combine(SourceRoot, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "child-output-" + process.Id + "-" + Guid.NewGuid().ToString("N") + ".log");
                    await File.WriteAllTextAsync(outputPath, safe ? output : "child-output-redacted\n");
                }
                process.Dispose();
            }
            if (holder is not null)
            {
                if (!holder.HasExited) { holder.Kill(); await holder.WaitForExitAsync(); }
                holder.Dispose();
            }
            NpgsqlConnection.ClearAllPools();
            Console.WriteLine("isolated-postcommit-db-and-evidence-retained; assertions-passed=" + passed);
        }
    }

    [Fact]
    public async Task SqlWriterRemainsFencedDuringInterruptedMigration()
    {
        var database = await NewDatabaseAsync();
        var evidence = NewEvidenceDirectory();
        var roleName = "gov24_sql_fence_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-test-role=" + roleName);
        using var identifier = new NpgsqlCommandBuilder();
        var role = identifier.QuoteIdentifier(roleName);
        var organization = Guid.NewGuid();
        try
        {
            await using (var setup = database.Context())
            {
                await setup.GetService<IMigrator>().MigrateAsync(Legacy);
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({organization},'SQLFENCE','Preserve','Active',now(),now())");
            }
            await using (var admin = await database.OpenAsync())
            await using (var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{FixturePassword}'; GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT,UPDATE ON public.organizations TO {role}", admin)) await create.ExecuteNonQueryAsync();
            var oldSettings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = roleName, Password = FixturePassword, Pooling = false };
            await using (var old = new NpgsqlConnection(oldSettings.ConnectionString))
            {
                await old.OpenAsync();
                await using var transaction = await old.BeginTransactionAsync();
                await using var write = new NpgsqlCommand("UPDATE public.organizations SET name=name WHERE id=@id", old, transaction);
                write.Parameters.AddWithValue("id", organization);
                Assert.Equal(1, await write.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }
            await using (var admin = await database.OpenAsync())
            await using (var disable = new NpgsqlCommand($"ALTER ROLE {role} NOLOGIN", admin)) await disable.ExecuteNonQueryAsync();
            async Task RejectOldLogin()
            {
                await using var attempt = new NpgsqlConnection(oldSettings.ConnectionString);
                Assert.Equal("28000", (await Assert.ThrowsAsync<PostgresException>(() => attempt.OpenAsync())).SqlState);
            }
            NpgsqlConnection.ClearAllPools();
            await using var monitor = await MonitorAsync(database);
            var dbName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
            await using (var barrier = await database.OpenAsync())
            await using (var transaction = await barrier.BeginTransactionAsync())
            {
                await using (var hold = new NpgsqlCommand("LOCK TABLE public.organizations IN ACCESS EXCLUSIVE MODE", barrier, transaction)) await hold.ExecuteNonQueryAsync();
                using var process = StartMigrator(database, evidence, sql: true);
                try
                {
                    await WaitUntilAsync(() => ActivityAsync(monitor, dbName, "waiting"), process, TimeSpan.FromSeconds(20));
                    await RejectOldLogin();
                    process.Kill(entireProcessTree: true);
                    Assert.NotEqual(0, (await FinishAsync(process, TimeSpan.FromSeconds(10))).ExitCode);
                }
                finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
                await transaction.RollbackAsync();
            }
            await RejectOldLogin();
            var id = Assert.Single(AttemptIds(evidence));
            Assert.Equal(MigrationEvidenceReadState.Incomplete, (await Journal(evidence).ReadAsync(id, CancellationToken.None)).State);
            using (var retry = StartMigrator(database, evidence, sql: true))
            {
                var blocked = await FinishAsync(retry, TimeSpan.FromSeconds(60));
                Assert.NotEqual(0, blocked.ExitCode);
                Assert.Contains("PendingReview", blocked.Output);
            }
            await using var check = database.Context();
            Assert.Equal(21, (await check.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserve", await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM public.organizations WHERE id={organization}").SingleAsync());
        }
        finally { NpgsqlConnection.ClearAllPools(); Console.WriteLine("isolated-sql-interrupted-db-role-and-evidence-retained"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedFailureCanBeExplicitlyRetriedAndOldWriterRemainsDisabled(bool sql)
    {
        var database = await NewDatabaseAsync();
        var evidence = NewEvidenceDirectory();
        var roleName = "gov24_retry_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-test-role=" + roleName);
        using var identifier = new NpgsqlCommandBuilder();
        var role = identifier.QuoteIdentifier(roleName);
        var organization = Guid.NewGuid();
        try
        {
            await using (var setup = database.Context())
            {
                await setup.GetService<IMigrator>().MigrateAsync(Legacy);
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({organization},'RETRY','Preserve','Active',now(),now())");
            }
            await using (var admin = await database.OpenAsync())
            await using (var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{FixturePassword}'; GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT,UPDATE ON public.organizations TO {role}", admin)) await create.ExecuteNonQueryAsync();
            var oldSettings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = roleName, Password = FixturePassword, Pooling = false };
            await using (var old = new NpgsqlConnection(oldSettings.ConnectionString))
            {
                await old.OpenAsync();
                await using var transaction = await old.BeginTransactionAsync();
                await using var write = new NpgsqlCommand("UPDATE public.organizations SET name=name WHERE id=@id", old, transaction);
                write.Parameters.AddWithValue("id", organization);
                Assert.Equal(1, await write.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }
            Guid first;
            if (!sql)
            {
                NpgsqlConnection.ClearAllPools();
                using var blocked = StartMigrator(database, evidence, sql: false);
                var result = await FinishAsync(blocked, TimeSpan.FromSeconds(60));
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("OldWriterUnfenced", result.Output);
                first = Assert.Single(AttemptIds(evidence));
            }
            else first = Guid.Empty;
            await using (var admin = await database.OpenAsync())
            await using (var disable = new NpgsqlCommand($"ALTER ROLE {role} NOLOGIN", admin)) await disable.ExecuteNonQueryAsync();
            async Task RejectOldLogin()
            {
                await using var attempt = new NpgsqlConnection(oldSettings.ConnectionString);
                Assert.Equal("28000", (await Assert.ThrowsAsync<PostgresException>(() => attempt.OpenAsync())).SqlState);
            }
            await RejectOldLogin();
            if (sql)
            {
                var script = Path.Combine(SourceRoot, "deploy", "initial-core.sql");
                var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(script)));
                var failure = await MigrationSqlAttemptCoordinator.RunAsync(oldSettings.ConnectionString,
                    new MigrationSqlBudget(TimeSpan.FromMinutes(15), 100000), Journal(evidence), script, hash);
                Assert.Equal(MigrationEvidenceOutcome.Failed, failure.Outcome);
                first = failure.AttemptId;
            }
            var firstEvidence = await Journal(evidence).ReadAsync(first, CancellationToken.None);
            Assert.Equal(MigrationEvidenceReadState.Complete, firstEvidence.State);
            Assert.Equal(MigrationEvidenceOutcome.Failed, Assert.Single(firstEvidence.Records).Outcome);
            NpgsqlConnection.ClearAllPools();
            using (var retry = StartMigrator(database, evidence, sql))
            {
                var result = await FinishAsync(retry, TimeSpan.FromSeconds(60));
                Assert.True(result.ExitCode == 0, result.Output);
                Assert.Contains("Committed", result.Output);
            }
            await RejectOldLogin();
            Assert.Equal(2, AttemptIds(evidence).Count);
            await using var check = database.Context();
            Assert.Empty(await check.Database.GetPendingMigrationsAsync());
            Assert.Equal("Preserve", await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM public.organizations WHERE id={organization}").SingleAsync());
        }
        finally { NpgsqlConnection.ClearAllPools(); Console.WriteLine("isolated-confirmed-retry-db-role-and-evidence-retained"); }
    }

    [Fact]
    public async Task MissingJournalHistoryBlocksBothEntriesWithoutCreatingAnotherReceipt()
    {
        var database = await NewDatabaseAsync();
        var originalEvidence = NewEvidenceDirectory();
        var budget = new MigrationBudget(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864);
        try
        {
            var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
            GovernanceMigrationRunner.Configure(options);
            await using (var initial = new WebApiDbContext(options.Options))
            {
                var result = await MigrationAttemptCoordinator.RunAsync(initial, budget, Journal(originalEvidence));
                Assert.Equal(MigrationEvidenceOutcome.Committed, result.Outcome);
            }
            NpgsqlConnection.ClearAllPools();
            foreach (var sql in new[] { false, true })
            {
                var emptyEvidence = NewEvidenceDirectory();
                using var attempt = StartMigrator(database, emptyEvidence, sql);
                var result = await FinishAsync(attempt, TimeSpan.FromSeconds(60));
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains("PendingReview", result.Output);
                await using var check = database.Context();
                Assert.Equal(1, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts").SingleAsync());
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Console.WriteLine("isolated-missing-journal-db-and-evidence-retained");
        }
    }

    [Fact]
    public async Task SimulatedDatabaseRollbackDoesNotRewriteOrRetryEvidence()
    {
        var database = await NewDatabaseAsync();
        var evidence = NewEvidenceDirectory();
        var original = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        var before = "gov24_before_" + Guid.NewGuid().ToString("N");
        var committed = "gov24_committed_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-before-db=" + before + "; isolated-committed-db=" + committed);
        using var identifier = new NpgsqlCommandBuilder();
        var originalSql = identifier.QuoteIdentifier(original);
        var beforeSql = identifier.QuoteIdentifier(before);
        var committedSql = identifier.QuoteIdentifier(committed);
        var organization = Guid.NewGuid();
        var passed = false;
        try
        {
            await using (var setup = database.Context())
            {
                await setup.Database.MigrateAsync();
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({organization},'RESTORE','Preserve','Active',now(),now())");
            }
            NpgsqlConnection.ClearAllPools();
            await using var admin = await MonitorAsync(database);
            await using (var clone = new NpgsqlCommand($"CREATE DATABASE {beforeSql} TEMPLATE {originalSql}", admin)) await clone.ExecuteNonQueryAsync();
            var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
            GovernanceMigrationRunner.Configure(options);
            MigrationAttemptResult attempt;
            await using (var migrate = new WebApiDbContext(options.Options))
                attempt = await MigrationAttemptCoordinator.RunAsync(migrate, new MigrationBudget(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864), Journal(evidence));
            Assert.Equal(MigrationEvidenceOutcome.Committed, attempt.Outcome);
            Assert.Equal(MigrationEvidenceReadState.Complete, (await Journal(evidence).ReadAsync(attempt.AttemptId, CancellationToken.None)).State);
            NpgsqlConnection.ClearAllPools();
            // Renaming retains the committed database and its exact OID under a
            // separate owned name; the earlier clone takes the original name.
            await using (var renameCommitted = new NpgsqlCommand($"ALTER DATABASE {originalSql} RENAME TO {committedSql}", admin)) await renameCommitted.ExecuteNonQueryAsync();
            await using (var renameBefore = new NpgsqlCommand($"ALTER DATABASE {beforeSql} RENAME TO {originalSql}", admin)) await renameBefore.ExecuteNonQueryAsync();
            await using (var restored = database.Context())
            {
                var report = await MigrationReconciler.InspectAsync(restored, Journal(evidence), attempt.AttemptId);
                Assert.Equal(MigrationReceiptObservation.Missing, report.Receipt);
                Assert.Equal(MigrationEvidenceOutcome.Unknown, report.Outcome);
                Assert.Equal("Preserve", await restored.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM public.organizations WHERE id={organization}").SingleAsync());
            }
            // A restored database missing the previously committed receipt must
            // not be modified by either controlled entry point.
            foreach (var sql in new[] { false, true })
            {
                using var retry = StartMigrator(database, evidence, sql);
                var blocked = await FinishAsync(retry, TimeSpan.FromSeconds(60));
                Assert.NotEqual(0, blocked.ExitCode);
                Assert.Contains("PendingReview", blocked.Output);
                Assert.Equal(2, AttemptIds(evidence).Count);
            }
            var committedSettings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = committed, Pooling = false };
            await using (var preserved = new NpgsqlConnection(committedSettings.ConnectionString))
            {
                await preserved.OpenAsync();
                await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id=@id)", preserved);
                check.Parameters.AddWithValue("id", attempt.AttemptId);
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            }
            Assert.Equal(2, AttemptIds(evidence).Count);
            Assert.Equal(MigrationEvidenceReadState.Complete, (await Journal(evidence).ReadAsync(attempt.AttemptId, CancellationToken.None)).State);
            passed = true;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            Console.WriteLine("isolated-rollback-databases-and-evidence-retained; assertions-passed=" + passed);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data;
using System.Data.Common;
using Npgsql;
using WebApi.Infrastructure.Persistence;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[Collection("MigrationIsolation")]
public sealed class MigrationBudgetTests
{
    private static MigrationBudget TestBudget => new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864);

    [Fact]
    public async Task RetainedIsolatedDatabaseIsNotDroppedOnFixtureDisposal()
    {
        var database = new PostgresDatabase(retain: true);
        await database.InitializeAsync();
        var expected = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database;
        await database.DisposeAsync();
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT pg_catalog.current_database()", connection);
        Assert.Equal(expected, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OnlyOwnBudgetDeadlineIsClassifiedAsATransactionBudgetHit()
    {
        using var deadline = new MigrationBudgetScope(TestBudget, CancellationToken.None, TimeSpan.FromMilliseconds(30));
        var timedOut = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token));
        Assert.True(deadline.TransactionDeadlineExpired);
        var record = MigrationEvidenceRecord.FromException(MigrationEvidencePhase.Migration, timedOut, rollbackConfirmed: true,
            transactionDeadlineExpired: deadline.TransactionDeadlineExpired);
        Assert.Equal(MigrationEvidenceReason.Budget, record.Reason);
        Assert.Equal(MigrationBudgetCause.TransactionDeadline, record.BudgetCause);
        using var caller = new CancellationTokenSource();
        using var external = new MigrationBudgetScope(TestBudget, caller.Token, TimeSpan.FromSeconds(5));
        caller.Cancel();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, external.Token));
        Assert.False(external.TransactionDeadlineExpired);
        var externalRecord = MigrationEvidenceRecord.FromException(MigrationEvidencePhase.Migration, cancelled, rollbackConfirmed: true,
            transactionDeadlineExpired: external.TransactionDeadlineExpired);
        Assert.Equal(MigrationEvidenceReason.Cancelled, externalRecord.Reason);
        Assert.Null(externalRecord.BudgetCause);
    }

    private static readonly Dictionary<string, string> ValidConfiguration = new(StringComparer.Ordinal)
    {
        ["WEBAPI_MIGRATION_TRANSACTION_SECONDS"] = "900",
        ["WEBAPI_MIGRATION_SETTINGS_MAX_BYTES"] = "65536",
        ["WEBAPI_MIGRATION_RULE_MAX_BYTES"] = "16384",
        ["WEBAPI_MIGRATION_RULE_MAX_COUNT"] = "100000",
        ["WEBAPI_MIGRATION_TOTAL_MAX_BYTES"] = "67108864"
    };

    [Fact]
    public void BudgetRejectsMissingInvalidAndOutOfRangeValues()
    {
        var valid = MigrationBudget.FromEnvironment(key => ValidConfiguration.GetValueOrDefault(key));
        Assert.Equal(TimeSpan.FromMinutes(15), valid.TransactionTimeout);
        Assert.Equal(65536, valid.SettingsMaxBytes);
        Assert.Equal(16384, valid.RuleMaxBytes);
        Assert.Equal(100000, valid.RuleMaxCount);
        Assert.Equal(67108864, valid.TotalMaxBytes);
        valid.Validate();

        (string Key, string? Value)[] invalid =
        [
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", null),
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", ""),
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", "0"),
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", "-1"),
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", "+60"),
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", " 60"),
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", "٦٠"),
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", "99999999999999999999999999"),
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", "59"),
            ("WEBAPI_MIGRATION_TRANSACTION_SECONDS", "3601"),
            ("WEBAPI_MIGRATION_SETTINGS_MAX_BYTES", "0"),
            ("WEBAPI_MIGRATION_SETTINGS_MAX_BYTES", "1048577"),
            ("WEBAPI_MIGRATION_RULE_MAX_BYTES", "0"),
            ("WEBAPI_MIGRATION_RULE_MAX_BYTES", "16385"),
            ("WEBAPI_MIGRATION_RULE_MAX_COUNT", "0"),
            ("WEBAPI_MIGRATION_RULE_MAX_COUNT", "1000001"),
            ("WEBAPI_MIGRATION_TOTAL_MAX_BYTES", "0"),
            ("WEBAPI_MIGRATION_TOTAL_MAX_BYTES", "1073741825")
        ];
        foreach (var (key, value) in invalid)
        {
            var candidate = new Dictionary<string, string>(ValidConfiguration, StringComparer.Ordinal);
            if (value is null) candidate.Remove(key);
            else candidate[key] = value;
            Assert.Throws<InvalidOperationException>(() => MigrationBudget.FromEnvironment(name => candidate.GetValueOrDefault(name)));
        }

        Assert.Throws<InvalidOperationException>(() => new MigrationBudget(TimeSpan.FromSeconds(59), 1, 1, 1, 1).Validate());
        Assert.Throws<InvalidOperationException>(() => new MigrationBudget(TimeSpan.FromSeconds(60.5), 1, 1, 1, 1).Validate());
        Assert.Throws<InvalidOperationException>(() => new MigrationBudget(TimeSpan.FromSeconds(60), 1, 16385, 1, 1).Validate());
        Assert.Throws<InvalidOperationException>(() => new MigrationBudget(TimeSpan.FromSeconds(60), 1, 1, 1, 1073741825).Validate());
    }

    [Fact]
    public async Task ShorterExistingTimeoutIsNotRelaxed()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET transaction_timeout='5s'; SET statement_timeout='4s'; SET lock_timeout='3s'");
        using var scope = MigrationBudgetScope.Start(TestBudget, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ConfigureTransactionAsync(db, scope.Token));
        await using (var transaction = await db.Database.BeginTransactionAsync(scope.Token))
        {
            await scope.ConfigureTransactionAsync(db, scope.Token);
            Assert.Equal(5000, await db.Database.SqlQueryRaw<int>("SELECT setting::int AS \"Value\" FROM pg_catalog.pg_settings WHERE name='transaction_timeout'").SingleAsync());
            Assert.Equal(4000, await db.Database.SqlQueryRaw<int>("SELECT setting::int AS \"Value\" FROM pg_catalog.pg_settings WHERE name='statement_timeout'").SingleAsync());
            Assert.Equal(3000, await db.Database.SqlQueryRaw<int>("SELECT setting::int AS \"Value\" FROM pg_catalog.pg_settings WHERE name='lock_timeout'").SingleAsync());
            await transaction.RollbackAsync();
        }
        Assert.Equal(5000, await db.Database.SqlQueryRaw<int>("SELECT setting::int AS \"Value\" FROM pg_catalog.pg_settings WHERE name='transaction_timeout'").SingleAsync());
    }

    [Fact]
    public async Task DefaultTimeoutsUseBudgetAndBoundLockWait()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET transaction_timeout='0'; SET statement_timeout='0'; SET lock_timeout='0'");
        using var scope = MigrationBudgetScope.Start(TestBudget, CancellationToken.None);
        await using var transaction = await db.Database.BeginTransactionAsync(scope.Token);
        await scope.ConfigureTransactionAsync(db, scope.Token);
        Assert.Equal(900000, await db.Database.SqlQueryRaw<int>("SELECT setting::int AS \"Value\" FROM pg_catalog.pg_settings WHERE name='transaction_timeout'").SingleAsync());
        Assert.Equal(900000, await db.Database.SqlQueryRaw<int>("SELECT setting::int AS \"Value\" FROM pg_catalog.pg_settings WHERE name='statement_timeout'").SingleAsync());
        Assert.Equal(5000, await db.Database.SqlQueryRaw<int>("SELECT setting::int AS \"Value\" FROM pg_catalog.pg_settings WHERE name='lock_timeout'").SingleAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task RunnerRejectsInvalidBudgetBeforeOpeningConnection()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        var invalid = TestBudget with { TransactionTimeout = TimeSpan.FromSeconds(1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunnerAssertions.RunAsync(db, invalid));
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task RunnerRespectsExistingShorterTimeoutOnHistoryFence()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var delay = new DelayHistoryFence();
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(delay);
        GovernanceMigrationRunner.Configure(options);
        await using (var db = new WebApiDbContext(options.Options))
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET transaction_timeout='1200ms'");
            var failure = await Record.ExceptionAsync(() => MigrationRunnerAssertions.RunAsync(db, TestBudget));
            Assert.True(delay.Reached);
            Assert.NotNull(failure);
            Assert.Contains("timeout", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        NpgsqlConnection.ClearAllPools();
        await using var check = database.Context();
        Assert.True(await check.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NULL AS \"Value\"").SingleAsync());
    }

    private sealed class DelayHistoryFence : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("$WEBAPI_HISTORY_FENCE$", StringComparison.Ordinal))
            {
                Reached = true;
                command.CommandText = "SELECT pg_catalog.pg_sleep(3);\n" + command.CommandText;
            }
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("read")]
    [InlineData("commit")]
    public async Task TransactionBudgetCoversLockReadAndCommit(string stage)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
        {
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TABLE public.migration_budget_marker(id integer PRIMARY KEY, value integer NOT NULL);
                INSERT INTO public.migration_budget_marker VALUES(1,7);
                CREATE FUNCTION public.migration_budget_delay_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_catalog.pg_sleep(3); RETURN NEW; END $$;
                CREATE CONSTRAINT TRIGGER migration_budget_commit_delay AFTER UPDATE ON public.migration_budget_marker
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.migration_budget_delay_commit();
                """);
        }

        NpgsqlConnection? blocker = null;
        NpgsqlTransaction? blockerTransaction = null;
        try
        {
            if (stage == "lock")
            {
                blocker = await database.OpenAsync();
                blockerTransaction = await blocker.BeginTransactionAsync();
                await using var hold = new NpgsqlCommand("UPDATE public.migration_budget_marker SET value=value WHERE id=1", blocker, blockerTransaction);
                await hold.ExecuteNonQueryAsync();
            }
            await using (var db = database.Context())
            {
                await db.Database.OpenConnectionAsync();
                await db.Database.ExecuteSqlRawAsync("SET transaction_timeout='1500ms'; SET statement_timeout='10s'; SET lock_timeout='10s'");
                using var scope = MigrationBudgetScope.Start(TestBudget, CancellationToken.None);
                await using var transaction = await db.Database.BeginTransactionAsync(scope.Token);
                await scope.ConfigureTransactionAsync(db, scope.Token);
                if (stage == "commit")
                    await db.Database.ExecuteSqlRawAsync("UPDATE public.migration_budget_marker SET value=8 WHERE id=1", scope.Token);
                var failure = stage switch
                {
                    "lock" => await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync("LOCK TABLE ONLY public.migration_budget_marker IN SHARE MODE", scope.Token)),
                    "read" => await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_sleep(3)", scope.Token)),
                    "commit" => await Record.ExceptionAsync(() => transaction.CommitAsync(scope.Token)),
                    _ => throw new ArgumentOutOfRangeException(nameof(stage))
                };
                Assert.NotNull(failure);
                Assert.Contains("timeout", failure.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (blockerTransaction is not null) await blockerTransaction.RollbackAsync();
            if (blockerTransaction is not null) await blockerTransaction.DisposeAsync();
            if (blocker is not null) await blocker.DisposeAsync();
        }
        NpgsqlConnection.ClearAllPools();
        await using var check = database.Context();
        Assert.Equal(7, await check.Database.SqlQueryRaw<int>("SELECT value AS \"Value\" FROM public.migration_budget_marker WHERE id=1").SingleAsync());
        Assert.Equal(0, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_catalog.pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid()").SingleAsync());
    }
}

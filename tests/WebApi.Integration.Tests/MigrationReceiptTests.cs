using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Infrastructure.Persistence;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[Collection("MigrationIsolation")]
public sealed class MigrationReceiptTests
{
    private static readonly MigrationBudget TestBudget = new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864);

    private static MigrationReceipt Receipt(Guid id, int maxRows = 100000) => new(
        id, MigrationReceiptEntry.Managed, null, "20261104090000_Governance24MigrationReceipts", null,
        null, new string('a', 64), MigrationReceiptValidation.Unknown, null, null, null,
        TestBudget, new string('b', 64), null, DateTimeOffset.UtcNow, maxRows);

    [Fact]
    public async Task ReceiptMigrationFingerprintsMatchAcrossIndependentInstallers()
    {
        var actual = new List<string>();
        foreach (var sqlEntry in new[] { false, false, true, true })
        {
            await using var database = new PostgresDatabase();
            await database.InitializeAsync();
            if (sqlEntry)
            {
                var root = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
                var script = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "initial-core.sql"));
                await using var connection = await database.OpenAsync();
                await using (var configure = new NpgsqlCommand("SELECT pg_catalog.set_config('webapi.migration_transaction_seconds','900',false),pg_catalog.set_config('webapi.migration_receipt_max_rows','100000',false)", connection))
                    await configure.ExecuteNonQueryAsync();
                await using var command = new NpgsqlCommand(script, connection);
                await command.ExecuteNonQueryAsync();
            }
            await using var db = database.Context();
            if (!sqlEntry) await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            actual.Add(await db.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        }
        Assert.All(actual, fingerprint => Assert.Equal(actual[0], fingerprint));
        Assert.True(GovernanceSchemaFingerprints.Expected.TryGetValue("20261104090000_Governance24MigrationReceipts", out var expected),
            "Missing new independently reproduced fingerprint: " + string.Join(",", actual));
        Assert.Equal(expected, actual[0]);
    }

    [Fact]
    public async Task ReceiptCommitsWithMigrationAndRollsBackWithIt()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var rolledBack = Guid.NewGuid();
        var committed = Guid.NewGuid();
        var rolledBackOrg = Guid.NewGuid();
        var committedOrg = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await MigrationReceiptStore.InsertAsync(db, Receipt(rolledBack), CancellationToken.None);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({rolledBackOrg},'ROLLBACK','Keep','Active',now(),now())");
            await transaction.RollbackAsync();
        }
        Assert.False(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={rolledBack}) AS \"Value\"").SingleAsync());
        Assert.False(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.organizations WHERE id={rolledBackOrg}) AS \"Value\"").SingleAsync());
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await MigrationReceiptStore.InsertAsync(db, Receipt(committed), CancellationToken.None);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({committedOrg},'COMMIT','Keep','Active',now(),now())");
            await transaction.CommitAsync();
        }
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={committed}) AS \"Value\"").SingleAsync());
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.organizations WHERE id={committedOrg}) AS \"Value\"").SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationReceiptStore.InsertAsync(db, Receipt(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task ReceiptRejectsOrdinaryMutationAndTruncate()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await MigrationReceiptStore.InsertAsync(db, Receipt(id), CancellationToken.None);
            await transaction.CommitAsync();
        }
        foreach (var sql in new[] { "UPDATE public.migration_validation_receipts SET entry_point=entry_point", "DELETE FROM public.migration_validation_receipts", "TRUNCATE public.migration_validation_receipts" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal("23514", error.SqlState);
        }
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={id}) AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task MissingIdentityIsUnknown()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await MigrationReceiptStore.InsertAsync(db, Receipt(id), CancellationToken.None);
            await transaction.CommitAsync();
        }
        Assert.True(await db.Database.SqlQuery<bool>($"""
            SELECT identity_state='Unknown' AND cluster_identifier IS NULL AND database_name IS NULL AND database_oid IS NULL
              AND validation_state='Unknown' AND settings_present IS NULL AND rule_count IS NULL AND total_bytes IS NULL AS "Value"
            FROM public.migration_validation_receipts WHERE attempt_id={id}
            """).SingleAsync());
    }

    [Theory]
    [InlineData("Observed", "Unknown")]
    [InlineData("Unknown", "Passed")]
    [InlineData("Unknown", "NotApplicable")]
    public async Task PartialIdentityOrValidationCannotBeClaimedByDirectSql(string identity, string validation)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.migration_validation_receipts(attempt_id,entry_point,target_migration,identity_state,validation_state,
              transaction_timeout_ms,settings_max_bytes,rule_max_bytes,rule_max_count,total_max_bytes,receipt_max_rows,recorded_at)
            VALUES({id},'Managed','20261104090000_Governance24MigrationReceipts',{identity},{validation},900000,65536,16384,100000,67108864,100000,now())
            """));
        Assert.Equal("23514", error.SqlState);
        Assert.False(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={id}) AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task InvalidReceiptFieldsDoNotWriteOrEcho()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var secretLike = "server=example;password=synthetic-canary";
        await using var transaction = await db.Database.BeginTransactionAsync();
        var invalidName = Receipt(Guid.NewGuid()) with { TargetMigration = secretLike };
        var nameError = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationReceiptStore.InsertAsync(db, invalidName, CancellationToken.None));
        Assert.DoesNotContain(secretLike, nameError.ToString());
        var invalidResult = Receipt(Guid.NewGuid()) with { ValidationState = MigrationReceiptValidation.Passed };
        await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationReceiptStore.InsertAsync(db, invalidResult, CancellationToken.None));
        var invalidIdentity = Receipt(Guid.NewGuid()) with { DatabaseIdentity = new MigrationDatabaseIdentity("1", new string('中', 22), 1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationReceiptStore.InsertAsync(db, invalidIdentity, CancellationToken.None));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts").SingleAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task DownMigrationCannotDeleteRetainedReceipts()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var id = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await MigrationReceiptStore.InsertAsync(db, Receipt(id), CancellationToken.None);
            await transaction.CommitAsync();
        }
        var failure = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261104080000_Governance24DatabaseClock"));
        Assert.Equal("23514", failure.SqlState);
        Assert.Contains("migration_receipts_down_blocked", failure.MessageText);
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.migration_validation_receipts WHERE attempt_id={id}) AS \"Value\"").SingleAsync());
        Assert.Contains("20261104090000_Governance24MigrationReceipts", await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task ConcurrentAndDuplicateAttemptsRespectReceiptQuota()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context()) await setup.Database.MigrateAsync();
        var appName = "receipt-wait-" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(database.ConnectionString) { ApplicationName = appName, Pooling = false }.ConnectionString).Options;
        var firstId = Guid.NewGuid();
        await using var first = database.Context();
        await using var second = new WebApiDbContext(options);
        await using var firstTx = await first.Database.BeginTransactionAsync();
        await using var secondTx = await second.Database.BeginTransactionAsync();
        await MigrationReceiptStore.InsertAsync(first, Receipt(firstId, 1), CancellationToken.None);
        var waiting = MigrationReceiptStore.InsertAsync(second, Receipt(Guid.NewGuid(), 1), CancellationToken.None);
        await using (var monitor = database.Context())
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!await monitor.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name={appName} AND wait_event_type='Lock') AS \"Value\"").SingleAsync(deadline.Token))
                await Task.Delay(25, deadline.Token);
        }
        await firstTx.CommitAsync();
        var capacity = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        Assert.Contains("receipt_capacity", capacity.Message);
        await secondTx.RollbackAsync();
        await using (var duplicate = database.Context())
        await using (var duplicateTx = await duplicate.Database.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationReceiptStore.InsertAsync(duplicate, Receipt(firstId, 2), CancellationToken.None));
            Assert.Contains("receipt_duplicate", error.Message);
            await duplicateTx.RollbackAsync();
        }
        await using var check = database.Context();
        Assert.Equal(1, await check.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts").SingleAsync());
    }
}

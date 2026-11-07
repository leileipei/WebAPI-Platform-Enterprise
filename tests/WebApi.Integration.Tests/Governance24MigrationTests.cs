using System.Diagnostics;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Integration.Tests.Support;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Integration.Tests;

[Collection("MigrationIsolation")]
public sealed class Governance24MigrationTests
{
    private const string DraftMigration = "20261009031127_CredentialSecurityLedger";
    private static MigrationBudget TestBudget => new(TimeSpan.FromMinutes(15), 65536, 16384, 100000, 67108864);

    private static string SqlPreflightStatement(string script)
    {
        const string startMarker = "DO $WEBAPI_PREFLIGHT$";
        const string endMarker = "$WEBAPI_PREFLIGHT$;";
        var start = script.IndexOf(startMarker, StringComparison.Ordinal);
        var end = script.IndexOf(endMarker, start < 0 ? 0 : start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return script[start..(end + endMarker.Length)];
    }

    private static WebApiDbContext ManagedMigrationContext(PostgresDatabase database, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString);
        GovernanceMigrationRunner.Configure(options);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new WebApiDbContext(options.Options);
    }

    [Fact]
    public async Task HistoricalMigrationCanBeRolledBackWithinCallerTransaction()
    {
        const string first = "20261003182012_InitialCore";
        const string second = "20261003201632_PublishCoordinatorIdentity";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync(first);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.GetService<IMigrator>().MigrateAsync(second);
            Assert.Contains(second, await db.Database.GetAppliedMigrationsAsync());
            Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute WHERE attrelid='public.release_records'::regclass AND attname='publish_requested_by' AND NOT attisdropped) AS \"Value\"").SingleAsync());
            await transaction.RollbackAsync();
        }
        await using var check = database.Context();
        Assert.Equal(new[] { first }, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute WHERE attrelid='public.release_records'::regclass AND attname='publish_requested_by' AND NOT attisdropped) AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task FullLegacyMigrationCanBeRolledBackWithinCallerTransaction()
    {
        const string prior = "20261005120000_SsoOidc";
        const string valid = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test/path\",\"webhookSecretRef\":\"vault://local/mail\"}";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        string[] history;
        string fingerprint;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync(prior);
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{valid}::jsonb,{user},now(),7)");
            history = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
            fingerprint = await setup.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync();
        }
        NpgsqlConnection.ClearAllPools();
        await using (var db = database.Context())
        {
            await db.Database.OpenConnectionAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await GovernanceMigrationPreflight.ValidateAsync(db);
            await WebApi.Infrastructure.Notifications.NotificationMigrationPreflight.ValidateAsync(db, TestBudget);
            await GovernanceMigrationSourceCapture.CaptureAsync(db);
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.True(await db.Database.SqlQuery<bool>($"SELECT value @> {valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
            await transaction.RollbackAsync();
        }
        NpgsqlConnection.ClearAllPools();
        await using var check = database.Context();
        Assert.Equal(history, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(fingerprint, await check.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT value={valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.users WHERE id={user}) AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task CallerTransactionFencesLegacySettingsThroughMigration()
    {
        const string prior = "20261005120000_SsoOidc";
        const string valid = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test/path\",\"webhookSecretRef\":\"vault://local/mail\"}";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        string[] history;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync(prior);
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{valid}::jsonb,{user},now(),7)");
            history = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
        }
        NpgsqlConnection.ClearAllPools();
        await using (var db = database.Context())
        {
            await db.Database.OpenConnectionAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await GovernanceMigrationWriteFence.AcquireAsync(db);
            await GovernanceMigrationPreflight.ValidateAsync(db);
            await WebApi.Infrastructure.Notifications.NotificationMigrationPreflight.ValidateAsync(db, TestBudget);
            await GovernanceMigrationSourceCapture.CaptureAsync(db);

            async Task AssertOtherConnectionCannotUpdateAsync()
            {
                var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
                await using var actor = new NpgsqlConnection(settings.ConnectionString);
                await actor.OpenAsync();
                await using (var timeout = new NpgsqlCommand("SET lock_timeout = '250ms'", actor)) await timeout.ExecuteNonQueryAsync();
                await using var update = new NpgsqlCommand("UPDATE public.system_settings SET revision=revision+1 WHERE key='system.notification'", actor);
                var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
                Assert.Equal("55P03", error.SqlState);
            }

            await AssertOtherConnectionCannotUpdateAsync();
            await db.Database.MigrateAsync();
            await AssertOtherConnectionCannotUpdateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.True(await db.Database.SqlQuery<bool>($"SELECT value @> {valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
            await transaction.RollbackAsync();
        }
        NpgsqlConnection.ClearAllPools();
        await using var check = database.Context();
        Assert.Equal(history, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT value={valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
    }

    [Fact]
    public async Task MigrationWriteFenceRequiresActiveTransaction()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.OpenConnectionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => GovernanceMigrationWriteFence.AcquireAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ManagedRunnerRestoresCallerConnectionLifetime(bool initiallyOpen, bool rejectSchema)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        if (rejectSchema)
        {
            await using var setup = database.Context();
            await setup.Database.ExecuteSqlRawAsync("CREATE TABLE public.unexpected_schema_fixture (id integer PRIMARY KEY)");
        }
        NpgsqlConnection.ClearAllPools();
        await using var db = ManagedMigrationContext(database);
        if (initiallyOpen) await db.Database.OpenConnectionAsync();
        if (rejectSchema)
            await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunnerAssertions.RunAsync(db, TestBudget));
        else
            await MigrationRunnerAssertions.RunAsync(db, TestBudget);
        Assert.Equal(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed, db.Database.GetDbConnection().State);
        if (initiallyOpen)
        {
            await db.Database.CloseConnectionAsync();
            Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedRunnerRestoresConnectionWhenNoMigrationsArePending(bool initiallyOpen)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context()) await setup.Database.MigrateAsync();
        NpgsqlConnection.ClearAllPools();
        await using var db = ManagedMigrationContext(database);
        if (initiallyOpen) await db.Database.OpenConnectionAsync();
        await MigrationRunnerAssertions.RunAsync(db, TestBudget);
        Assert.Equal(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed, db.Database.GetDbConnection().State);
        if (initiallyOpen)
        {
            await db.Database.CloseConnectionAsync();
            Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
    }

    [Fact]
    public async Task ManagedRunnerRejectsUnregisteredExternalTransactionWithoutLeakingConnection()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = ManagedMigrationContext(database);
        await db.Database.OpenConnectionAsync();
        await using var external = await db.Database.GetDbConnection().BeginTransactionAsync();
        Assert.Null(db.Database.CurrentTransaction);
        await using (var marker = db.Database.GetDbConnection().CreateCommand())
        {
            marker.CommandText = "CREATE TEMP TABLE caller_marker (id integer); INSERT INTO caller_marker VALUES (7)";
            await marker.ExecuteNonQueryAsync();
        }
        var rejection = await Record.ExceptionAsync(() => MigrationRunnerAssertions.RunAsync(db, TestBudget));
        Assert.True(rejection is InvalidOperationException or NotSupportedException);
        Assert.Equal(7, await db.Database.SqlQueryRaw<int>("SELECT id AS \"Value\" FROM pg_temp.caller_marker").SingleAsync());
        await external.RollbackAsync();
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('pg_temp.caller_marker') IS NULL AS \"Value\"").SingleAsync());
        await db.Database.CloseConnectionAsync();
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledTransactionStartPreservesCallerConnectionLifetime(bool runner)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = ManagedMigrationContext(database);
        await db.Database.OpenConnectionAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (runner)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MigrationRunnerAssertions.RunAsync(db, TestBudget, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GovernanceMigrationSourceCapture.CaptureAsync(db, cancellation.Token));
        await db.Database.CloseConnectionAsync();
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionRegistrationFailureLeavesNoHiddenTransaction(bool runner)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = ManagedMigrationContext(database, new FailAfterTransactionRegistrationStarts());
        await db.Database.OpenConnectionAsync();
        var failure = runner
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunnerAssertions.RunAsync(db, TestBudget))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => GovernanceMigrationSourceCapture.CaptureAsync(db));
        Assert.Contains("fixture_transaction_registration_failed", failure.Message);
        Assert.Null(db.Database.CurrentTransaction);
        await using (var fresh = await db.Database.GetDbConnection().BeginTransactionAsync()) await fresh.RollbackAsync();
        await db.Database.CloseConnectionAsync();
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task TransactionRegistrationFailureClosesRunnerOwnedConnection()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = ManagedMigrationContext(database, new FailAfterTransactionRegistrationStarts());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunnerAssertions.RunAsync(db, TestBudget));
        Assert.Contains("fixture_transaction_registration_failed", failure.Message);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedRunnerRejectsAmbientTransactionBeforeOpeningConnection(bool initiallyOpen)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var observer = new CountConnectionOpens();
        await using var db = ManagedMigrationContext(database, observer);
        if (initiallyOpen) await db.Database.OpenConnectionAsync();
        var opensBefore = observer.Count;
        using (var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeOption.RequiresNew, System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunnerAssertions.RunAsync(db, TestBudget));
            Assert.Equal(opensBefore, observer.Count);
            Assert.Equal(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed, db.Database.GetDbConnection().State);
            scope.Complete();
        }
        if (initiallyOpen)
        {
            await db.Database.CloseConnectionAsync();
            Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
    }

    [Fact]
    public void ManagedNotificationGeneratorRequiresValidationAndExactHistoricalOperation()
    {
        var database = new PostgresDatabase();
        using var db = ManagedMigrationContext(database);
        var generator = Assert.IsType<ManagedNotificationMigrationsSqlGenerator>(db.GetService<IMigrationsSqlGenerator>());
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations["20261008030000_ExternalNotifications"], db.Database.ProviderName!);
        var sql = Assert.Single(migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>(), item => item.Sql.Contains("DO $notification_upgrade$", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => generator.Generate(new[] { sql }));
        generator.EnableManagedValidation();
        try
        {
            var altered = new Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation { Sql = sql.Sql + "\n-- changed" };
            Assert.Throws<InvalidOperationException>(() => generator.Generate(new[] { altered }));
            var commands = generator.Generate(new[] { sql });
            var actual = string.Join("\n", commands.Select(command => command.CommandText)).Trim();
            var marker = "END $notification_upgrade$;";
            var suffix = sql.Sql[(sql.Sql.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
            Assert.Equal(suffix, actual);
        }
        finally { generator.DisableManagedValidation(); }
        Assert.Throws<InvalidOperationException>(() => generator.Generate(new[] { sql }));
    }

    [Fact]
    public void ManagedNotificationGeneratorRejectsMissingOrRenamedHistoricalGuard()
    {
        var database = new PostgresDatabase();
        using var db = ManagedMigrationContext(database);
        var generator = Assert.IsType<ManagedNotificationMigrationsSqlGenerator>(db.GetService<IMigrationsSqlGenerator>());
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations["20261008030000_ExternalNotifications"], db.Database.ProviderName!);
        var operations = migration.UpOperations.ToArray();
        var sql = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>(operations[0]);
        ManagedNotificationMigrationsSqlGenerator.VerifyHistoricalMigration(assembly, db.Database.ProviderName!);
        ManagedNotificationMigrationsSqlGenerator.ValidateHistoricalOperations(operations);
        var renamed = operations.ToArray();
        renamed[0] = new Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation
        {
            Sql = sql.Sql.Replace("DO $notification_upgrade$", "DO $renamed_notification$", StringComparison.Ordinal)
        };
        Assert.Throws<InvalidOperationException>(() => generator.Generate(renamed));
        Assert.Throws<InvalidOperationException>(() => ManagedNotificationMigrationsSqlGenerator.ValidateHistoricalOperations(renamed));
        Assert.Throws<InvalidOperationException>(() => ManagedNotificationMigrationsSqlGenerator.ValidateHistoricalOperations(operations.Skip(1).ToArray()));
        generator.EnableManagedValidation();
        try
        {
            Assert.Throws<InvalidOperationException>(() => generator.Generate(renamed));
            Assert.Throws<InvalidOperationException>(() => generator.Generate(operations.Skip(1).ToArray()));
        }
        finally { generator.DisableManagedValidation(); }
    }

    [Fact]
    public async Task MigrationWriteFenceKeepsBoundedLockTimeoutUntilTransactionEnds()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.OpenConnectionAsync();
        var original = await db.Database.SqlQueryRaw<string>("SELECT pg_catalog.current_setting('lock_timeout') AS \"Value\"").SingleAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='0'");
            await GovernanceMigrationWriteFence.AcquireAsync(db);
            Assert.Equal("5s", await db.Database.SqlQueryRaw<string>("SELECT pg_catalog.current_setting('lock_timeout') AS \"Value\"").SingleAsync());
            await transaction.RollbackAsync();
        }
        Assert.Equal(original, await db.Database.SqlQueryRaw<string>("SELECT pg_catalog.current_setting('lock_timeout') AS \"Value\"").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoPendingMigrationDoesNotWaitForBusinessWriter(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var org = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({org},'KEEP','Keep','Active',now(),now())");
        }
        NpgsqlConnection.ClearAllPools();
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
        await using (var actor = new NpgsqlConnection(settings.ConnectionString))
        {
            await actor.OpenAsync();
            await using var writeTransaction = await actor.BeginTransactionAsync();
            await using (var write = new NpgsqlCommand("UPDATE public.organizations SET name='Pending' WHERE code='KEEP'", actor, writeTransaction)) await write.ExecuteNonQueryAsync();
            if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
            else
            {
                await using var db = ManagedMigrationContext(database);
                await MigrationRunnerAssertions.RunAsync(db, TestBudget);
            }
            await writeTransaction.RollbackAsync();
        }
        await using var check = database.Context();
        Assert.Equal("Keep", await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM public.organizations WHERE id={org}").SingleAsync());
    }

    [Fact]
    public async Task ManagedRunnerUpgradesLegacyNotificationUsingExistingValidator()
    {
        const string valid = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test/path\",\"webhookSecretRef\":\"vault://local/mail\"}";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{valid}::jsonb,{user},now(),7)");
        }
        NpgsqlConnection.ClearAllPools();
        await using var db = ManagedMigrationContext(database);
        await MigrationRunnerAssertions.RunAsync(db, TestBudget);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT value @> {valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
    }

    [Fact]
    public async Task ManagedRunnerKeepsLegacyRowsFencedUntilCommit()
    {
        const string valid = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test/path\",\"webhookSecretRef\":\"vault://local/mail\"}";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        var org = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({org},'KEEP','Keep','Active',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{valid}::jsonb,{user},now(),7)");
        }
        NpgsqlConnection.ClearAllPools();
        var pause = new PauseBeforeLegacySettingsRead();
        await using var db = ManagedMigrationContext(database, pause);
        var migration = MigrationRunnerAssertions.RunAsync(db, TestBudget);
        try
        {
            var first = await Task.WhenAny(pause.Reached, migration).WaitAsync(TimeSpan.FromSeconds(20));
            if (first == migration) await migration;
            Assert.True(pause.Reached.IsCompleted, "The migration must reach the legacy settings read.");
            var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
            await using var actor = new NpgsqlConnection(settings.ConnectionString);
            await actor.OpenAsync();
            await using (var timeout = new NpgsqlCommand("SET lock_timeout = '250ms'", actor)) await timeout.ExecuteNonQueryAsync();
            foreach (var sql in new[] { "UPDATE public.system_settings SET revision=revision+1 WHERE key='system.notification'", "UPDATE public.organizations SET name='Changed' WHERE code='KEEP'" })
            {
                await using var write = new NpgsqlCommand(sql, actor);
                var error = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
                Assert.Equal("55P03", error.SqlState);
            }
        }
        finally
        {
            pause.Release();
        }
        await migration;
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await db.Database.SqlQuery<bool>($"SELECT value @> {valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
        Assert.Equal("Keep", await db.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM public.organizations WHERE id={org}").SingleAsync());
    }

    [Fact]
    public async Task ManagedRunnerBoundsLateReaderLockAndRollsBack()
    {
        const string valid = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test/path\",\"webhookSecretRef\":\"vault://local/mail\"}";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        string[] before;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{valid}::jsonb,{user},now(),7)");
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
        }
        NpgsqlConnection.ClearAllPools();
        var pause = new PauseBeforeLegacySettingsRead();
        await using var db = ManagedMigrationContext(database, pause);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var migration = MigrationRunnerAssertions.RunAsync(db, TestBudget, deadline.Token);
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
        await using (var reader = new NpgsqlConnection(settings.ConnectionString))
        {
            try
            {
                var first = await Task.WhenAny(pause.Reached, migration).WaitAsync(TimeSpan.FromSeconds(20));
                if (first == migration) await migration;
                Assert.True(pause.Reached.IsCompleted, "The migration must reach the legacy settings read.");
                await reader.OpenAsync();
                await using var readerTransaction = await reader.BeginTransactionAsync();
                await using (var select = new NpgsqlCommand("SELECT count(*) FROM public.environments", reader, readerTransaction)) await select.ExecuteScalarAsync();
                pause.Release();
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => migration);
                var error = Assert.IsType<PostgresException>(failure.InnerException);
                Assert.Equal("55P03", error.SqlState);
                await readerTransaction.RollbackAsync();
            }
            finally { pause.Release(); }
        }
        NpgsqlConnection.ClearAllPools();
        await using var check = database.Context();
        Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT value={valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
    }

    [Fact]
    public async Task ManagedRunnerCancellationRollsBackAndReleasesWriteFence()
    {
        const string valid = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test/path\",\"webhookSecretRef\":\"vault://local/mail\"}";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        string[] before;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{valid}::jsonb,{user},now(),7)");
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
        }
        NpgsqlConnection.ClearAllPools();
        var pause = new PauseBeforeLegacySettingsRead();
        await using (var db = ManagedMigrationContext(database, pause))
        using (var cancellation = new CancellationTokenSource())
        {
            var migration = MigrationRunnerAssertions.RunAsync(db, TestBudget, cancellation.Token);
            try
            {
                var first = await Task.WhenAny(pause.Reached, migration).WaitAsync(TimeSpan.FromSeconds(20));
                if (first == migration) await migration;
                Assert.True(pause.Reached.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration);
            }
            finally { pause.Release(); }
        }
        NpgsqlConnection.ClearAllPools();
        await using var check = database.Context();
        Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT value={valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
        await check.Database.ExecuteSqlRawAsync("SET lock_timeout='250ms'");
        Assert.Equal(1, await check.Database.ExecuteSqlRawAsync("UPDATE public.system_settings SET revision=revision WHERE key='system.notification'"));
    }

    [Fact]
    public async Task DisabledOldWriterRemainsIsolatedAcrossCancelledMigrationAndRetry()
    {
        const string valid = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test/path\",\"webhookSecretRef\":\"vault://local/mail\"}";
        const string fixturePassword = "isolated-test-only-cancelled-writer-8426";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        var role = "gov24_cancel_" + Guid.NewGuid().ToString("N");
        string[] before;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{valid}::jsonb,{user},now(),7)");
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
        }
        var created = false;
        try
        {
            await using (var admin = await database.OpenAsync())
            {
                await using (var create = new NpgsqlCommand($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{fixturePassword}'", admin))
                    await create.ExecuteNonQueryAsync();
                created = true;
                await using var grant = new NpgsqlCommand($"GRANT USAGE ON SCHEMA public TO \"{role}\"; GRANT SELECT, UPDATE ON public.system_settings TO \"{role}\"", admin);
                await grant.ExecuteNonQueryAsync();
            }
            var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role, Password = fixturePassword, Pooling = false };
            async Task AssertOldLoginRejectedAsync()
            {
                await using var attempt = new NpgsqlConnection(settings.ConnectionString);
                var error = await Assert.ThrowsAsync<PostgresException>(() => attempt.OpenAsync());
                Assert.Equal("28000", error.SqlState);
            }

            await using (var oldWriter = new NpgsqlConnection(settings.ConnectionString))
            {
                await oldWriter.OpenAsync();
                await using var transaction = await oldWriter.BeginTransactionAsync();
                await using var update = new NpgsqlCommand("UPDATE public.system_settings SET revision=revision+1 WHERE key='system.notification'", oldWriter, transaction);
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }
            await using (var admin = await database.OpenAsync())
            await using (var disable = new NpgsqlCommand($"ALTER ROLE \"{role}\" NOLOGIN", admin))
                await disable.ExecuteNonQueryAsync();
            await AssertOldLoginRejectedAsync();
            NpgsqlConnection.ClearAllPools();
            var pause = new PauseBeforeLegacySettingsRead();
            await using (var db = ManagedMigrationContext(database, pause))
            using (var cancellation = new CancellationTokenSource())
            {
                var migration = MigrationRunnerAssertions.RunAsync(db, TestBudget, cancellation.Token);
                try
                {
                    var first = await Task.WhenAny(pause.Reached, migration).WaitAsync(TimeSpan.FromSeconds(20));
                    if (first == migration) await migration;
                    Assert.True(pause.Reached.IsCompleted, "The migration must reach the legacy settings read.");
                    await AssertOldLoginRejectedAsync();
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration);
                }
                finally
                {
                    cancellation.Cancel();
                    pause.Release();
                    try { await migration; }
                    catch (OperationCanceledException) { }
                }
            }
            await AssertOldLoginRejectedAsync();
            NpgsqlConnection.ClearAllPools();
            await using (var check = database.Context())
            {
                Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
                Assert.True(await check.Database.SqlQuery<bool>($"SELECT value={valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
                Assert.False(await check.Database.SqlQuery<bool>($"SELECT rolcanlogin AS \"Value\" FROM pg_catalog.pg_roles WHERE rolname={role}").SingleAsync());
            }
            NpgsqlConnection.ClearAllPools();
            await using (var retry = ManagedMigrationContext(database))
            {
                await MigrationRunnerAssertions.RunAsync(retry, TestBudget);
                Assert.Empty(await retry.Database.GetPendingMigrationsAsync());
                Assert.True(await retry.Database.SqlQuery<bool>($"SELECT value @> {valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
            }
            await AssertOldLoginRejectedAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                if (created)
                {
                    await using var admin = await database.OpenAsync();
                    await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"", admin);
                    await cleanup.ExecuteNonQueryAsync();
                }
            });
        }
    }

    [Fact]
    public async Task TransactionWriteFenceIsReleasedAtCommit()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var owner = database.Context();
        await owner.Database.ExecuteSqlRawAsync("CREATE TABLE public.fence_sample (id integer PRIMARY KEY, value integer NOT NULL); INSERT INTO public.fence_sample VALUES (1,7)");
        await owner.Database.OpenConnectionAsync();
        await using var transaction = await owner.Database.BeginTransactionAsync();
        await GovernanceMigrationWriteFence.AcquireAsync(owner);
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
        await using var writer = new NpgsqlConnection(settings.ConnectionString);
        await writer.OpenAsync();
        await using (var timeout = new NpgsqlCommand("SET lock_timeout='250ms'", writer)) await timeout.ExecuteNonQueryAsync();
        await using (var blocked = new NpgsqlCommand("UPDATE public.fence_sample SET value=8 WHERE id=1", writer))
            Assert.Equal("55P03", (await Assert.ThrowsAsync<PostgresException>(() => blocked.ExecuteNonQueryAsync())).SqlState);
        await transaction.CommitAsync();
        await using (var allowed = new NpgsqlCommand("UPDATE public.fence_sample SET value=8 WHERE id=1", writer)) Assert.Equal(1, await allowed.ExecuteNonQueryAsync());
        Assert.Equal(8, await owner.Database.SqlQueryRaw<int>("SELECT value AS \"Value\" FROM public.fence_sample WHERE id=1").SingleAsync());
    }

    private sealed class PauseBeforeLegacySettingsRead : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Reached => reached.Task;
        public void Release() => release.TrySetResult(true);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("system_settings", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("value", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                reached.TrySetResult(true);
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    private sealed class CountConnectionOpens : DbConnectionInterceptor
    {
        public int Count { get; private set; }
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancelAfterIdentityPermissionError(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_control_system", StringComparison.Ordinal)
                && eventData.Exception is PostgresException { SqlState: "42501" })
                cancellation.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class DelayClusterIdentityQuery : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Reached => reached.Task;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_control_system", StringComparison.Ordinal))
            {
                command.CommandText = "SELECT 'fixture'::text AS \"Value\" FROM pg_catalog.pg_sleep(30)";
                reached.TrySetResult(true);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelDuringSavepointRelease(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public bool Released { get; private set; }

        public override ValueTask<InterceptionResult> ReleasingSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }

        public override Task ReleasedSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
        {
            Released = true;
            return Task.CompletedTask;
        }
    }

    private sealed class StallSavepointRecovery : DbTransactionInterceptor
    {
        public override async ValueTask<InterceptionResult> RollingBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return result;
        }
    }

    private sealed class FailAfterTransactionRegistrationStarts : DbTransactionInterceptor
    {
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("fixture_transaction_registration_failed");

        public override ValueTask<DbTransaction> TransactionUsedAsync(DbConnection connection, TransactionEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("fixture_transaction_registration_failed");
    }

    [Fact]
    public async Task ManagedRunnerInitializesEmptyDatabase()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = ManagedMigrationContext(database);
        await MigrationRunnerAssertions.RunAsync(db, TestBudget);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task SqlInstallerFailureAfterInitialMigrationRollsBackWholeScript()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        var initial = script.IndexOf("VALUES ('20261003182012_InitialCore', '10.0.12');", StringComparison.Ordinal);
        Assert.True(initial >= 0);
        var next = script.IndexOf("DO $EF$", initial, StringComparison.Ordinal);
        Assert.True(next > initial);
        var withFailure = script.Insert(next, "DO $migration_probe$ BEGIN RAISE EXCEPTION 'isolated_migration_probe'; END $migration_probe$;\n");
        await using (var connection = await database.OpenAsync())
        await using (var command = new NpgsqlCommand(withFailure, connection) { CommandTimeout = 120 })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("isolated_migration_probe", error.MessageText);
        }
        NpgsqlConnection.ClearAllPools();
        await using var check = database.Context();
        Assert.True(await check.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NULL AS \"Value\"").SingleAsync());
        Assert.Equal(0, await PublicTableCountAsync(database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionGuardProbeDistinguishesExistingCallerTransaction(bool outerTransaction)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var connection = await database.OpenAsync();
        await using var transaction = outerTransaction ? await connection.BeginTransactionAsync() : null;
        await using var command = new NpgsqlCommand("DO $transaction_guard$ BEGIN COMMIT; END $transaction_guard$; SELECT 1;", connection, transaction);
        var failure = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
        if (outerTransaction) Assert.IsType<PostgresException>(failure);
        else Assert.Null(failure);
        if (transaction is not null) await transaction.RollbackAsync();
    }

    [Fact]
    public async Task SqlInstallerRejectsExistingCallerTransactionWithoutCommitting()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        Exception? failure;
        await using (var connection = await database.OpenAsync())
        {
            await using (var setup = new NpgsqlCommand("CREATE SCHEMA caller_fixture; CREATE TABLE caller_fixture.marker(id integer)", connection)) await setup.ExecuteNonQueryAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var insert = new NpgsqlCommand("INSERT INTO caller_fixture.marker VALUES (1)", connection, transaction)) await insert.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(script, connection, transaction) { CommandTimeout = 120 };
            failure = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
            try { await transaction.RollbackAsync(); }
            catch (InvalidOperationException) { }
        }
        NpgsqlConnection.ClearAllPools();
        await using var check = await database.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*)::int FROM caller_fixture.marker", check);
        Assert.Equal(0, (int)(await count.ExecuteScalarAsync())!);
        var error = Assert.IsType<PostgresException>(failure);
        Assert.Equal("2D000", error.SqlState);
    }

    [Fact]
    public async Task SqlInstallerFencesExistingRowsDuringMigration()
    {
        const string valid = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":null,\"webhookSecretRef\":null,\"smtpEnabled\":false,\"webhookEnabled\":false,\"smtpSecurity\":\"StartTlsRequired\"}";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        var org = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({org},'KEEP','Keep','Active',now(),now())");
        }
        NpgsqlConnection.ClearAllPools();
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        const string marker = "$WEBAPI_PREFLIGHT$;";
        var end = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(end >= 0);
        script = script.Insert(end + marker.Length, "\nSELECT pg_catalog.pg_sleep(4);\n");

        var appName = "gov24_sql_probe_" + Guid.NewGuid().ToString("N");
        var runnerOptions = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false, ApplicationName = appName };
        var monitorOptions = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres", Pooling = false };
        await using var monitor = new NpgsqlConnection(monitorOptions.ConnectionString);
        await monitor.OpenAsync();
        await using var runner = new NpgsqlConnection(runnerOptions.ConnectionString);
        await runner.OpenAsync();
        await using var command = new NpgsqlCommand(script, runner) { CommandTimeout = 120 };
        var running = command.ExecuteNonQueryAsync();
        var wait = Stopwatch.StartNew();
        while (true)
        {
            await using var status = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name=@name AND wait_event='PgSleep')", monitor);
            status.Parameters.AddWithValue("name", appName);
            if ((bool)(await status.ExecuteScalarAsync())!) break;
            if (running.IsCompleted) { await running; throw new InvalidOperationException("SQL migration ended before the probe window."); }
            if (wait.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("SQL migration did not reach the probe window.");
            await Task.Delay(25);
        }

        var actorOptions = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
        await using (var actor = new NpgsqlConnection(actorOptions.ConnectionString))
        {
            await actor.OpenAsync();
            await using (var timeout = new NpgsqlCommand("SET lock_timeout = '250ms'", actor)) await timeout.ExecuteNonQueryAsync();
            await using (var insert = new NpgsqlCommand("INSERT INTO public.system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',@value::jsonb,@user,now(),7)", actor))
            {
                insert.Parameters.AddWithValue("value", valid);
                insert.Parameters.AddWithValue("user", user);
                var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
                Assert.Equal("55P03", error.SqlState);
            }
            await using var write = new NpgsqlCommand("UPDATE public.organizations SET name='Changed' WHERE code='KEEP'", actor);
            var writeError = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
            Assert.Equal("55P03", writeError.SqlState);
        }
        await running.WaitAsync(TimeSpan.FromSeconds(30));
        await using var check = database.Context();
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM public.system_settings WHERE key='system.notification') AS \"Value\"").SingleAsync());
        Assert.Equal("Keep", await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM public.organizations WHERE id={org}").SingleAsync());
    }

    [Fact]
    public async Task SqlLateGuardRejectsLegacySettingsAddedAfterPreflight()
    {
        const string valid = "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test/path\",\"webhookSecretRef\":\"vault://local/mail\"}";
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        string[] before;
        string fingerprint;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
            fingerprint = await setup.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync();
        }
        NpgsqlConnection.ClearAllPools();
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        const string marker = "$WEBAPI_PREFLIGHT$;";
        var end = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(end >= 0);
        var injected = $"\nINSERT INTO public.system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system','{valid}'::jsonb,'{user:D}'::uuid,now(),7);\n";
        script = script.Insert(end + marker.Length, injected);
        await using (var connection = await database.OpenAsync())
        await using (var command = new NpgsqlCommand(script, connection) { CommandTimeout = 120 })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("managed_notification_validation_required", error.MessageText);
        }
        NpgsqlConnection.ClearAllPools();
        await using var check = database.Context();
        Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(fingerprint, await check.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM public.system_settings WHERE key='system.notification') AS \"Value\"").SingleAsync());
    }

    [Fact]
    public async Task EmptyDatabaseHistoryReaderPreservesTransaction()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.OpenConnectionAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.Empty(await GovernanceMigrationHistory.ReadAppliedAsync(db));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync());
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedDatabaseEncodingBlocksBeforeMigration(bool sqlEntry)
    {
        var fixture = new PostgresDatabase();
        var name = "gov24_encoding_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-retained-encoding-database=" + name);
        var adminOptions = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminOptions.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C'", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var options = new NpgsqlConnectionStringBuilder(adminOptions.ConnectionString) { Database = name };
            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using (var encoding = new NpgsqlCommand("SELECT pg_catalog.current_setting('server_encoding')", connection))
                Assert.Equal("LATIN1", (string)(await encoding.ExecuteScalarAsync())!);
            if (sqlEntry)
            {
                var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
                var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
                const string marker = "$WEBAPI_PREFLIGHT$;";
                var end = script.IndexOf(marker, StringComparison.Ordinal);
                Assert.True(end >= 0);
                await using var command = new NpgsqlCommand(script[..(end + marker.Length)], connection);
                var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Contains("governance_migration_preflight_failed: unsupported database encoding", error.MessageText);
                // The installer now owns one transaction. Roll it back before
                // checking the unchanged schema on this same connection.
                await using var rollback = new NpgsqlCommand("ROLLBACK", connection);
                await rollback.ExecuteNonQueryAsync();
            }
            else
            {
                await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection).Options);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GovernanceMigrationPreflight.ValidateAsync(db));
                Assert.Contains("governance_migration_preflight_failed: unsupported database encoding", error.Message);
            }
            await using var check = new NpgsqlCommand("SELECT pg_catalog.to_regclass('public.\"__EFMigrationsHistory\"') IS NULL", connection);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownPublicProcedureBlocksBeforeFurtherMigration(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        string[] before;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlRawAsync("CREATE PROCEDURE public.test_extra_procedure() LANGUAGE SQL AS 'SELECT 1'");
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
        }
        await AssertPreflightBlockedAsync(database, sqlEntry);
        await using var check = database.Context();
        Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await check.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regprocedure('public.test_extra_procedure()') IS NOT NULL AS \"Value\"").SingleAsync());
    }

    [Theory]
    [InlineData(false, "policy")]
    [InlineData(true, "policy")]
    [InlineData(false, "rule")]
    [InlineData(true, "rule")]
    public async Task UnknownPublicTableDefinitionBlocksBeforeFurtherMigration(bool sqlEntry, string kind)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        string[] before;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            var sql = kind == "policy"
                ? "CREATE POLICY test_extra_policy ON public.organizations USING (true)"
                : "CREATE RULE test_extra_rule AS ON DELETE TO public.organizations DO ALSO NOTHING";
            await setup.Database.ExecuteSqlRawAsync(sql);
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
        }
        await AssertPreflightBlockedAsync(database, sqlEntry);
        await using var check = database.Context();
        Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        var existsSql = kind == "policy"
            ? "SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_policy WHERE polname='test_extra_policy') AS \"Value\""
            : "SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_rewrite WHERE rulename='test_extra_rule') AS \"Value\"";
        Assert.True(await check.Database.SqlQueryRaw<bool>(existsSql).SingleAsync());
    }

    [Theory]
    [InlineData(false, "child")]
    [InlineData(true, "child")]
    [InlineData(false, "parent")]
    [InlineData(true, "parent")]
    [InlineData(false, "partition")]
    [InlineData(true, "partition")]
    public async Task UnknownTableInheritanceBlocksBeforeFurtherMigration(bool sqlEntry, string kind)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        string[] before;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            var sql = kind switch
            {
                "child" => "CREATE SCHEMA test_extra; CREATE TABLE test_extra.inherited() INHERITS (public.organizations)",
                "parent" => "CREATE SCHEMA test_extra; CREATE TABLE test_extra.parent(id integer); CREATE TABLE public.test_child() INHERITS (test_extra.parent)",
                _ => "CREATE SCHEMA test_extra; CREATE TABLE test_extra.parent(id integer) PARTITION BY RANGE (id); CREATE TABLE public.test_child PARTITION OF test_extra.parent FOR VALUES FROM (0) TO (10)"
            };
            await setup.Database.ExecuteSqlRawAsync(sql);
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
        }
        await AssertPreflightBlockedAsync(database, sqlEntry);
        await using var check = database.Context();
        Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        var child = kind == "child" ? "test_extra.inherited" : "public.test_child";
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits WHERE inhrelid=pg_catalog.to_regclass({child})) AS \"Value\"").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySupportedMigrationPrefixPassesBothPreflights(bool sqlSeed)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        string[] migrations;
        await using (var discovery = database.Context()) migrations = discovery.Database.GetMigrations().ToArray();
        Assert.Equal(36, migrations.Length);
        Assert.Equal("20261104080000_Governance24DatabaseClock", migrations[34]);
        Assert.Equal("20261104090000_Governance24MigrationReceipts", migrations[35]);
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        const string marker = "$WEBAPI_PREFLIGHT$;";
        var end = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(end >= 0);
        var sqlPreflight = script[..(end + marker.Length)];
        var starts = System.Text.RegularExpressions.Regex.Matches(script, @"(?m)^-- WEBAPI_MIGRATION_START [0-9]{2}[ \t]*\r?$");
        var commits = System.Text.RegularExpressions.Regex.Matches(script, @"(?m)^-- WEBAPI_MIGRATION_END [0-9]{2}[ \t]*\r?$");
        Assert.Equal(migrations.Length, starts.Count);
        Assert.Equal(migrations.Length, commits.Count);
        var sqlHeader = script[(end + marker.Length)..starts[0].Index];
        for (var index = 0; index <= migrations.Length; index++)
        {
            if (index > 0)
            {
                if (sqlSeed)
                {
                    var start = starts[index - 1].Index;
                    var stop = commits[index - 1].Index + commits[index - 1].Length;
                    var block = "START TRANSACTION;\n" + (index == 1 ? sqlHeader : "") + script[start..stop] + "\nCOMMIT;";
                    await using var connection = await database.OpenAsync();
                    await using var command = new NpgsqlCommand(block, connection);
                    await command.ExecuteNonQueryAsync();
                }
                else
                {
                    await using var setup = database.Context();
                    await setup.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
                }
            }
            NpgsqlConnection.ClearAllPools();
            await using (var check = database.Context())
            {
                await check.Database.OpenConnectionAsync();
                await GovernanceMigrationPreflight.ValidateAsync(check);
                await WebApi.Infrastructure.Notifications.NotificationMigrationPreflight.ValidateAsync(check, TestBudget);
                Assert.Equal(migrations.Take(index), await check.Database.GetAppliedMigrationsAsync());
                Assert.True(await check.Database.SqlQueryRaw<bool>("""
                    SELECT NOT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                        WHERE n.nspname='public' AND c.relkind IN ('v','m','p','f')
                    ) AND NOT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace
                        WHERE n.nspname='public' AND t.typtype IN ('e','d','r','m')
                    ) AND NOT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_attribute a
                        JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
                        JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                        JOIN pg_catalog.pg_type t ON t.oid=a.atttypid
                        JOIN pg_catalog.pg_namespace tn ON tn.oid=t.typnamespace
                        WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f')
                          AND a.attnum>0 AND NOT a.attisdropped AND tn.nspname<>'pg_catalog'
                    ) AS "Value"
                    """).SingleAsync());
            }
            NpgsqlConnection.ClearAllPools();
            await using (var connection = await database.OpenAsync())
            await using (var preflight = new NpgsqlCommand(sqlPreflight, connection))
                await preflight.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, "{\"smtpHost\":\"mail.example.test\",\"smtpPort\":587,\"fromEmail\":\"bad@bad@bad\",\"smtpSecretRef\":\"vault://local/mail\",\"webhookUrl\":null,\"webhookSecretRef\":null}")]
    [InlineData(true, "{\"smtpHost\":\"mail.example.test\",\"smtpPort\":587,\"fromEmail\":\"bad@bad@bad\",\"smtpSecretRef\":\"vault://local/mail\",\"webhookUrl\":null,\"webhookSecretRef\":null}")]
    [InlineData(false, "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":null,\"webhookSecretRef\":null,\"smtpEnabled\":false,\"webhookEnabled\":false,\"smtpSecurity\":\"StartTlsRequired\"}")]
    [InlineData(true, "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":null,\"webhookSecretRef\":null,\"smtpEnabled\":false,\"webhookEnabled\":false,\"smtpSecurity\":\"StartTlsRequired\"}")]
    [InlineData(false, "{\"smtpHost\":{\"unexpected\":[]},\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":null,\"webhookSecretRef\":null}")]
    [InlineData(true, "{\"smtpHost\":{\"unexpected\":[]},\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":null,\"webhookSecretRef\":null}")]
    public async Task InvalidLegacyNotificationStopsBeforeAnyEarlierMigration(bool sqlEntry, string invalid)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        string[] before;
        string fingerprint;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{invalid}::jsonb,{user},now(),7)");
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
            fingerprint = await setup.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync();
        }
        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            Assert.NotNull(error);
            Assert.Contains("governance_migration_preflight_failed: managed_notification_validation_required", error.MessageText);
            Assert.Contains("system.notification", error.MessageText);
            Assert.DoesNotContain("vault://", error.MessageText);
        }
        else
        {
            var result = await RunMigratorAsync(database);
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(result.NotificationPreflightBlocked);
        }
        await using var check = database.Context();
        Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(fingerprint, await check.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT value={invalid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task InvalidLegacyUrlPortStopsBeforeAnyEarlierMigration(bool sqlEntry) =>
        InvalidLegacyNotificationStopsBeforeAnyEarlierMigration(sqlEntry, "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test:65536/path\",\"webhookSecretRef\":\"vault://local/mail\"}");

    [Fact]
    public Task SqlInstallerRequiresManagedValidatorForLegacySettings() =>
        InvalidLegacyNotificationStopsBeforeAnyEarlierMigration(true, "{\"smtpHost\":null,\"smtpPort\":null,\"fromEmail\":null,\"smtpSecretRef\":null,\"webhookUrl\":\"https://hook.example.test/path\",\"webhookSecretRef\":\"vault://local/mail\"}");

    [Fact]
    public Task SqlInstallerRequiresManagedValidatorForLegacyRule() =>
        InvalidLegacyAlertRuleStopsBeforeAnyEarlierMigration(true, "{\"inConsole\":true,\"requestedChannels\":[]}");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingNineFieldNotificationUsesManagedValidation(bool sqlEntry, bool invalid)
    {
        var webhook = invalid ? "https://hook.example.test:65536/path" : "https://hook.example.test/path";
        var json = System.Text.Json.JsonSerializer.Serialize(new { smtpHost = (string?)null, smtpPort = (int?)null, fromEmail = (string?)null, smtpSecretRef = (string?)null, webhookUrl = webhook, webhookSecretRef = "vault://local/mail", smtpEnabled = false, webhookEnabled = false, smtpSecurity = "StartTlsRequired" });
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        string[] before;
        string fingerprint;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{json}::jsonb,{user},now(),7)");
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
            fingerprint = await setup.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync();
        }
        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            Assert.NotNull(error);
            Assert.Contains("managed_notification_validation_required", error.MessageText);
        }
        else
        {
            var result = await RunMigratorAsync(database);
            if (invalid) { Assert.NotEqual(0, result.ExitCode); Assert.True(result.NotificationPreflightBlocked); }
            else Assert.Equal(0, result.ExitCode);
        }
        await using var check = database.Context();
        if (sqlEntry || invalid)
        {
            Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(fingerprint, await check.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        }
        else Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT value={json}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingExtendedAlertPolicyUsesManagedValidation(bool sqlEntry, bool invalid)
    {
        var channels = invalid ? new[] { "Email", "Email" } : Array.Empty<string>();
        var policy = System.Text.Json.JsonSerializer.Serialize(new { inConsole = true, requestedChannels = channels, externalEnabled = false, emailRecipients = Array.Empty<string>(), notifyRecovery = true, retryPolicy = new { maxAttempts = 5, baseDelaySeconds = 30, maxDelaySeconds = 900, expiresAfterMinutes = 1440 } });
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid(); var org = Guid.NewGuid(); var project = Guid.NewGuid(); var env = Guid.NewGuid(); var rule = Guid.NewGuid();
        string[] before;
        string fingerprint;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({org},'KEEP','Keep','Active',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES({project},{org},'KEEP','Keep','Active',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status,deployment_sequence,revision) VALUES({env},{project},'KEEP','Keep',false,0,'Active',0,1)");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO alert_rules(id,organization_id,project_id,environment_id,name,normalized_name,metric,expression,severity,enabled,for_seconds,target_type,window_seconds,notification,revision,logic_revision,created_by,updated_by,created_at,updated_at) VALUES({rule},{org},{project},{env},'Keep','KEEP','request_rps','request_rps > 10','Warning',true,0,'Environment',60,{policy}::jsonb,7,3,{user},{user},now(),now())");
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
            fingerprint = await setup.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync();
        }
        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            Assert.NotNull(error);
            Assert.Contains("managed_notification_validation_required", error.MessageText);
            Assert.Contains(rule.ToString(), error.MessageText);
        }
        else
        {
            var result = await RunMigratorAsync(database);
            if (invalid) { Assert.NotEqual(0, result.ExitCode); Assert.True(result.NotificationPreflightBlocked); }
            else Assert.Equal(0, result.ExitCode);
        }
        await using var check = database.Context();
        if (sqlEntry || invalid)
        {
            Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Equal(fingerprint, await check.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        }
        else Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT notification={policy}::jsonb AND revision=7 AND logic_revision=3 AS \"Value\" FROM public.alert_rules WHERE id={rule}").SingleAsync());
    }

    [Theory]
    [InlineData(false, "Ops@example.test")]
    [InlineData(true, "Ops@example.test")]
    [InlineData(false, "\"a@b\"@example.test")]
    [InlineData(true, "\"a@b\"@example.test")]
    public async Task ValidLegacyNotificationUsesManagedUpgradeOrSqlProtection(bool sqlEntry, string address)
    {
        var valid = System.Text.Json.JsonSerializer.Serialize(new { smtpHost = "mail.example.test", smtpPort = 587, fromEmail = address, smtpSecretRef = "vault://local/mail", webhookUrl = (string?)null, webhookSecretRef = (string?)null });
        Assert.NotNull(WebApi.Infrastructure.Settings.SettingsValueCodec.Decode("notification", valid));
        if (sqlEntry)
        {
            await InvalidLegacyNotificationStopsBeforeAnyEarlierMigration(true, valid);
            return;
        }
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{valid}::jsonb,{user},now(),7)");
        }
        Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
        await using var check = database.Context();
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT value @> {valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidLegacyUnicodeHostUsesManagedUpgradeOrSqlProtection(bool sqlEntry)
    {
        const string valid = "{\"smtpHost\":\"münchen.example\",\"smtpPort\":587,\"fromEmail\":\"ops@example.test\",\"smtpSecretRef\":\"vault://local/mail\",\"webhookUrl\":null,\"webhookSecretRef\":null}";
        Assert.NotNull(WebApi.Infrastructure.Settings.SettingsValueCodec.Decode("notification", valid));
        if (sqlEntry)
        {
            await InvalidLegacyNotificationStopsBeforeAnyEarlierMigration(true, valid);
            return;
        }
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{valid}::jsonb,{user},now(),7)");
        }
        Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
        await using var check = database.Context();
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT value @> {valid}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
    }

    [Theory]
    [InlineData(false, '\u00a0')]
    [InlineData(true, '\u00a0')]
    [InlineData(false, '\u202f')]
    [InlineData(true, '\u202f')]
    public async Task LegacyHostWhitespaceStopsBeforeEitherMigration(bool sqlEntry, char character)
    {
        var invalid = System.Text.Json.JsonSerializer.Serialize(new { smtpHost = "mail" + character + "example.test", smtpPort = 587, fromEmail = "ops@example.test", smtpSecretRef = "vault://local/mail", webhookUrl = (string?)null, webhookSecretRef = (string?)null });
        Assert.Throws<WebApi.Contracts.Common.ApiException>(() => WebApi.Infrastructure.Settings.SettingsValueCodec.Decode("notification", invalid));
        await InvalidLegacyNotificationStopsBeforeAnyEarlierMigration(sqlEntry, invalid);
    }

    [Theory]
    [InlineData("mail.example.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail..example.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail.example.test", "bad address@example.test", "vault://local/mail", null, null)]
    [InlineData("mail.example.test", "ops@", "vault://local/mail", null, null)]
    [InlineData("mail.example.test", "@example.test", "vault://local/mail", null, null)]
    [InlineData("mail.-example.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail.example-.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("münchen.example", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("-mail.example.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData(".mail.example.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail.example.test.", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail_example.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail\u00a0example.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail\u202fexample.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail\u200bexample.test", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail.example.test", "a..b@example.test", "vault://local/mail", null, null)]
    [InlineData("mail.example.test", "ops@localhost", "vault://local/mail", null, null)]
    [InlineData("mail.example.test", "ops@.example.test", "vault://local/mail", null, null)]
    [InlineData("mail.example.test", "ops@example..test", "vault://local/mail", null, null)]
    [InlineData("mail.example.test", "ops@example test", "vault://local/mail", null, null)]
    [InlineData(null, null, null, "http://hook.example.test/path", "vault://local/hook")]
    [InlineData(null, null, null, "https://hook.example.test/path?query=1", "vault://local/hook")]
    [InlineData(null, null, null, "https://hook.example.test/path", "vault://local/")]
    [InlineData("mail.example.test", "\"a@b\"@example.test", "vault://local/mail", null, null)]
    [InlineData("localhost", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("[::1]", "ops@example.test", "vault://local/mail", null, null)]
    [InlineData("mail.example.test", "ops@example.test", "vault://local/path with space", null, null)]
    [InlineData("mail.example.test", "ops@example.test", "vault://local/path\u0085end", null, null)]
    [InlineData(null, null, null, "https://hook.example.test/path\u009fend", "vault://local/hook")]
    [InlineData(null, null, null, "https://hook.example.test/path\tend", "vault://local/hook")]
    [InlineData(null, null, null, "https://hook.example.test/path\u200bend", "vault://local/hook")]
    [InlineData(null, null, null, "HTTPS://hook.example.test/path with space", "VAULT://local/path with space")]
    [InlineData(null, null, null, null, null)]
    public async Task LegacyNotificationUsesManagedValidatorAndSqlBlocksSample(string? host, string? address, string? smtpRef, string? webhook, string? webhookRef)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid();
        var json = System.Text.Json.JsonSerializer.Serialize(new { smtpHost = host, smtpPort = host is null ? (int?)null : 587, fromEmail = address, smtpSecretRef = smtpRef, webhookUrl = webhook, webhookSecretRef = webhookRef });
        var decoderFailure = Record.Exception(() => WebApi.Infrastructure.Settings.SettingsValueCodec.Decode("notification", json));
        if (decoderFailure is not null) Assert.IsType<WebApi.Contracts.Common.ApiException>(decoderFailure);
        bool managedAllowed;
        string[] before;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO system_settings(key,scope_type,value,updated_by,updated_at,revision) VALUES('system.notification','system',{json}::jsonb,{user},now(),7)");
            var failure = await Record.ExceptionAsync(() => WebApi.Infrastructure.Notifications.NotificationMigrationPreflight.ValidateAsync(setup, TestBudget));
            if (failure is not null) Assert.IsType<InvalidOperationException>(failure);
            managedAllowed = failure is null;
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
        }
        Assert.Equal(decoderFailure is null, managedAllowed);
        NpgsqlConnection.ClearAllPools();
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        const string marker = "$WEBAPI_PREFLIGHT$;";
        var end = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(end >= 0);
        await using (var connection = await database.OpenAsync())
        await using (var preflight = new NpgsqlCommand(script[..(end + marker.Length)], connection))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => preflight.ExecuteNonQueryAsync());
            Assert.Contains("governance_migration_preflight_failed: managed_notification_validation_required", error.MessageText);
        }
        NpgsqlConnection.ClearAllPools();
        await using (var check = database.Context())
        {
            Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.True(await check.Database.SqlQuery<bool>($"SELECT value={json}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
        }
        if (managedAllowed)
        {
            NpgsqlConnection.ClearAllPools();
            var result = await RunMigratorAsync(database);
            Assert.Equal(0, result.ExitCode);
            await using var check = database.Context();
            Assert.Empty(await check.Database.GetPendingMigrationsAsync());
            Assert.True(await check.Database.SqlQuery<bool>($"SELECT value @> {json}::jsonb AND revision=7 AS \"Value\" FROM public.system_settings WHERE key='system.notification'").SingleAsync());
        }
    }

    [Theory]
    [InlineData("https://hook.example.test:443/path", "vault://local/mail", true)]
    [InlineData("https://hook.example.test:65536/path", "vault://local/mail", false)]
    [InlineData("https://hook.example.test:abc/path", "vault://local/mail", false)]
    [InlineData("https://hook.example.test/path", "vault://local:65536/mail", false)]
    [InlineData("https://hook.example.test/path", "vault://local:abc/mail", false)]
    public async Task LegacyNotificationUriPortsUseManagedValidatorAndSqlBlocks(string webhook, string webhookRef, bool expected)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { smtpHost = (string?)null, smtpPort = (int?)null, fromEmail = (string?)null, smtpSecretRef = (string?)null, webhookUrl = webhook, webhookSecretRef = webhookRef });
        var failure = Record.Exception(() => WebApi.Infrastructure.Settings.SettingsValueCodec.Decode("notification", json));
        Assert.Equal(expected, failure is null);
        await LegacyNotificationUsesManagedValidatorAndSqlBlocksSample(null, null, null, webhook, webhookRef);
    }

    [Theory]
    [InlineData(" https://hook.example.test/path ", "vault://local/mail")]
    [InlineData("https://hook.example.test/path", " vault://local/mail ")]
    [InlineData("https://hook.example.test/path%3Fvalue", "vault://local/path%23value")]
    public Task LegacyUriWhitespaceAndEscapesFollowManagedValidator(string webhook, string webhookRef) =>
        LegacyNotificationUsesManagedValidatorAndSqlBlocksSample(null, null, null, webhook, webhookRef);

    [Theory]
    [InlineData(10)]
    [InlineData(1011)]
    [InlineData(1012)]
    [InlineData(1020)]
    public Task LegacyNotificationUnicodeLengthBoundaryAgrees(int count)
    {
        var webhook = "https://hook.example.test/" + string.Concat(Enumerable.Repeat("😀", count));
        return LegacyNotificationUsesManagedValidatorAndSqlBlocksSample(null, null, null, webhook, "vault://local/hook");
    }

    [Theory]
    [InlineData(1010)]
    [InlineData(1011)]
    public Task LegacyNotificationUnicodeMixedBoundaryAgrees(int count)
    {
        var webhook = "https://hook.example.test/" + string.Concat(Enumerable.Repeat("😀", count)) + "x";
        return LegacyNotificationUsesManagedValidatorAndSqlBlocksSample(null, null, null, webhook, "vault://local/hook");
    }

    [Theory]
    [InlineData(120)]
    [InlineData(121)]
    public Task LegacyNotificationUnicodeEmailBoundaryAgrees(int count)
    {
        var address = string.Concat(Enumerable.Repeat("😀", count)) + "@example.test";
        return LegacyNotificationUsesManagedValidatorAndSqlBlocksSample("mail.example.test", address, "vault://local/mail", null, null);
    }

    [Theory]
    [InlineData(1017)]
    [InlineData(1018)]
    public Task LegacyNotificationUnicodeReferenceBoundaryAgrees(int count)
    {
        var reference = "vault://local/" + string.Concat(Enumerable.Repeat("😀", count));
        return LegacyNotificationUsesManagedValidatorAndSqlBlocksSample("mail.example.test", "ops@example.test", reference, null, null);
    }

    [Fact]
    public async Task LegacyControlCharacterClassificationMatchesManagedSamples()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT i, (pg_catalog.chr(i) COLLATE "C") ~ '[[:cntrl:]]',
                   pg_catalog.chr(i) ~ '[[:cntrl:]]'
            FROM pg_catalog.unnest(ARRAY(SELECT pg_catalog.generate_series(1,159)) || ARRAY[160,173,8203,65039]) AS sample(i)
            ORDER BY i
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            var codepoint = reader.GetInt32(0);
            var expected = char.IsControl((char)codepoint);
            Assert.Equal(expected, reader.GetBoolean(1));
            Assert.Equal(expected, reader.GetBoolean(2));
            count++;
        }
        Assert.Equal(163, count);
    }

    [Fact]
    public async Task LegacyHostWhitespaceSamplesMatchManagedValidator()
    {
        var characters = Enumerable.Range(1, char.MaxValue).Select(code => (char)code).Where(char.IsWhiteSpace).ToArray();
        Assert.Equal(25, characters.Length);
        Assert.Contains('\u00a0', characters);
        Assert.Contains('\u202f', characters);
        foreach (var character in characters)
        {
            var host = "mail" + character + "example.test";
            var json = System.Text.Json.JsonSerializer.Serialize(new { smtpHost = host, smtpPort = 587, fromEmail = "ops@example.test", smtpSecretRef = "vault://local/mail", webhookUrl = (string?)null, webhookSecretRef = (string?)null });
            Assert.Throws<WebApi.Contracts.Common.ApiException>(() => WebApi.Infrastructure.Settings.SettingsValueCodec.Decode("notification", json));
            await LegacyNotificationUsesManagedValidatorAndSqlBlocksSample(host, "ops@example.test", "vault://local/mail", null, null);
        }
    }

    [Theory]
    [InlineData(false, "{\"inConsole\":true,\"requestedChannels\":[\"Email\",\"Email\"]}")]
    [InlineData(true, "{\"inConsole\":true,\"requestedChannels\":[\"Email\",\"Email\"]}")]
    [InlineData(false, "{\"inConsole\":true,\"requestedChannels\":[],\"externalEnabled\":false}")]
    [InlineData(true, "{\"inConsole\":true,\"requestedChannels\":[],\"externalEnabled\":false}")]
    public async Task InvalidLegacyAlertRuleStopsBeforeAnyEarlierMigration(bool sqlEntry, string invalid)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var user = Guid.NewGuid(); var org = Guid.NewGuid(); var project = Guid.NewGuid(); var env = Guid.NewGuid(); var rule = Guid.NewGuid();
        string[] before; string fingerprint;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261005120000_SsoOidc");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,password_hash,security_stamp,status,auth_source,created_at,updated_at) VALUES({user},{user.ToString()},'Keep','hash','stamp','Active','local',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({org},'KEEP','Keep','Active',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES({project},{org},'KEEP','Keep','Active',now(),now())");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status,deployment_sequence,revision) VALUES({env},{project},'KEEP','Keep',false,0,'Active',0,1)");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO alert_rules(id,organization_id,project_id,environment_id,name,normalized_name,metric,expression,severity,enabled,for_seconds,target_type,window_seconds,notification,revision,logic_revision,created_by,updated_by,created_at,updated_at) VALUES({rule},{org},{project},{env},'Keep','KEEP','request_rps','request_rps > 10','Warning',true,0,'Environment',60,{invalid}::jsonb,7,3,{user},{user},now(),now())");
            before = (await setup.Database.GetAppliedMigrationsAsync()).ToArray();
            fingerprint = await setup.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync();
        }
        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            Assert.NotNull(error);
            Assert.Contains("governance_migration_preflight_failed: managed_notification_validation_required", error.MessageText);
            Assert.Contains(rule.ToString(), error.MessageText);
        }
        else
        {
            var result = await RunMigratorAsync(database);
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(result.NotificationPreflightBlocked);
        }
        await using var check = database.Context();
        Assert.Equal(before, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(fingerprint, await check.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        Assert.True(await check.Database.SqlQuery<bool>($"SELECT notification={invalid}::jsonb AND revision=7 AND logic_revision=3 AS \"Value\" FROM public.alert_rules WHERE id={rule}").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatestMigrationHasAReproducibleApprovedSchemaFingerprint(bool sqlEntry)
    {
        var fingerprints = new List<string>();
        string? latest = null;
        for (var i = 0; i < 2; i++)
        {
            await using var database = new PostgresDatabase();
            await database.InitializeAsync();
            if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
            await using var db = database.Context();
            if (!sqlEntry) await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            latest = (await db.Database.GetAppliedMigrationsAsync()).Last();
            fingerprints.Add(await db.Database.SqlQueryRaw<string>(GovernanceSchemaFingerprints.Query).SingleAsync());
        }
        Assert.Equal(fingerprints[0], fingerprints[1]);
        Assert.True(GovernanceSchemaFingerprints.Expected.TryGetValue(latest!, out var expected),
            $"Missing independently reproduced fingerprint for {latest}: {fingerprints[0]} / {fingerprints[1]}");
        Assert.True(expected == fingerprints[0], $"Fingerprint mismatch for {latest}: expected {expected}, actual {fingerprints[0]}");
    }

    [Fact]
    public async Task DraftGovernanceHistoryBlocksMigratorBeforeAnySchemaChange()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await SeedUnknownHistoryAsync(database);

        var result = await RunMigratorAsync(database);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(result.PreflightBlocked);
        await AssertUnchangedAsync(database);
    }

    [Fact]
    public async Task DraftGovernanceHistoryBlocksInstallSqlBeforeAnySchemaChange()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await SeedUnknownHistoryAsync(database);
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var sql = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("governance_migration_preflight_failed", error.MessageText);
        await AssertUnchangedAsync(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyDatabaseRemainsSupported(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();

        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);

        await using var db = database.Context();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyWellFormedMigrationHistoryRemainsSupported(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var connection = await database.OpenAsync())
        await using (var command = new NpgsqlCommand("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" character varying(150) NOT NULL, \"ProductVersion\" character varying(32) NOT NULL, CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY (\"MigrationId\"))", connection))
            await command.ExecuteNonQueryAsync();

        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
        await using var check = database.Context();
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedEmptyMigrationHistoryBlocksBeforeAnyWrite(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var connection = await database.OpenAsync())
        await using (var command = new NpgsqlCommand("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" character varying(150), \"ProductVersion\" character varying(32), extra text)", connection))
            await command.ExecuteNonQueryAsync();

        await AssertPreflightBlockedAsync(database, sqlEntry);

        await using var check = await database.OpenAsync();
        await using var verify = new NpgsqlCommand("SELECT (SELECT count(*) FROM public.\"__EFMigrationsHistory\") = 0 AND (SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname='public') = 1", check);
        Assert.True((bool)(await verify.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownMainPrefixUpgradesWithoutChangingExistingBusinessData(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261008020000_ApprovalInboxIndexes");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'KEEP','Keep original','Active',now(),now(),7)");
        }

        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);

        await using var check = database.Context();
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        var preserved = await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync();
        Assert.Equal("Keep original", preserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReachableOldWriterBlocksBeforeGovernanceMigration(bool sqlEntry)
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        Console.WriteLine("isolated-test-db=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var role = "gov24_fence_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-test-role=" + role);
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{role}\" LOGIN; GRANT USAGE ON SCHEMA public TO \"{role}\"; GRANT UPDATE ON application_credentials TO \"{role}\"", connection))
            await create.ExecuteNonQueryAsync();
        var passed = false;
        try
        {
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var check = database.Context();
            Assert.Equal(21, (await check.Database.GetAppliedMigrationsAsync()).Count());
            Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='application_credentials' AND column_name='binding_mode') AS \"Value\"").SingleAsync());
            await using (var connection = await database.OpenAsync())
            await using (var disable = new NpgsqlCommand($"ALTER ROLE \"{role}\" NOLOGIN", connection))
                await disable.ExecuteNonQueryAsync();
            if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
            else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
            Assert.Empty(await check.Database.GetPendingMigrationsAsync());
            passed = true;
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                if (passed)
                {
                    await using (var connection = await database.OpenAsync())
                    await using (var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"", connection))
                        await cleanup.ExecuteNonQueryAsync();
                    await database.DisposeAsync();
                }
                else Console.WriteLine("isolated-test-fixture-retained");
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldWriterReachableThroughSetRoleAlsoBlocksBeforeGovernanceMigration(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var suffix = Guid.NewGuid().ToString("N");
        var writer = "gov24_writer_" + suffix;
        var member = "gov24_member_" + suffix;
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{writer}\" NOLOGIN; CREATE ROLE \"{member}\" LOGIN NOINHERIT; GRANT \"{writer}\" TO \"{member}\" WITH INHERIT FALSE, SET TRUE; GRANT UPDATE ON application_credentials TO \"{writer}\"", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var check = database.Context();
            Assert.Equal(21, (await check.Database.GetAppliedMigrationsAsync()).Count());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{member}\"; DROP OWNED BY \"{writer}\"; REVOKE \"{writer}\" FROM \"{member}\"; DROP ROLE \"{member}\"; DROP ROLE \"{writer}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldWriterRecoverableThroughAdminOptionBlocksBeforeGovernanceMigration(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'FENCE','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var writer = "gov24_admin_writer_" + suffix;
        var member = "gov24_admin_member_" + suffix;
        const string fixturePassword = "isolated-test-only-admin-option-4572";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{writer}\" NOLOGIN; CREATE ROLE \"{member}\" LOGIN NOINHERIT PASSWORD '{fixturePassword}'; GRANT \"{writer}\" TO \"{member}\" WITH ADMIN TRUE, INHERIT FALSE, SET FALSE; GRANT USAGE ON SCHEMA public TO \"{writer}\"; GRANT SELECT, UPDATE ON organizations TO \"{writer}\"", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.pg_has_role('{member}','{writer}','SET') AND NOT pg_catalog.has_table_privilege('{member}','public.organizations','UPDATE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);

            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = member, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                await using var escalation = new NpgsqlCommand($"GRANT \"{writer}\" TO \"{member}\" WITH SET TRUE; SET LOCAL ROLE \"{writer}\"; UPDATE public.organizations SET name='ShouldRollBack' WHERE id=@id RETURNING name", actor, transaction);
                escalation.Parameters.AddWithValue("id", organization);
                Assert.Equal("ShouldRollBack", (string?)await escalation.ExecuteScalarAsync());
                await transaction.RollbackAsync();
            }

            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{member}\"; DROP OWNED BY \"{writer}\"; REVOKE \"{writer}\" FROM \"{member}\"; DROP ROLE \"{member}\"; DROP ROLE \"{writer}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false, "admin-then-set")]
    [InlineData(true, "admin-then-set")]
    [InlineData(false, "set-then-admin")]
    [InlineData(true, "set-then-admin")]
    public async Task OldWriterRecoverableThroughMixedRoleChainsBlocksBeforeGovernanceMigration(bool sqlEntry, string path)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'CHAIN','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var writer = "gov24_chain_writer_" + suffix;
        var middle = "gov24_chain_mid_" + suffix;
        var member = "gov24_chain_member_" + suffix;
        const string fixturePassword = "isolated-test-only-role-chain-4672";
        var grants = path == "admin-then-set"
            ? $"GRANT \"{middle}\" TO \"{member}\" WITH ADMIN TRUE, INHERIT FALSE, SET FALSE; GRANT \"{writer}\" TO \"{middle}\" WITH INHERIT FALSE, SET TRUE"
            : $"GRANT \"{middle}\" TO \"{member}\" WITH INHERIT FALSE, SET TRUE; GRANT \"{writer}\" TO \"{middle}\" WITH ADMIN TRUE, INHERIT FALSE, SET FALSE";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{writer}\" NOLOGIN; CREATE ROLE \"{middle}\" NOLOGIN; CREATE ROLE \"{member}\" LOGIN NOINHERIT PASSWORD '{fixturePassword}'; {grants}; GRANT USAGE ON SCHEMA public TO \"{writer}\"; GRANT SELECT, UPDATE ON organizations TO \"{writer}\"", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.pg_has_role('{member}','{writer}','SET') AND NOT pg_catalog.has_table_privilege('{member}','public.organizations','UPDATE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            var promote = path == "admin-then-set"
                ? $"GRANT \"{middle}\" TO \"{member}\" WITH SET TRUE; SET LOCAL ROLE \"{writer}\""
                : $"SET LOCAL ROLE \"{middle}\"; GRANT \"{writer}\" TO \"{middle}\" WITH SET TRUE; SET LOCAL ROLE \"{writer}\"";
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = member, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                await using var escalation = new NpgsqlCommand($"{promote}; UPDATE public.organizations SET name='ShouldRollBack' WHERE id=@id RETURNING name", actor, transaction);
                escalation.Parameters.AddWithValue("id", organization);
                Assert.Equal("ShouldRollBack", (string?)await escalation.ExecuteScalarAsync());
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{member}\"; DROP OWNED BY \"{middle}\"; DROP OWNED BY \"{writer}\"; REVOKE \"{writer}\" FROM \"{middle}\"; REVOKE \"{middle}\" FROM \"{member}\"; DROP ROLE \"{member}\"; DROP ROLE \"{middle}\"; DROP ROLE \"{writer}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminOptionForRoleWithoutWritePrivilegeDoesNotBlockSupportedUpgrade(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'READONLY','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var observer = "gov24_readonly_" + suffix;
        var member = "gov24_readmember_" + suffix;
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{observer}\" NOLOGIN; CREATE ROLE \"{member}\" LOGIN NOINHERIT; GRANT \"{observer}\" TO \"{member}\" WITH ADMIN TRUE, INHERIT FALSE, SET FALSE", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
            else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
            await using var check = database.Context();
            Assert.Empty(await check.Database.GetPendingMigrationsAsync());
            Assert.Equal("Preserved", await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{member}\"; DROP OWNED BY \"{observer}\"; REVOKE \"{observer}\" FROM \"{member}\"; DROP ROLE \"{member}\"; DROP ROLE \"{observer}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false, "ordinary")]
    [InlineData(true, "ordinary")]
    [InlineData(false, "pgxtoast")]
    [InlineData(true, "pgxtoast")]
    public async Task OldWriterReachableThroughSecurityDefinerBlocksBeforeGovernanceMigration(bool sqlEntry, string schemaKind)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'DEFINER','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var schema = (schemaKind == "pgxtoast" ? "pgxtoast_" : "gov24_definer_schema_") + suffix;
        var actorRole = "gov24_definer_actor_" + suffix;
        const string fixturePassword = "isolated-test-only-definer-9852";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"""
            CREATE ROLE "{actorRole}" LOGIN PASSWORD '{fixturePassword}';
            CREATE SCHEMA "{schema}";
            CREATE FUNCTION "{schema}".touch(p_id uuid) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $body$
            DECLARE changed text;
            BEGIN UPDATE public.organizations SET name='ShouldRollBack' WHERE id=p_id RETURNING name INTO changed; RETURN changed; END $body$;
            REVOKE ALL ON FUNCTION "{schema}".touch(uuid) FROM PUBLIC;
            GRANT USAGE ON SCHEMA "{schema}" TO "{actorRole}";
            GRANT EXECUTE ON FUNCTION "{schema}".touch(uuid) TO "{actorRole}";
            """, connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','UPDATE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                await using var call = new NpgsqlCommand($"SELECT \"{schema}\".touch(@id)", actor, transaction);
                call.Parameters.AddWithValue("id", organization);
                Assert.Equal("ShouldRollBack", (string?)await call.ExecuteScalarAsync());
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP FUNCTION \"{schema}\".touch(uuid); DROP SCHEMA \"{schema}\"; DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldWriterReachableThroughNestedSecurityDefinersBlocksBeforeGovernanceMigration(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'DEFCHAIN','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var schema = "gov24_def_chain_" + suffix;
        var owner = "gov24_def_owner_" + suffix;
        var actorRole = "gov24_def_actor_" + suffix;
        const string fixturePassword = "isolated-test-only-definer-chain-3528";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"""
            CREATE ROLE "{owner}" NOLOGIN;
            CREATE ROLE "{actorRole}" LOGIN PASSWORD '{fixturePassword}';
            CREATE SCHEMA "{schema}";
            CREATE FUNCTION "{schema}".inner_write(p_id uuid) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $body$
            DECLARE changed text;
            BEGIN UPDATE public.organizations SET name='ShouldRollBack' WHERE id=p_id RETURNING name INTO changed; RETURN changed; END $body$;
            CREATE FUNCTION "{schema}".outer_call(p_id uuid) RETURNS text LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $body$
            SELECT "{schema}".inner_write(p_id) $body$;
            GRANT USAGE, CREATE ON SCHEMA "{schema}" TO "{owner}";
            ALTER FUNCTION "{schema}".outer_call(uuid) OWNER TO "{owner}";
            REVOKE CREATE ON SCHEMA "{schema}" FROM "{owner}";
            REVOKE ALL ON FUNCTION "{schema}".inner_write(uuid) FROM PUBLIC;
            REVOKE ALL ON FUNCTION "{schema}".outer_call(uuid) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION "{schema}".inner_write(uuid) TO "{owner}";
            GRANT USAGE ON SCHEMA "{schema}" TO "{actorRole}";
            GRANT EXECUTE ON FUNCTION "{schema}".outer_call(uuid) TO "{actorRole}";
            """, connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','UPDATE') AND NOT pg_catalog.has_table_privilege('{owner}','public.organizations','UPDATE') AND NOT pg_catalog.has_function_privilege('{actorRole}','\"{schema}\".inner_write(uuid)','EXECUTE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                await using var call = new NpgsqlCommand($"SELECT \"{schema}\".outer_call(@id)", actor, transaction);
                call.Parameters.AddWithValue("id", organization);
                Assert.Equal("ShouldRollBack", (string?)await call.ExecuteScalarAsync());
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP FUNCTION \"{schema}\".outer_call(uuid); DROP FUNCTION \"{schema}\".inner_write(uuid); DROP SCHEMA \"{schema}\"; DROP OWNED BY \"{actorRole}\"; DROP OWNED BY \"{owner}\"; DROP ROLE \"{actorRole}\"; DROP ROLE \"{owner}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefinerWithoutWritePrivilegeDoesNotBlockSupportedUpgrade(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var suffix = Guid.NewGuid().ToString("N");
        var schema = "gov24_def_read_" + suffix;
        var owner = "gov24_def_ro_" + suffix;
        var actorRole = "gov24_def_user_" + suffix;
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"""
            CREATE ROLE "{owner}" NOLOGIN;
            CREATE ROLE "{actorRole}" LOGIN;
            CREATE SCHEMA "{schema}";
            CREATE FUNCTION "{schema}".read_only() RETURNS integer LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $body$ SELECT 7 $body$;
            GRANT USAGE, CREATE ON SCHEMA "{schema}" TO "{owner}";
            ALTER FUNCTION "{schema}".read_only() OWNER TO "{owner}";
            REVOKE CREATE ON SCHEMA "{schema}" FROM "{owner}";
            REVOKE ALL ON FUNCTION "{schema}".read_only() FROM PUBLIC;
            GRANT USAGE ON SCHEMA "{schema}" TO "{actorRole}";
            GRANT EXECUTE ON FUNCTION "{schema}".read_only() TO "{actorRole}";
            """, connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{owner}','public.organizations','UPDATE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
            else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
            await using var checkDb = database.Context();
            Assert.Empty(await checkDb.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP FUNCTION \"{schema}\".read_only(); DROP SCHEMA \"{schema}\"; DROP OWNED BY \"{actorRole}\"; DROP OWNED BY \"{owner}\"; DROP ROLE \"{actorRole}\"; DROP ROLE \"{owner}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false, "UPDATE")]
    [InlineData(true, "UPDATE")]
    [InlineData(false, "INSERT")]
    [InlineData(true, "INSERT")]
    public async Task OldWriterWithColumnLevelPermissionBlocksBeforeGovernanceMigration(bool sqlEntry, string operation)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'COLKEEP','Preserved','Active',now(),now(),1)");
        }
        var actorRole = "gov24_column_" + Guid.NewGuid().ToString("N");
        const string fixturePassword = "isolated-test-only-column-6482";
        var columnGrant = operation == "UPDATE" ? "UPDATE(name)" : "INSERT(id,code,name,status,created_at,updated_at,revision)";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{actorRole}\" LOGIN PASSWORD '{fixturePassword}'; GRANT USAGE ON SCHEMA public TO \"{actorRole}\"; GRANT SELECT, {columnGrant} ON organizations TO \"{actorRole}\"", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','{operation}') AND pg_catalog.has_any_column_privilege('{actorRole}','public.organizations','{operation}')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                var sql = operation == "UPDATE"
                    ? "UPDATE public.organizations SET name='ShouldRollBack' WHERE id=@id"
                    : "INSERT INTO public.organizations(id,code,name,status,created_at,updated_at,revision) VALUES(@id,'COLNEW','ShouldRollBack','Active',now(),now(),1)";
                await using var write = new NpgsqlCommand(sql, actor, transaction);
                write.Parameters.AddWithValue("id", operation == "UPDATE" ? organization : Guid.NewGuid());
                Assert.Equal(1, await write.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
            Assert.False(await checkDb.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM organizations WHERE code='COLNEW') AS \"Value\"").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShadowedPrivilegeFunctionCannotHideReachableOldWriterDuringUpgrade(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'SHADOW','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var schema = "gov24_shadow_" + suffix;
        var actorRole = "gov24_shadow_user_" + suffix;
        var databaseName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        var quotedDatabase = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"""
            CREATE ROLE "{actorRole}" LOGIN;
            GRANT UPDATE ON public.organizations TO "{actorRole}";
            CREATE SCHEMA "{schema}";
            CREATE FUNCTION "{schema}".has_database_privilege(oid,text,text) RETURNS boolean LANGUAGE sql IMMUTABLE AS $body$ SELECT false $body$;
            """, connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var configure = new NpgsqlCommand($"ALTER DATABASE {quotedDatabase} SET search_path TO public, \"{schema}\", pg_catalog", connection))
                await configure.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT pg_catalog.current_schema()='public' AND pg_catalog.has_database_privilege('{actorRole}',pg_catalog.current_database(),'CONNECT') AND NOT has_database_privilege('{actorRole}'::pg_catalog.regrole::oid,pg_catalog.current_database()::text,'CONNECT') AND pg_catalog.has_table_privilege('{actorRole}','public.organizations','UPDATE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.PreflightBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using (var reset = new NpgsqlCommand($"ALTER DATABASE {quotedDatabase} RESET search_path", connection))
                    await reset.ExecuteNonQueryAsync();
                await using var cleanup = new NpgsqlCommand($"DROP FUNCTION \"{schema}\".has_database_privilege(oid,text,text); DROP SCHEMA \"{schema}\"; DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoginRoleUsedAsCurrentMigrationRoleStillBlocksAsOldWriter(bool sqlEntry)
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        var oldRole = "gov24_current_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-test-db=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database + "; role=" + oldRole);
        const string fixturePassword = "isolated-test-only-current-role-4751";
        var databaseName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        var quotedDatabase = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        var roleCreated = false;
        var roleConfigured = false;
        var passed = false;
        try
        {
            var organization = Guid.NewGuid();
            await using (var setup = database.Context())
            {
                await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'CURROLE','Preserved','Active',now(),now(),1)");
            }
            await using (var connection = await database.OpenAsync())
            await using (var create = new NpgsqlCommand($"CREATE ROLE \"{oldRole}\" LOGIN PASSWORD '{fixturePassword}'; GRANT pg_read_all_stats TO \"{oldRole}\"; GRANT USAGE ON SCHEMA public TO \"{oldRole}\"; GRANT SELECT, UPDATE ON ALL TABLES IN SCHEMA public TO \"{oldRole}\"", connection))
                await create.ExecuteNonQueryAsync();
            roleCreated = true;
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = oldRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                await using var write = new NpgsqlCommand("UPDATE public.organizations SET name='ShouldRollBack' WHERE id=@id", actor, transaction);
                write.Parameters.AddWithValue("id", organization);
                Assert.Equal(1, await write.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }
            await using (var connection = await database.OpenAsync())
            await using (var configure = new NpgsqlCommand($"ALTER ROLE webapi IN DATABASE {quotedDatabase} SET role TO \"{oldRole}\"", connection))
                await configure.ExecuteNonQueryAsync();
            roleConfigured = true;
            NpgsqlConnection.ClearAllPools();
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT current_user='{oldRole}' AND session_user='webapi'", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
            passed = true;
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                var adminOptions = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres", Pooling = false };
                if (passed && roleConfigured)
                {
                    NpgsqlConnection.ClearAllPools();
                    await using var connection = new NpgsqlConnection(adminOptions.ConnectionString);
                    await connection.OpenAsync();
                    await using (var reset = new NpgsqlCommand($"ALTER ROLE webapi IN DATABASE {quotedDatabase} RESET role", connection))
                        await reset.ExecuteNonQueryAsync();
                }
                // If the unpatched installer succeeds, new test-db objects may be
                // owned by oldRole. Drop only this test database before its role.
                if (passed) await database.DisposeAsync();
                if (passed && roleCreated)
                {
                    await using var cleanupConnection = new NpgsqlConnection(adminOptions.ConnectionString);
                    await cleanupConnection.OpenAsync();
                    await using var cleanup = new NpgsqlCommand($"DROP ROLE \"{oldRole}\"", cleanupConnection);
                    await cleanup.ExecuteNonQueryAsync();
                }
                if (!passed) Console.WriteLine("isolated-db-role-and-role-setting-retained");
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonLoginCurrentMigrationRoleDoesNotAloneBlockSupportedUpgrade(bool sqlEntry)
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        var migrationRole = "gov24_nologin_" + Guid.NewGuid().ToString("N");
        var databaseName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        var quotedDatabase = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        var roleCreated = false;
        var roleConfigured = false;
        try
        {
            var organization = Guid.NewGuid();
            await using (var setup = database.Context())
            {
                await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'NOLOGIN','Preserved','Active',now(),now(),1)");
            }
            await using (var connection = await database.OpenAsync())
            await using (var create = new NpgsqlCommand($"CREATE ROLE \"{migrationRole}\" NOLOGIN SUPERUSER", connection))
                await create.ExecuteNonQueryAsync();
            roleCreated = true;
            await using (var connection = await database.OpenAsync())
            await using (var configure = new NpgsqlCommand($"ALTER ROLE webapi IN DATABASE {quotedDatabase} SET role TO \"{migrationRole}\"", connection))
                await configure.ExecuteNonQueryAsync();
            roleConfigured = true;
            NpgsqlConnection.ClearAllPools();
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT current_user='{migrationRole}' AND session_user='webapi'", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
            else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
            await using var checkDb = database.Context();
            Assert.Empty(await checkDb.Database.GetPendingMigrationsAsync());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                var adminOptions = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres", Pooling = false };
                if (roleConfigured)
                {
                    NpgsqlConnection.ClearAllPools();
                    await using var connection = new NpgsqlConnection(adminOptions.ConnectionString);
                    await connection.OpenAsync();
                    await using (var reset = new NpgsqlCommand($"ALTER ROLE webapi IN DATABASE {quotedDatabase} RESET role", connection))
                        await reset.ExecuteNonQueryAsync();
                }
                await database.DisposeAsync();
                if (roleCreated)
                {
                    await using var cleanupConnection = new NpgsqlConnection(adminOptions.ConnectionString);
                    await cleanupConnection.OpenAsync();
                    await using var cleanup = new NpgsqlCommand($"DROP ROLE \"{migrationRole}\"", cleanupConnection);
                    await cleanup.ExecuteNonQueryAsync();
                }
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSessionRetainsPreviouslySetWriterRoleAfterMembershipRevocation(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'LIVESET','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var writer = "gov24_live_writer_" + suffix;
        var actorRole = "gov24_live_actor_" + suffix;
        const string fixturePassword = "isolated-test-only-live-role-6891";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{writer}\" NOLOGIN; CREATE ROLE \"{actorRole}\" LOGIN NOINHERIT PASSWORD '{fixturePassword}'; GRANT \"{writer}\" TO \"{actorRole}\" WITH INHERIT FALSE, SET TRUE; GRANT USAGE ON SCHEMA public TO \"{writer}\"; GRANT SELECT, UPDATE ON organizations TO \"{writer}\"", connection))
            await create.ExecuteNonQueryAsync();
        var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
        await using var actor = new NpgsqlConnection(options.ConnectionString);
        try
        {
            await actor.OpenAsync();
            await using (var switchRole = new NpgsqlCommand($"SET ROLE \"{writer}\"", actor)) await switchRole.ExecuteNonQueryAsync();
            await using (var connection = await database.OpenAsync())
            await using (var revoke = new NpgsqlCommand($"REVOKE \"{writer}\" FROM \"{actorRole}\"", connection))
                await revoke.ExecuteNonQueryAsync();
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.pg_has_role('{actorRole}','{writer}','SET') AND NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','UPDATE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            await using (var transaction = await actor.BeginTransactionAsync())
            {
                await using var write = new NpgsqlCommand("UPDATE public.organizations SET name='ShouldRollBack' WHERE id=@id", actor, transaction);
                write.Parameters.AddWithValue("id", organization);
                Assert.Equal(1, await write.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await actor.DisposeAsync();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{actorRole}\"; DROP OWNED BY \"{writer}\"; DROP ROLE \"{actorRole}\"; DROP ROLE \"{writer}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedReadOnlyClientMustDisconnectBeforeUpgrade(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'READER','Preserved','Active',now(),now(),1)");
        }
        var actorRole = "gov24_live_reader_" + Guid.NewGuid().ToString("N");
        const string fixturePassword = "isolated-test-only-live-reader-5362";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{actorRole}\" LOGIN PASSWORD '{fixturePassword}'; GRANT USAGE ON SCHEMA public TO \"{actorRole}\"; GRANT SELECT ON organizations TO \"{actorRole}\"", connection))
            await create.ExecuteNonQueryAsync();
        var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
        await using var actor = new NpgsqlConnection(options.ConnectionString);
        try
        {
            await actor.OpenAsync();
            await using (var read = new NpgsqlCommand("SELECT name FROM public.organizations WHERE id=@id", actor))
            {
                read.Parameters.AddWithValue("id", organization);
                Assert.Equal("Preserved", (string?)await read.ExecuteScalarAsync());
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await actor.DisposeAsync();
            if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
            else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
            await using var check = database.Context();
            Assert.Empty(await check.Database.GetPendingMigrationsAsync());
            Assert.Equal("Preserved", await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await actor.DisposeAsync();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false, "direct")]
    [InlineData(true, "direct")]
    [InlineData(false, "inherited")]
    [InlineData(true, "inherited")]
    public async Task TableOwnerCanRegrantRevokedWritePermissionAndMustBlockUpgrade(bool sqlEntry, string kind)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'OWNER','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var actorRole = "gov24_owner_actor_" + suffix;
        var owner = kind == "direct" ? actorRole : "gov24_owner_group_" + suffix;
        const string fixturePassword = "isolated-test-only-owner-7562";
        var extra = kind == "direct" ? "" : $"CREATE ROLE \"{owner}\" NOLOGIN; GRANT \"{owner}\" TO \"{actorRole}\" WITH INHERIT TRUE, SET FALSE;";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{actorRole}\" LOGIN PASSWORD '{fixturePassword}'; {extra} ALTER TABLE public.organizations OWNER TO \"{owner}\"; REVOKE ALL ON public.organizations FROM \"{owner}\"", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','UPDATE') AND NOT pg_catalog.has_any_column_privilege('{actorRole}','public.organizations','INSERT, UPDATE') AND pg_catalog.pg_has_role('{actorRole}','{owner}','USAGE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                await using var write = new NpgsqlCommand($"GRANT SELECT, UPDATE ON public.organizations TO \"{actorRole}\"; UPDATE public.organizations SET name='ShouldRollBack' WHERE id=@id", actor, transaction);
                write.Parameters.AddWithValue("id", organization);
                Assert.Equal(1, await write.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                var removeRoles = kind == "direct"
                    ? $"DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\""
                    : $"DROP OWNED BY \"{actorRole}\"; DROP OWNED BY \"{owner}\"; REVOKE \"{owner}\" FROM \"{actorRole}\"; DROP ROLE \"{actorRole}\"; DROP ROLE \"{owner}\"";
                await using var cleanup = new NpgsqlCommand($"ALTER TABLE public.organizations OWNER TO webapi; {removeRoles}", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryMigrationHistoryCannotShadowRealPublicHistory(bool sqlEntry)
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        Console.WriteLine("isolated-test-db-retained=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        string[] known;
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            known = setup.Database.GetMigrations().ToArray();
        }
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var sql = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        await using var connection = await database.OpenAsync();
        await using (var shadow = new NpgsqlCommand("CREATE TEMP TABLE \"__EFMigrationsHistory\" (LIKE public.\"__EFMigrationsHistory\" INCLUDING ALL); INSERT INTO pg_temp.\"__EFMigrationsHistory\"(\"MigrationId\",\"ProductVersion\") SELECT id,'10.0.12' FROM pg_catalog.unnest(@ids) AS id", connection))
        {
            shadow.Parameters.AddWithValue("ids", known);
            await shadow.ExecuteNonQueryAsync();
        }
        await using (var check = new NpgsqlCommand("SELECT pg_catalog.current_schema()='public' AND pg_catalog.starts_with((pg_catalog.current_schemas(true))[1]::text,'pg_temp_') AND (SELECT count(*) FROM public.\"__EFMigrationsHistory\")=21 AND (SELECT count(*) FROM \"__EFMigrationsHistory\")=@known", connection))
        {
            check.Parameters.AddWithValue("known", known.Length);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
        }
        if (sqlEntry)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("governance_migration_preflight_failed: unsupported search path", error.MessageText);
            await using var rollback = new NpgsqlCommand("ROLLBACK", connection);
            await rollback.ExecuteNonQueryAsync();
        }
        else
        {
            await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection).Options);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GovernanceMigrationPreflight.ValidateAsync(db));
            Assert.Contains("governance_migration_preflight_failed: unsupported search path", error.Message);
        }
        await using var verify = new NpgsqlCommand("SELECT count(*)::int FROM public.\"__EFMigrationsHistory\"", connection);
        Assert.Equal(21, (int)(await verify.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedMigrationRoleCannotIgnoreAnotherLiveClient(bool sqlEntry)
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        Console.WriteLine("isolated-test-db=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        NpgsqlConnection.ClearAllPools();
        var role = "gov24_restricted_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-test-role=" + role);
        await using var admin = await database.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{role}\" NOLOGIN; GRANT USAGE ON SCHEMA public TO \"{role}\"; GRANT SELECT, UPDATE ON ALL TABLES IN SCHEMA public TO \"{role}\"", admin))
            await create.ExecuteNonQueryAsync();
        var passed = false;
        try
        {
            await using var db = database.Context();
            await db.Database.OpenConnectionAsync();
            await using (var assume = db.Database.GetDbConnection().CreateCommand())
            {
                assume.CommandText = $"SET ROLE \"{role}\"";
                await assume.ExecuteNonQueryAsync();
            }
            try
            {
                if (sqlEntry)
                {
                    var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
                    var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
                    const string marker = "$WEBAPI_PREFLIGHT$;";
                    var end = script.IndexOf(marker, StringComparison.Ordinal);
                    Assert.True(end >= 0);
                    await using var preflight = db.Database.GetDbConnection().CreateCommand();
                    preflight.CommandText = SqlPreflightStatement(script);
                    var error = await Assert.ThrowsAsync<PostgresException>(() => preflight.ExecuteNonQueryAsync());
                    Assert.Contains("governance_migration_preflight_failed: activity_visibility_unverified", error.MessageText);
                }
                else
                {
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GovernanceMigrationPreflight.ValidateAsync(db));
                    Assert.Contains("governance_migration_preflight_failed: activity_visibility_unverified", error.Message);
                }
            }
            finally
            {
                await using var reset = db.Database.GetDbConnection().CreateCommand();
                reset.CommandText = "RESET ROLE";
                await reset.ExecuteNonQueryAsync();
            }
            passed = true;
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                if (passed)
                {
                    await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"", admin);
                    await cleanup.ExecuteNonQueryAsync();
                    await database.DisposeAsync();
                }
                else Console.WriteLine("isolated-db-and-role-retained");
            });
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MigrationRoleWithActivityReadPermissionDetectsLiveClients(bool sqlEntry, bool sameIdentity)
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        Console.WriteLine("isolated-test-db=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        NpgsqlConnection.ClearAllPools();
        var suffix = Guid.NewGuid().ToString("N");
        var migrationRole = "gov24_monitor_" + suffix;
        var clientRole = "gov24_reader_" + suffix;
        Console.WriteLine("isolated-test-roles=" + migrationRole + "," + clientRole);
        const string fixturePassword = "isolated-test-only-reader-7048";
        await using (var admin = await database.OpenAsync())
        await using (var setup = new NpgsqlCommand($"CREATE ROLE \"{migrationRole}\" NOLOGIN; GRANT pg_read_all_stats TO \"{migrationRole}\"; GRANT USAGE ON SCHEMA public TO \"{migrationRole}\"; GRANT SELECT, UPDATE ON ALL TABLES IN SCHEMA public TO \"{migrationRole}\"; CREATE ROLE \"{clientRole}\" LOGIN PASSWORD '{fixturePassword}'", admin))
            await setup.ExecuteNonQueryAsync();
        NpgsqlConnection.ClearAllPools();
        var passed = false;
        try
        {
            var clientOptions = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
            if (!sameIdentity) { clientOptions.Username = clientRole; clientOptions.Password = fixturePassword; }
            await using var client = new NpgsqlConnection(clientOptions.ConnectionString);
            await client.OpenAsync();
            await using var db = database.Context();
            await db.Database.OpenConnectionAsync();
            await using (var assume = db.Database.GetDbConnection().CreateCommand())
            {
                assume.CommandText = $"SET ROLE \"{migrationRole}\"";
                await assume.ExecuteNonQueryAsync();
            }
            try
            {
                var expected = sameIdentity ? "same_identity_session" : "old_writer_unfenced";
                if (sqlEntry)
                {
                    var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
                    var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
                    const string marker = "$WEBAPI_PREFLIGHT$;";
                    var end = script.IndexOf(marker, StringComparison.Ordinal);
                    Assert.True(end >= 0);
                    await using var preflight = db.Database.GetDbConnection().CreateCommand();
                    preflight.CommandText = SqlPreflightStatement(script);
                    var error = await Assert.ThrowsAsync<PostgresException>(() => preflight.ExecuteNonQueryAsync());
                    Assert.Contains(expected, error.MessageText);
                    await client.DisposeAsync();
                    await preflight.ExecuteNonQueryAsync();
                }
                else
                {
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GovernanceMigrationPreflight.ValidateAsync(db));
                    Assert.Contains(expected, error.Message);
                    await client.DisposeAsync();
                    await GovernanceMigrationPreflight.ValidateAsync(db);
                }
            }
            finally
            {
                await using var reset = db.Database.GetDbConnection().CreateCommand();
                reset.CommandText = "RESET ROLE";
                await reset.ExecuteNonQueryAsync();
            }
            passed = true;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                if (passed)
                {
                    await using var admin = await database.OpenAsync();
                    await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{migrationRole}\"; DROP ROLE \"{migrationRole}\"; DROP OWNED BY \"{clientRole}\"; DROP ROLE \"{clientRole}\"", admin);
                    await cleanup.ExecuteNonQueryAsync();
                    await database.DisposeAsync();
                }
                else Console.WriteLine("isolated-db-and-roles-retained");
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterSearchPathOverloadCannotExecuteDuringPreflight(bool sqlEntry)
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        Console.WriteLine("isolated-test-db=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'OVERLOAD','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var actorRole = "gov24_overload_" + suffix;
        var schema = "gov24_overload_schema_" + suffix;
        Console.WriteLine("isolated-test-role=" + actorRole + "; schema=" + schema);
        const string fixturePassword = "isolated-test-only-overload-8159";
        await using (var admin = await database.OpenAsync())
        await using (var setup = new NpgsqlCommand($"CREATE ROLE \"{actorRole}\" LOGIN PASSWORD '{fixturePassword}'; CREATE SCHEMA \"{schema}\" AUTHORIZATION \"{actorRole}\"", admin))
            await setup.ExecuteNonQueryAsync();
        var passed = false;
        try
        {
            var actorOptions = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(actorOptions.ConnectionString))
            {
                await actor.OpenAsync();
                await using var create = new NpgsqlCommand($"""
                    CREATE SEQUENCE "{schema}".probe;
                    CREATE FUNCTION "{schema}".unnest(value text[]) RETURNS SETOF text LANGUAGE plpgsql AS $body$
                    BEGIN
                        PERFORM pg_catalog.nextval('"{schema}".probe'::pg_catalog.regclass);
                        UPDATE public.organizations SET name='ShouldRollBack' WHERE id='{organization}';
                        RETURN QUERY SELECT * FROM pg_catalog.unnest(value);
                    END $body$;
                    """, actor);
                await create.ExecuteNonQueryAsync();
            }
            NpgsqlConnection.ClearAllPools();
            await using var connection = await database.OpenAsync();
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','UPDATE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using (var path = new NpgsqlCommand($"SET LOCAL search_path TO public, \"{schema}\"", connection, transaction))
                    await path.ExecuteNonQueryAsync();
                await using (var resolution = new NpgsqlCommand($"SELECT pg_catalog.to_regprocedure('unnest(text[])') = pg_catalog.to_regprocedure('\"{schema}\".unnest(text[])')", connection, transaction))
                    Assert.True((bool)(await resolution.ExecuteScalarAsync())!);
                Exception? failure;
                if (sqlEntry)
                {
                    var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
                    var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
                    const string marker = "$WEBAPI_PREFLIGHT$;";
                    var end = script.IndexOf(marker, StringComparison.Ordinal);
                    Assert.True(end >= 0);
                    await using var preflight = new NpgsqlCommand(SqlPreflightStatement(script), connection, transaction);
                    failure = await Record.ExceptionAsync(() => preflight.ExecuteNonQueryAsync());
                }
                else
                {
                    await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection).Options);
                    await db.Database.UseTransactionAsync(transaction);
                    failure = await Record.ExceptionAsync(() => GovernanceMigrationPreflight.ValidateAsync(db));
                }
                await transaction.RollbackAsync();
                if (failure is not null) Assert.Contains("governance_migration_preflight_failed", failure.ToString());
            }
            await using var verify = new NpgsqlCommand($"SELECT NOT is_called AND (SELECT name FROM public.organizations WHERE id=@id)='Preserved' FROM \"{schema}\".probe", connection);
            verify.Parameters.AddWithValue("id", organization);
            Assert.True((bool)(await verify.ExecuteScalarAsync())!);
            passed = true;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                if (passed)
                {
                    await using var admin = await database.OpenAsync();
                    await using var cleanup = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE; DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", admin);
                    await cleanup.ExecuteNonQueryAsync();
                    await database.DisposeAsync();
                }
                else Console.WriteLine("isolated-db-role-and-schema-retained");
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicFingerprintFunctionOverloadCannotExecuteBeforeMismatch(bool sqlEntry)
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        Console.WriteLine("isolated-test-db=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'PUBLICOVERLOAD','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var actorRole = "gov24_public_overload_" + suffix;
        var schema = "gov24_public_probe_" + suffix;
        Console.WriteLine("isolated-test-role=" + actorRole + "; schema=" + schema);
        const string fixturePassword = "isolated-test-only-public-overload-5632";
        await using (var admin = await database.OpenAsync())
        await using (var setup = new NpgsqlCommand($"CREATE ROLE \"{actorRole}\" LOGIN PASSWORD '{fixturePassword}'; CREATE SCHEMA \"{schema}\" AUTHORIZATION \"{actorRole}\"; GRANT USAGE, CREATE ON SCHEMA public TO \"{actorRole}\"", admin))
            await setup.ExecuteNonQueryAsync();
        var passed = false;
        try
        {
            var actorOptions = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(actorOptions.ConnectionString))
            {
                await actor.OpenAsync();
                await using var create = new NpgsqlCommand($"""
                    CREATE SEQUENCE "{schema}".probe;
                    CREATE FUNCTION public.jsonb_build_array(a text,b name,c text,d text,e boolean,f boolean) RETURNS jsonb LANGUAGE plpgsql AS $body$
                    BEGIN
                        PERFORM pg_catalog.nextval('"{schema}".probe'::pg_catalog.regclass);
                        UPDATE public.organizations SET name='ShouldRollBack' WHERE id='{organization}';
                        RETURN pg_catalog.jsonb_build_array(a,b,c,d,e,f);
                    END $body$;
                    """, actor);
                await create.ExecuteNonQueryAsync();
            }
            await using (var admin = await database.OpenAsync())
            await using (var revoke = new NpgsqlCommand($"REVOKE CREATE ON SCHEMA public FROM \"{actorRole}\"", admin))
                await revoke.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearAllPools();
            await using var connection = await database.OpenAsync();
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','UPDATE') AND NOT pg_catalog.has_schema_privilege('{actorRole}','public','CREATE') AND pg_catalog.to_regprocedure('jsonb_build_array(text,name,text,text,boolean,boolean)')=pg_catalog.to_regprocedure('public.jsonb_build_array(text,name,text,text,boolean,boolean)')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            Exception? failure;
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                if (sqlEntry)
                {
                    var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
                    var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
                    const string marker = "$WEBAPI_PREFLIGHT$;";
                    var end = script.IndexOf(marker, StringComparison.Ordinal);
                    Assert.True(end >= 0);
                    await using var preflight = new NpgsqlCommand(SqlPreflightStatement(script), connection, transaction);
                    failure = await Record.ExceptionAsync(() => preflight.ExecuteNonQueryAsync());
                }
                else
                {
                    await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection).Options);
                    await db.Database.UseTransactionAsync(transaction);
                    failure = await Record.ExceptionAsync(() => GovernanceMigrationPreflight.ValidateAsync(db));
                }
                await transaction.RollbackAsync();
            }
            Assert.NotNull(failure);
            Assert.Contains("governance_migration_preflight_failed", failure.ToString());
            await using var verify = new NpgsqlCommand($"SELECT NOT is_called AND (SELECT name FROM public.organizations WHERE id=@id)='Preserved' FROM \"{schema}\".probe", connection);
            verify.Parameters.AddWithValue("id", organization);
            Assert.True((bool)(await verify.ExecuteScalarAsync())!);
            passed = true;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                if (passed)
                {
                    await using var admin = await database.OpenAsync();
                    await using var cleanup = new NpgsqlCommand($"DROP FUNCTION public.jsonb_build_array(text,name,text,text,boolean,boolean); DROP SCHEMA \"{schema}\" CASCADE; DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", admin);
                    await cleanup.ExecuteNonQueryAsync();
                    await database.DisposeAsync();
                }
                else Console.WriteLine("isolated-db-role-and-schema-retained");
            });
        }
    }

    [Fact]
    public async Task SqlEntryCanBeRepeatedOnSameConnectionAfterSourceCapture()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        NpgsqlConnection.ClearAllPools();
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        await using var connection = await database.OpenAsync();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var command = new NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync();
            await using var verify = new NpgsqlCommand("SELECT pg_catalog.pg_my_temp_schema()<>0 AND (SELECT count(*) FROM public.\"__EFMigrationsHistory\")=36", connection);
            Assert.True((bool)(await verify.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task EfPreflightCanBeRepeatedOnSameConnectionAfterSourceCapture()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        NpgsqlConnection.ClearAllPools();
        await using var db = database.Context();
        await db.Database.OpenConnectionAsync();
        await GovernanceMigrationPreflight.ValidateAsync(db);
        await GovernanceMigrationSourceCapture.CaptureAsync(db);
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.pg_my_temp_schema()<>0 AS \"Value\"").SingleAsync());
        await GovernanceMigrationPreflight.ValidateAsync(db);
        await db.Database.MigrateAsync();
        await GovernanceMigrationPreflight.ValidateAsync(db);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UntrustedTemporaryObjectBlocksEvenAfterSourceCapture(bool sqlEntry, bool typeObject)
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        Console.WriteLine("isolated-test-db-retained=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        NpgsqlConnection.ClearAllPools();
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        const string marker = "$WEBAPI_PREFLIGHT$;";
        var end = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(end >= 0);
        var preflight = SqlPreflightStatement(script);
        await using var connection = await database.OpenAsync();
        await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection).Options);
        if (sqlEntry)
        {
            await using var command = new NpgsqlCommand(preflight, connection);
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            await GovernanceMigrationPreflight.ValidateAsync(db);
            await GovernanceMigrationSourceCapture.CaptureAsync(db);
        }
        await using (var shadow = new NpgsqlCommand(typeObject
            ? "CREATE TYPE pg_temp.gov24_untrusted_type AS ENUM ('probe')"
            : "CREATE TEMP TABLE organizations(id uuid)", connection))
            await shadow.ExecuteNonQueryAsync();
        if (sqlEntry)
        {
            await using var command = new NpgsqlCommand(preflight, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("governance_migration_preflight_failed: unsupported search path", error.MessageText);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GovernanceMigrationPreflight.ValidateAsync(db));
            Assert.Contains("governance_migration_preflight_failed: unsupported search path", error.Message);
        }
    }

    [Fact]
    public async Task TimedOutOwnedMigratorProcessIsStoppedBeforeCleanup()
    {
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") : new ProcessStartInfo("/bin/sh");
        start.UseShellExecute = false;
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        start.ArgumentList.Add(OperatingSystem.IsWindows() ? "ping -n 31 127.0.0.1 > nul" : "sleep 30");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Isolated test process could not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitForMigratorExitAsync(process, timeout.Token));
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicOperatorCannotHideDefinerWithoutSourceSchemaUsage(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'OPDEF','Preserved','Active',now(),now(),1)");
        }
        var suffix = Guid.NewGuid().ToString("N");
        var schema = "gov24_hidden_" + suffix;
        var actorRole = "gov24_oper_actor_" + suffix;
        const string fixturePassword = "isolated-test-only-operator-2847";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"""
            CREATE ROLE "{actorRole}" LOGIN PASSWORD '{fixturePassword}';
            CREATE SCHEMA "{schema}";
            CREATE FUNCTION "{schema}".touch(p_id uuid, flag integer) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $body$
            DECLARE changed text;
            BEGIN UPDATE public.organizations SET name='ShouldRollBack' WHERE id=p_id RETURNING name INTO changed; RETURN changed; END $body$;
            REVOKE ALL ON SCHEMA "{schema}" FROM PUBLIC;
            REVOKE ALL ON FUNCTION "{schema}".touch(uuid,integer) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION "{schema}".touch(uuid,integer) TO "{actorRole}";
            CREATE OPERATOR public.@@@ (LEFTARG=uuid, RIGHTARG=integer, FUNCTION="{schema}".touch);
            """, connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_schema_privilege('{actorRole}','{schema}','USAGE') AND pg_catalog.has_function_privilege('{actorRole}','\"{schema}\".touch(uuid,integer)','EXECUTE') AND NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','UPDATE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                await using var call = new NpgsqlCommand("SELECT @id OPERATOR(public.@@@) 1", actor, transaction);
                call.Parameters.AddWithValue("id", organization);
                Assert.Equal("ShouldRollBack", (string?)await call.ExecuteScalarAsync());
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal("Preserved", await checkDb.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OPERATOR public.@@@ (uuid,integer); DROP FUNCTION \"{schema}\".touch(uuid,integer); DROP SCHEMA \"{schema}\"; DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false, "UPDATE")]
    [InlineData(true, "UPDATE")]
    [InlineData(false, "USAGE")]
    [InlineData(true, "USAGE")]
    public async Task OldWriterWithSequenceMutationPermissionBlocksBeforeUpgrade(bool sqlEntry, string privilege)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await setup.Database.ExecuteSqlRawAsync("SELECT pg_catalog.setval('public.audit_logs_id_seq'::pg_catalog.regclass,1,false)");
        }
        var actorRole = "gov24_sequence_" + Guid.NewGuid().ToString("N");
        const string fixturePassword = "isolated-test-only-sequence-6832";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{actorRole}\" LOGIN PASSWORD '{fixturePassword}'; GRANT USAGE ON SCHEMA public TO \"{actorRole}\"; GRANT {privilege} ON SEQUENCE public.audit_logs_id_seq TO \"{actorRole}\"", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{actorRole}','public.audit_logs','INSERT') AND pg_catalog.has_sequence_privilege('{actorRole}','public.audit_logs_id_seq','{privilege}')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                // PostgreSQL sequence changes are not rolled back. This test
                // changes only the newly created disposable database's sequence.
                var sql = privilege == "USAGE" ? "SELECT pg_catalog.nextval('public.audit_logs_id_seq'::pg_catalog.regclass)" : "SELECT pg_catalog.setval('public.audit_logs_id_seq'::pg_catalog.regclass,50000,true)";
                await using var write = new NpgsqlCommand(sql, actor);
                Assert.Equal(privilege == "USAGE" ? 1L : 50000L, (long)(await write.ExecuteScalarAsync())!);
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal(privilege == "USAGE" ? 1L : 50000L, await checkDb.Database.SqlQueryRaw<long>("SELECT last_value AS \"Value\" FROM public.audit_logs_id_seq").SingleAsync());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false, "schema")]
    [InlineData(true, "schema")]
    [InlineData(false, "database")]
    [InlineData(true, "database")]
    public async Task OldOwnerCanRegrantSchemaOrDatabaseCreationAndMustBlockUpgrade(bool sqlEntry, string kind)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var suffix = Guid.NewGuid().ToString("N");
        var actorRole = "gov24_object_owner_" + suffix;
        var probe = "gov24_owner_probe_" + suffix;
        const string fixturePassword = "isolated-test-only-object-owner-3842";
        var databaseName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        var quotedDatabase = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        string schemaOwner;
        await using (var connection = await database.OpenAsync())
        await using (var owner = new NpgsqlCommand("SELECT nspowner::pg_catalog.regrole::text FROM pg_catalog.pg_namespace WHERE nspname='public'", connection))
            schemaOwner = (string)(await owner.ExecuteScalarAsync())!;
        var quotedSchemaOwner = new NpgsqlCommandBuilder().QuoteIdentifier(schemaOwner);
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{actorRole}\" LOGIN PASSWORD '{fixturePassword}'", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            {
                var alter = kind == "schema"
                    ? $"ALTER SCHEMA public OWNER TO \"{actorRole}\"; REVOKE ALL ON SCHEMA public FROM \"{actorRole}\""
                    : $"ALTER DATABASE {quotedDatabase} OWNER TO \"{actorRole}\"; REVOKE CREATE ON DATABASE {quotedDatabase} FROM \"{actorRole}\"";
                await using var command = new NpgsqlCommand(alter, connection);
                await command.ExecuteNonQueryAsync();
            }
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','UPDATE')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                var write = kind == "schema"
                    ? $"GRANT USAGE, CREATE ON SCHEMA public TO \"{actorRole}\"; CREATE TABLE public.\"{probe}\"(id integer)"
                    : $"GRANT CREATE ON DATABASE {quotedDatabase} TO \"{actorRole}\"; CREATE SCHEMA \"{probe}\"";
                await using var command = new NpgsqlCommand(write, actor, transaction);
                await command.ExecuteNonQueryAsync();
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using (var connection = await database.OpenAsync())
                {
                    var restore = kind == "schema"
                        ? $"ALTER SCHEMA public OWNER TO {quotedSchemaOwner}"
                        : $"ALTER DATABASE {quotedDatabase} OWNER TO webapi";
                    await using var command = new NpgsqlCommand(restore, connection);
                    await command.ExecuteNonQueryAsync();
                }
                await using var cleanupConnection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", cleanupConnection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false, "schema")]
    [InlineData(true, "schema")]
    [InlineData(false, "database")]
    [InlineData(true, "database")]
    public async Task OldRoleWithCreationPermissionBlocksBeforeUpgrade(bool sqlEntry, string kind)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var suffix = Guid.NewGuid().ToString("N");
        var actorRole = "gov24_creator_" + suffix;
        var probe = "gov24_create_probe_" + suffix;
        const string fixturePassword = "isolated-test-only-create-6452";
        var databaseName = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        var quotedDatabase = new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
        var grant = kind == "schema"
            ? $"GRANT USAGE, CREATE ON SCHEMA public TO \"{actorRole}\""
            : $"GRANT CREATE ON DATABASE {quotedDatabase} TO \"{actorRole}\"";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{actorRole}\" LOGIN PASSWORD '{fixturePassword}'; {grant}", connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                var write = kind == "schema" ? $"CREATE TABLE public.\"{probe}\"(id integer)" : $"CREATE SCHEMA \"{probe}\"";
                await using var command = new NpgsqlCommand(write, actor, transaction);
                await command.ExecuteNonQueryAsync();
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var check = database.Context();
            Assert.Equal(21, (await check.Database.GetAppliedMigrationsAsync()).Count());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false, "TRIGGER")]
    [InlineData(true, "TRIGGER")]
    [InlineData(false, "MAINTAIN")]
    [InlineData(true, "MAINTAIN")]
    public async Task OldRoleWithTableMutationPrivilegeBlocksBeforeUpgrade(bool sqlEntry, string privilege)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var suffix = Guid.NewGuid().ToString("N");
        var actorRole = "gov24_table_priv_" + suffix;
        var schema = "gov24_trigger_" + suffix;
        const string fixturePassword = "isolated-test-only-table-priv-3295";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"""
            CREATE ROLE "{actorRole}" LOGIN PASSWORD '{fixturePassword}';
            CREATE SCHEMA "{schema}";
            CREATE FUNCTION "{schema}".probe() RETURNS trigger LANGUAGE plpgsql AS $body$ BEGIN RETURN NEW; END $body$;
            GRANT USAGE ON SCHEMA public, "{schema}" TO "{actorRole}";
            GRANT EXECUTE ON FUNCTION "{schema}".probe() TO "{actorRole}";
            GRANT {privilege} ON public.organizations TO "{actorRole}";
            """, connection))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using (var connection = await database.OpenAsync())
            await using (var check = new NpgsqlCommand($"SELECT NOT pg_catalog.has_table_privilege('{actorRole}','public.organizations','INSERT, UPDATE, DELETE, TRUNCATE') AND pg_catalog.has_table_privilege('{actorRole}','public.organizations','{privilege}')", connection))
                Assert.True((bool)(await check.ExecuteScalarAsync())!);
            var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = actorRole, Password = fixturePassword, Pooling = false };
            await using (var actor = new NpgsqlConnection(options.ConnectionString))
            {
                await actor.OpenAsync();
                await using var transaction = await actor.BeginTransactionAsync();
                var change = privilege == "TRIGGER"
                    ? $"CREATE TRIGGER gov24_probe BEFORE UPDATE ON public.organizations FOR EACH ROW EXECUTE FUNCTION \"{schema}\".probe()"
                    : "REINDEX TABLE public.organizations";
                await using var command = new NpgsqlCommand(change, actor, transaction);
                await command.ExecuteNonQueryAsync();
                await transaction.RollbackAsync();
            }
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await using var checkDb = database.Context();
            Assert.Equal(21, (await checkDb.Database.GetAppliedMigrationsAsync()).Count());
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP FUNCTION \"{schema}\".probe(); DROP SCHEMA \"{schema}\"; DROP OWNED BY \"{actorRole}\"; DROP ROLE \"{actorRole}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherLiveSessionUsingMigratorIdentityBlocksBeforeGovernanceMigration(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        NpgsqlConnection.ClearAllPools();
        await using var liveOldSession = await database.OpenAsync();
        try
        {
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: same_identity_session", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.SameIdentitySessionBlocked);
            }
        }
        finally { await liveOldSession.DisposeAsync(); NpgsqlConnection.ClearAllPools(); }
        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisablingOldLoginDoesNotEraseAnExistingWriterSession(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var setup = database.Context())
            await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var role = "gov24_active_" + Guid.NewGuid().ToString("N");
        const string fixturePassword = "isolated-test-only-3724";
        await using (var connection = await database.OpenAsync())
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{role}\" LOGIN PASSWORD '{fixturePassword}'; GRANT USAGE ON SCHEMA public TO \"{role}\"; GRANT UPDATE ON application_credentials TO \"{role}\"", connection))
            await create.ExecuteNonQueryAsync();
        var options = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role, Password = fixturePassword, Pooling = false };
        await using var oldSession = new NpgsqlConnection(options.ConnectionString);
        async Task AssertNewOldRoleLoginIsRejectedAsync()
        {
            await using var attempt = new NpgsqlConnection(options.ConnectionString);
            var error = await Assert.ThrowsAsync<PostgresException>(() => attempt.OpenAsync());
            Assert.Equal("28000", error.SqlState);
        }
        try
        {
            await oldSession.OpenAsync();
            await using (var connection = await database.OpenAsync())
            await using (var disable = new NpgsqlCommand($"ALTER ROLE \"{role}\" NOLOGIN", connection))
                await disable.ExecuteNonQueryAsync();
            await AssertNewOldRoleLoginIsRejectedAsync();
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("governance_migration_preflight_failed: old_writer_unfenced", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.WriterFenceBlocked);
            }
            await oldSession.DisposeAsync();
            await AssertNewOldRoleLoginIsRejectedAsync();
            if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
            else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
            await AssertNewOldRoleLoginIsRejectedAsync();
        }
        finally
        {
            await oldSession.DisposeAsync();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var connection = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand($"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"", connection);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NodeRegistrationIdentityHistoryCannotBeEditedOrDeleted(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
        await using var db = database.Context();
        var organization = new Organization { Code = "HISTORY-ORG", Name = "Organization" };
        var project = new Project { OrganizationId = organization.Id, Code = "HISTORY-PROJECT", Name = "Project" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test" };
        var node = new GatewayNode { EnvironmentId = environment.Id, NodeName = "history-node", IdentityHash = new string('a', 64), InstanceId = Guid.NewGuid().ToString() };
        var registration = new GatewayNodeEvent { GatewayNodeId = node.Id, EventType = "registered", Message = "registered", Detail = $"{{\"instanceId\":\"{node.InstanceId}\"}}" };
        var ordinary = new GatewayNodeEvent { GatewayNodeId = node.Id, EventType = "status", Message = "status" };
        db.AddRange(organization, project, environment, node, registration, ordinary);
        await db.SaveChangesAsync();
        var update = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE gateway_node_events SET detail='{{}}'::jsonb WHERE id={registration.Id}"));
        Assert.Equal("23514", update.SqlState);
        var delete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM gateway_node_events WHERE id={registration.Id}"));
        Assert.Equal("23514", delete.SqlState);
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE gateway_node_events"));
        Assert.Equal("23514", truncate.SqlState);
        var convert = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE gateway_node_events SET event_type='registered' WHERE id={ordinary.Id}"));
        Assert.Equal("23514", convert.SqlState);
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM gateway_node_events WHERE id={ordinary.Id}"));
        Assert.True(await db.Set<GatewayNodeEvent>().AnyAsync(x => x.Id == registration.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseIssuanceUpgradePreservesOldHorizonWithoutInventingEvidence(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var org = new Organization { Code = "LEASE-MIG-ORG", Name = "Organization" };
        var project = new Project { OrganizationId = org.Id, Code = "LEASE-MIG-P", Name = "Project" };
        var env = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test" };
        var node = new GatewayNode { EnvironmentId = env.Id, NodeName = "lease-migration", IdentityHash = new string('a', 64), InstanceId = Guid.NewGuid().ToString() };
        var instance = Guid.Parse(node.InstanceId); var generation = Guid.NewGuid(); var oldLeaseId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow; at = at.AddTicks(-(at.Ticks % 10));
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261104020000_Governance24RegistrationHistory");
            setup.AddRange(org, project, env, node, new EnvironmentSecurityInitialization { EnvironmentId = env.Id, ProjectId = project.Id,
                OrganizationId = org.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
            await setup.SaveChangesAsync();
            setup.Add(new EnvironmentSecurityState { EnvironmentId = env.Id, ProjectId = project.Id, OrganizationId = org.Id,
                SecurityGeneration = generation, InitialRequireEnvironmentCredentials = false, RequireEnvironmentCredentials = false, UpdatedAt = at });
            await setup.SaveChangesAsync();
            setup.Add(new GatewaySecurityLease { NodeId = node.Id, InstanceId = instance, EnvironmentId = env.Id, ProjectId = project.Id,
                OrganizationId = org.Id, SecurityGeneration = generation, Epoch = 0, CheckpointHash = new string('b', 64), LeaseId = oldLeaseId,
                IssuedAt = at, FreshUntil = at.AddSeconds(20), LastIssuedLeaseUntil = at.AddSeconds(20), SigningKeyId = "test-key", LeaseSeconds = 30 });
            await setup.SaveChangesAsync();
        }
        NpgsqlConnection.ClearAllPools();
        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
        await using var verify = database.Context();
        var horizon = await verify.Set<GatewaySecurityLease>().AsNoTracking().SingleAsync();
        Assert.Equal(oldLeaseId, horizon.LeaseId);
        Assert.Equal(at.AddSeconds(20), horizon.LastIssuedLeaseUntil);
        Assert.Empty(await verify.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().ToArrayAsync());
        var newId = Guid.NewGuid();
        verify.Add(new GatewaySecurityLeaseIssuance { LeaseId = newId, NodeId = node.Id, InstanceId = instance, EnvironmentId = env.Id,
            ProjectId = project.Id, OrganizationId = org.Id, SecurityGeneration = generation, Epoch = 0, CheckpointHash = new string('b', 64),
            PayloadHash = new string('c', 64), IssuedAt = at, FreshUntil = at.AddSeconds(20), SigningKeyId = "test-key" });
        await verify.SaveChangesAsync();
        foreach (var (sql, expected) in new[] { ("UPDATE gateway_security_lease_issuances SET payload_hash=payload_hash", "23514"),
            ("DELETE FROM gateway_security_lease_issuances", "23514"), ("TRUNCATE gateway_security_lease_issuances", "0A000"),
            ("TRUNCATE gateway_security_lease_issuances CASCADE", "23514") })
            Assert.Equal(expected, (await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlRawAsync(sql))).SqlState);
        Assert.Single(await verify.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LegacyAckRemainsUnverifiedAfterEitherUpgradeEntry(bool sqlEntry, bool priorAmbiguousVerification)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var org = new Organization { Code = "ACK-MIG-ORG", Name = "Organization" };
        var project = new Project { OrganizationId = org.Id, Code = "ACK-MIG-P", Name = "Project" };
        var env = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test" };
        var instance = Guid.NewGuid(); var generation = Guid.NewGuid(); var operation = Guid.NewGuid(); var challenge = Guid.NewGuid(); var lease = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow; at = at.AddTicks(-(at.Ticks % 10)); var expiry = at.AddSeconds(20); var hash = new string('a', 64);
        var node = new GatewayNode { EnvironmentId = env.Id, NodeName = "ack-migration", InstanceId = instance.ToString(), IdentityHash = new string('b', 64), LastHeartbeatAt = at };
        node.Metadata = SnapshotSchemaCapabilities.Merge(node.Metadata, instance, ["2.0", "2.1", "2.2", "2.3", "2.4"]);
        node.Metadata = SecurityProtocolCapabilities.Merge(node.Metadata, instance, ["security-ledger-v1"]);
        node.Metadata = SecurityApplicationReceipt.Merge(node.Metadata, new(node.Id, env.Id, instance, generation, 1, hash, at, hash, expiry, at, lease));
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync(priorAmbiguousVerification ? "20261104040000_Governance24AckVerification" : "20261104030000_Governance24LeaseIssuance");
            // Deliberately synthetic old-schema facts exercise migration and
            // status semantics; they do not assert a real signature or gateway.
            await setup.Database.OpenConnectionAsync();
            await setup.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try
            {
                setup.AddRange(org, project, env, node,
                    new EnvironmentSecurityInitialization { EnvironmentId = env.Id, ProjectId = project.Id, OrganizationId = org.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() },
                    new EnvironmentSecurityState { EnvironmentId = env.Id, ProjectId = project.Id, OrganizationId = org.Id, SecurityGeneration = generation, Epoch = 1,
                        HeadHash = hash, MinimumSnapshotSchema = "2.4", UpdatedAt = at },
                    new SecurityOperation { Id = operation, OrganizationId = org.Id, Kind = "EnvironmentPolicyCutover", RequestedBy = Guid.NewGuid(), Reason = "synthetic legacy", CommittedAt = at },
                    new SecurityEvent { EnvironmentId = env.Id, ProjectId = project.Id, OrganizationId = org.Id, SecurityGeneration = generation, Epoch = 1,
                        OperationId = operation, EventType = "EnvironmentPolicyChanged", PolicyJson = "{\"requireEnvironmentCredentials\":false,\"minimumSnapshotSchema\":\"2.4\"}", PreviousHash = hash, EventHash = hash, OccurredAt = at },
                    new SecurityOperationTarget { OperationId = operation, EnvironmentId = env.Id, NodeId = node.Id, InstanceGuid = instance, ChallengeNonce = challenge, CreatedAt = at, ProtocolSupportedAtFreeze = true },
                    new GatewaySecurityLease { NodeId = node.Id, InstanceId = instance, EnvironmentId = env.Id, ProjectId = project.Id, OrganizationId = org.Id,
                        SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, LeaseId = lease, IssuedAt = at, FreshUntil = expiry, LastIssuedLeaseUntil = expiry, SigningKeyId = "synthetic", LeaseSeconds = 30 },
                    new GatewaySecurityLeaseIssuance { LeaseId = lease, NodeId = node.Id, InstanceId = instance, EnvironmentId = env.Id, ProjectId = project.Id,
                        OrganizationId = org.Id, SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, PayloadHash = new string('c', 64), IssuedAt = at, FreshUntil = expiry, SigningKeyId = "synthetic" },
                    new GatewaySecurityCurrentReceipt { NodeId = node.Id, InstanceId = instance, EnvironmentId = env.Id, ProjectId = project.Id, OrganizationId = org.Id,
                        SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, HeadHash = hash, LeaseId = lease, FreshUntil = expiry, ReceivedAt = at },
                    new SecurityAckRecord { OperationId = operation, EnvironmentId = env.Id, NodeId = node.Id, InstanceGuid = instance, SecurityGeneration = generation,
                        Epoch = 1, CheckpointHash = hash, ChallengeNonce = challenge, AppliedAt = at, ReceivedAt = at, Success = true, Durable = true, AdmissionEnforced = true });
                await setup.SaveChangesAsync();
                var attemptHash = new string('d', 64);
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_ack_attempts(id,operation_id,environment_id,node_id,instance_guid,challenge_nonce,request_hash,error_code,received_at) VALUES({Guid.NewGuid()},{operation},{env.Id},{node.Id},{instance},{challenge},{attemptHash},'security_material_invalid',{at})");
                if (priorAmbiguousVerification)
                    await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_ack_verifications(operation_id,environment_id,node_id,instance_guid,challenge_nonce,security_generation,epoch,checkpoint_hash,lease_id,applied_at,received_at) VALUES({operation},{env.Id},{node.Id},{instance},{challenge},{generation},1,{hash},{lease},{at},{at})");
            }
            finally { await setup.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        }
        NpgsqlConnection.ClearAllPools();
        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            if (priorAmbiguousVerification) { Assert.NotNull(error); Assert.Equal("23514", error.SqlState); Assert.Contains("security_ack_verification_origin_unverified", error.MessageText); }
            else Assert.Null(error);
        }
        else
        {
            var result = await RunMigratorAsync(database);
            if (priorAmbiguousVerification) Assert.NotEqual(0, result.ExitCode); else Assert.Equal(0, result.ExitCode);
        }
        if (priorAmbiguousVerification)
        {
            await using var connection = await database.OpenAsync();
            await using var check = new NpgsqlCommand("SELECT (SELECT count(*) FROM security_ack_verifications)=1 AND (SELECT count(*) FROM security_acks)=1 AND (SELECT count(*) FROM security_ack_attempts)=1 AND NOT EXISTS(SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261104050000_Governance24AckRevalidation') AND to_regclass('public.security_ack_revalidation_challenges') IS NULL", connection);
            Assert.True((bool)(await check.ExecuteScalarAsync())!);
            return;
        }
        await using var verify = database.Context();
        Assert.Single(await verify.Set<SecurityAckRecord>().AsNoTracking().ToArrayAsync());
        Assert.Empty(await verify.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
        var historicalAttempt = await verify.Set<SecurityAckAttempt>().AsNoTracking().SingleAsync();
        Assert.Null(historicalAttempt.ReportedNonce);
        Assert.Null(historicalAttempt.ObservationSequence);
        Assert.Empty(await verify.Set<SecurityAckFailureLatest>().AsNoTracking().ToArrayAsync());
        Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(verify).ReadAsync(operation)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossScopeIssuanceBlocksUpgradeWithoutDeletingItsEvidence(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var org = new Organization { Code = "BAD-SCOPE-ORG", Name = "Organization" };
        var project = new Project { OrganizationId = org.Id, Code = "BAD-SCOPE-P", Name = "Project" };
        var first = new EnvironmentRecord { ProjectId = project.Id, Code = "FIRST", Name = "First" };
        var second = new EnvironmentRecord { ProjectId = project.Id, Code = "SECOND", Name = "Second" };
        var instance = Guid.NewGuid(); var generationA = Guid.NewGuid(); var generationB = Guid.NewGuid(); var badId = Guid.NewGuid();
        var node = new GatewayNode { EnvironmentId = first.Id, NodeName = "bad-scope", IdentityHash = new string('a', 64), InstanceId = instance.ToString() };
        var at = DateTimeOffset.UtcNow; at = at.AddTicks(-(at.Ticks % 10));
        await using (var setup = database.Context())
        {
            await setup.GetService<IMigrator>().MigrateAsync("20261104030000_Governance24LeaseIssuance");
            setup.AddRange(org, project, first, second, node,
                new EnvironmentSecurityInitialization { EnvironmentId = first.Id, ProjectId = project.Id, OrganizationId = org.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() },
                new EnvironmentSecurityInitialization { EnvironmentId = second.Id, ProjectId = project.Id, OrganizationId = org.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
            await setup.SaveChangesAsync();
            setup.AddRange(new EnvironmentSecurityState { EnvironmentId = first.Id, ProjectId = project.Id, OrganizationId = org.Id, SecurityGeneration = generationA,
                    InitialRequireEnvironmentCredentials = false, RequireEnvironmentCredentials = false, UpdatedAt = at },
                new EnvironmentSecurityState { EnvironmentId = second.Id, ProjectId = project.Id, OrganizationId = org.Id, SecurityGeneration = generationB,
                    InitialRequireEnvironmentCredentials = false, RequireEnvironmentCredentials = false, UpdatedAt = at });
            await setup.SaveChangesAsync();
            setup.Add(new GatewaySecurityLease { NodeId = node.Id, InstanceId = instance, EnvironmentId = first.Id, ProjectId = project.Id, OrganizationId = org.Id,
                SecurityGeneration = generationA, Epoch = 0, CheckpointHash = new string('b', 64), LeaseId = Guid.NewGuid(), IssuedAt = at,
                FreshUntil = at.AddSeconds(20), LastIssuedLeaseUntil = at.AddSeconds(20), SigningKeyId = "test-key", LeaseSeconds = 30 });
            await setup.SaveChangesAsync();
            // This row satisfies the old independent FKs but combines two
            // environments. Preserve it so the new composite FK must block.
            setup.Add(new GatewaySecurityLeaseIssuance { LeaseId = badId, NodeId = node.Id, InstanceId = instance, EnvironmentId = second.Id,
                ProjectId = project.Id, OrganizationId = org.Id, SecurityGeneration = generationB, Epoch = 0, CheckpointHash = new string('b', 64),
                PayloadHash = new string('c', 64), IssuedAt = at, FreshUntil = at.AddSeconds(20), SigningKeyId = "test-key" });
            await setup.SaveChangesAsync();
        }
        NpgsqlConnection.ClearAllPools();
        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            Assert.NotNull(error);
            Assert.Equal("23503", error.SqlState);
        }
        else Assert.NotEqual(0, (await RunMigratorAsync(database)).ExitCode);
        await using var connection = await database.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM gateway_security_lease_issuances WHERE lease_id=@id AND environment_id=@env) AND NOT EXISTS(SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261104040000_Governance24AckVerification') AND to_regclass('public.security_ack_verifications') IS NULL", connection);
        check.Parameters.AddWithValue("id", badId);
        check.Parameters.AddWithValue("env", second.Id);
        Assert.True((bool)(await check.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingCredentialBecomesLegacyGlobalWithoutChangingItsIdentityOrUsage(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        var application = Guid.NewGuid();
        var credential = Guid.NewGuid();
        var revokedCredential = Guid.NewGuid();
        var lastUsed = new DateTimeOffset(2026, 1, 12, 14, 30, 0, TimeSpan.Zero);
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'GOV-KEEP','Keep organization','Active',now(),now(),7)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO applications(id,organization_id,code,name,owner,status,created_at,updated_at,revision) VALUES({application},{organization},'GOV-APP','Keep application','fixture','Active',now(),now(),5)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO application_credentials(id,application_id,access_key,secret_hash,secret_last4,status,valid_from,expires_at,last_used_at,created_at,revision) VALUES({credential},{application},'fixture-access','fixture-digest','test','Active',now()-interval '1 day',now()+interval '30 days',{lastUsed},now(),9)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO application_credentials(id,application_id,access_key,secret_hash,secret_last4,status,valid_from,expires_at,last_used_at,revoked_at,created_at,revision) VALUES({revokedCredential},{application},'fixture-revoked','fixture-revoked-digest','test','Revoked',timestamptz '2026-01-01 00:00:00+00',timestamptz '2027-01-01 00:00:00+00',{lastUsed},{lastUsed},now(),11)");
        }

        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);

        await using var connection = await database.OpenAsync();
        await using (var schema = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='application_credentials' AND column_name='binding_mode')", connection))
            Assert.True((bool)(await schema.ExecuteScalarAsync())!, "The integrated governance migration must exist and run.");
        await using var verify = new NpgsqlCommand("SELECT organization_id=@org AND application_id=@app AND binding_mode='LegacyGlobal' AND project_id IS NULL AND environment_id IS NULL AND access_key='fixture-access' AND secret_hash='fixture-digest' AND secret_last4='test' AND status='Active' AND last_used_at=@used AND revision=9 FROM application_credentials WHERE id=@credential", connection);
        verify.Parameters.AddWithValue("org", organization);
        verify.Parameters.AddWithValue("app", application);
        verify.Parameters.AddWithValue("used", lastUsed);
        verify.Parameters.AddWithValue("credential", credential);
        Assert.True((bool)(await verify.ExecuteScalarAsync())!);
        await using var revoked = new NpgsqlCommand("SELECT c.status='Revoked' AND c.revoked_at=@used AND c.organization_id=@org AND c.revision=11 AND NOT EXISTS(SELECT 1 FROM security_operations s WHERE s.credential_id=c.id) AND NOT EXISTS(SELECT 1 FROM credential_revocations r WHERE r.credential_id=c.id) FROM application_credentials c WHERE c.id=@credential", connection);
        revoked.Parameters.AddWithValue("used", lastUsed);
        revoked.Parameters.AddWithValue("org", organization);
        revoked.Parameters.AddWithValue("credential", revokedCredential);
        Assert.True((bool)(await revoked.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLegacyCredentialScopeStopsBeforeAnyGovernanceDdl(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var credential = Guid.NewGuid();
        var missingApplication = Guid.NewGuid();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO application_credentials(id,application_id,access_key,secret_hash,secret_last4,status,valid_from,expires_at,created_at,revision) VALUES({credential},{missingApplication},'orphan-fixture','fixture-digest','test','Active',now()-interval '1 day',now()+interval '30 days',now(),1)");
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin");
        }

        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            Assert.NotNull(error);
            Assert.Contains("credential_governance_preflight_failed", error.MessageText);
        }
        else
        {
            var result = await RunMigratorAsync(database);
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(result.CredentialPreflightBlocked);
        }

        await using var connection = await database.OpenAsync();
        await using var verify = new NpgsqlCommand("SELECT NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='application_credentials' AND column_name='binding_mode') AND EXISTS(SELECT 1 FROM application_credentials WHERE id=@credential) AND (SELECT count(*) FROM \"__EFMigrationsHistory\")=21", connection);
        verify.Parameters.AddWithValue("credential", credential);
        Assert.True((bool)(await verify.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingEnvironmentIsPersistedAsUnknownUntilEvidenceIsConfirmed(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        var project = Guid.NewGuid();
        var environment = Guid.NewGuid();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at,revision) VALUES({organization},'TRUST-ORG','Trust organization','Active',now(),now(),1)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at,revision) VALUES({project},{organization},'TRUST-PROJ','Trust project','Active',now(),now(),1)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status,revision,base_path) VALUES({environment},{project},'TEST','Existing environment',false,0,'Active',7,'/keep')");
        }

        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);

        await using var connection = await database.OpenAsync();
        await using (var schema = new NpgsqlCommand("SELECT to_regclass('public.environment_security_initializations') IS NOT NULL", connection))
            Assert.True((bool)(await schema.ExecuteScalarAsync())!, "The environment initialization migration must exist and run.");
        await using (var ledgerSchema = new NpgsqlCommand("SELECT to_regclass('public.environment_security_states') IS NOT NULL", connection))
            Assert.True((bool)(await ledgerSchema.ExecuteScalarAsync())!, "The security ledger migration must exist and run.");
        await using (var receiptSchema = new NpgsqlCommand("SELECT to_regclass('public.environment_security_source_receipts') IS NOT NULL", connection))
            Assert.True((bool)(await receiptSchema.ExecuteScalarAsync())!, "The pre-migration source receipt must be persisted.");
        await using (var receipt = new NpgsqlCommand("SELECT r.source_database=current_database() AND r.source_database_oid=(SELECT oid::bigint FROM pg_database WHERE datname=current_database()) AND r.source_cluster_id=(SELECT system_identifier::text FROM pg_control_system()) AND r.source_migration='20261015020000_CredentialUsageTracking' AND r.source_schema_hash='b9676942e2e462605b1f00d56dd5e27dcf2910c8b9a78c812383a65079a31560' AND r.source_history_hash=(SELECT encode(sha256(convert_to(json_agg(json_build_array(\"MigrationId\",\"ProductVersion\") ORDER BY \"MigrationId\")::text,'UTF8')),'hex') FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"<=r.source_migration) AND r.project_id=@project AND r.organization_id=@org FROM environment_security_source_receipts r WHERE r.environment_id=@environment", connection))
        {
            receipt.Parameters.AddWithValue("environment", environment);
            receipt.Parameters.AddWithValue("project", project);
            receipt.Parameters.AddWithValue("org", organization);
            Assert.True((bool)(await receipt.ExecuteScalarAsync())!);
        }
        await using (var rewriteReceipt = new NpgsqlCommand("UPDATE environment_security_source_receipts SET source_schema_hash=@hash WHERE environment_id=@environment", connection))
        {
            rewriteReceipt.Parameters.AddWithValue("hash", new string('0', 64));
            rewriteReceipt.Parameters.AddWithValue("environment", environment);
            var error = await Assert.ThrowsAsync<PostgresException>(() => rewriteReceipt.ExecuteNonQueryAsync());
            Assert.Equal("23514", error.SqlState);
        }
        await using (var deleteReceipt = new NpgsqlCommand("DELETE FROM environment_security_source_receipts WHERE environment_id=@environment", connection))
        {
            deleteReceipt.Parameters.AddWithValue("environment", environment);
            var error = await Assert.ThrowsAsync<PostgresException>(() => deleteReceipt.ExecuteNonQueryAsync());
            Assert.Equal("23514", error.SqlState);
        }
        await using (var noAutomaticLedger = new NpgsqlCommand("SELECT NOT EXISTS(SELECT 1 FROM environment_security_states WHERE environment_id=@environment)", connection))
        {
            noAutomaticLedger.Parameters.AddWithValue("environment", environment);
            Assert.True((bool)(await noAutomaticLedger.ExecuteScalarAsync())!);
        }
        // Simulate an inconsistent database only inside the isolated fixture:
        // even a structurally coherent genesis cannot make Unknown trustworthy.
        await using (var corrupt = new NpgsqlCommand("SET session_replication_role=replica; INSERT INTO environment_security_states(environment_id,organization_id,project_id,security_generation,epoch,head_hash,initial_require_environment_credentials,require_environment_credentials,minimum_snapshot_schema,lease_seconds,lease_refresh_seconds,maximum_clock_skew_seconds,revision,updated_at) VALUES(@environment,@org,@project,@generation,0,@head,false,false,'2.0',30,5,2,1,now()); SET session_replication_role=origin", connection))
        {
            corrupt.Parameters.AddWithValue("environment", environment);
            corrupt.Parameters.AddWithValue("org", organization);
            corrupt.Parameters.AddWithValue("project", project);
            corrupt.Parameters.AddWithValue("generation", Guid.NewGuid());
            corrupt.Parameters.AddWithValue("head", WebApi.Infrastructure.CredentialSecurity.SecurityEventHash.Genesis);
            await corrupt.ExecuteNonQueryAsync();
        }
        await using (var readDb = database.Context())
        {
            var readError = await Assert.ThrowsAsync<InvalidOperationException>(() => new WebApi.Infrastructure.CredentialSecurity.SecurityLedgerReader(readDb).ReadCompleteAsync(environment));
            Assert.Contains("initialization is not verified", readError.Message);
        }
        await using (var removeCorrupt = new NpgsqlCommand("SET session_replication_role=replica; DELETE FROM environment_security_states WHERE environment_id=@environment; SET session_replication_role=origin", connection))
        {
            removeCorrupt.Parameters.AddWithValue("environment", environment);
            await removeCorrupt.ExecuteNonQueryAsync();
        }
        await using var verify = new NpgsqlCommand("SELECT i.state='Unknown' AND i.organization_id=@org AND i.project_id=@project AND i.evidence_hash IS NULL AND i.confirmed_by IS NULL AND i.confirmed_at IS NULL AND e.base_path='/keep' AND e.revision=7 FROM environment_security_initializations i JOIN environments e ON e.id=i.environment_id WHERE i.environment_id=@environment", connection);
        verify.Parameters.AddWithValue("org", organization);
        verify.Parameters.AddWithValue("project", project);
        verify.Parameters.AddWithValue("environment", environment);
        Assert.True((bool)(await verify.ExecuteScalarAsync())!);

        // Schema-only fixture: an arbitrary hash here is not a valid signed checkpoint.
        await using var minimum = new NpgsqlCommand("INSERT INTO environment_security_states(environment_id,organization_id,project_id,security_generation,epoch,head_hash,require_environment_credentials,minimum_snapshot_schema,lease_seconds,lease_refresh_seconds,maximum_clock_skew_seconds,revision,updated_at) VALUES(@environment,@org,@project,@generation,0,@head,false,'2.4',30,5,2,1,now())", connection);
        minimum.Parameters.AddWithValue("environment", environment);
        minimum.Parameters.AddWithValue("org", organization);
        minimum.Parameters.AddWithValue("project", project);
        minimum.Parameters.AddWithValue("generation", Guid.NewGuid());
        minimum.Parameters.AddWithValue("head", new string('0', 64));
        var unverified = await Assert.ThrowsAsync<PostgresException>(() => minimum.ExecuteNonQueryAsync());
        Assert.Contains("security_environment_initialization_unverified", unverified.MessageText);

        await using var change = new NpgsqlCommand("UPDATE environment_security_initializations SET state='FreshNonStrict',fresh_proof_id=@proof,revision=revision+1 WHERE environment_id=@environment", connection);
        change.Parameters.AddWithValue("proof", Guid.NewGuid());
        change.Parameters.AddWithValue("environment", environment);
        var changed = await Assert.ThrowsAsync<PostgresException>(() => change.ExecuteNonQueryAsync());
        Assert.Equal("23514", changed.SqlState);
        await using var delete = new NpgsqlCommand("DELETE FROM environment_security_initializations WHERE environment_id=@environment", connection);
        delete.Parameters.AddWithValue("environment", environment);
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal("23514", deleted.SqlState);
        Assert.True((bool)(await verify.ExecuteScalarAsync())!);

        // Unknown cannot be promoted straight to Governed by an incomplete path.
        await using var simulateGoverned = new NpgsqlCommand("UPDATE environment_security_initializations SET state='Governed',revision=revision+1 WHERE environment_id=@environment", connection);
        simulateGoverned.Parameters.AddWithValue("environment", environment);
        var unverifiedGovernance = await Assert.ThrowsAsync<PostgresException>(() => simulateGoverned.ExecuteNonQueryAsync());
        Assert.Contains("environment_initialization_regression", unverifiedGovernance.MessageText);

        // This direct SQL only prepares a structural fixture; the real application
        // must verify evidence and both permissions before confirming this state.
        var confirmer = Guid.NewGuid();
        await using (var userInsert = new NpgsqlCommand("INSERT INTO users(id,username,display_name,status,auth_source,created_at,updated_at) VALUES(@user,'trust-confirmer','Trust confirmer','Active','local',now(),now())", connection))
        {
            userInsert.Parameters.AddWithValue("user", confirmer);
            await userInsert.ExecuteNonQueryAsync();
        }
        await using (var simulateVerified = new NpgsqlCommand("UPDATE environment_security_initializations SET state='VerifiedLegacyNetwork',evidence_hash=@evidence,confirmed_by=@user,confirmed_at=now(),confirmation_reason='isolated fixture',revision=revision+1 WHERE environment_id=@environment", connection))
        {
            simulateVerified.Parameters.AddWithValue("evidence", new string('a', 64));
            simulateVerified.Parameters.AddWithValue("user", confirmer);
            simulateVerified.Parameters.AddWithValue("environment", environment);
            Assert.Equal(1, await simulateVerified.ExecuteNonQueryAsync());
        }
        var wrongGenesis = await Assert.ThrowsAsync<PostgresException>(() => minimum.ExecuteNonQueryAsync());
        Assert.Contains("security_genesis_hash_mismatch", wrongGenesis.MessageText);
        minimum.Parameters["head"].Value = WebApi.Infrastructure.CredentialSecurity.SecurityEventHash.Genesis;
        Assert.Equal(1, await minimum.ExecuteNonQueryAsync());
        Assert.Equal(1, await simulateGoverned.ExecuteNonQueryAsync());
        await using var downgrade = new NpgsqlCommand("UPDATE environment_security_initializations SET state='Unknown',revision=revision+1 WHERE environment_id=@environment", connection);
        downgrade.Parameters.AddWithValue("environment", environment);
        var downgraded = await Assert.ThrowsAsync<PostgresException>(() => downgrade.ExecuteNonQueryAsync());
        Assert.Equal("23514", downgraded.SqlState);
        await using var lowerMinimum = new NpgsqlCommand("UPDATE environment_security_states SET minimum_snapshot_schema='2.3',epoch=1,revision=revision+1 WHERE environment_id=@environment", connection);
        lowerMinimum.Parameters.AddWithValue("environment", environment);
        var lowered = await Assert.ThrowsAsync<PostgresException>(() => lowerMinimum.ExecuteNonQueryAsync());
        Assert.Contains("security_state_regression", lowered.MessageText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumingAnIntegratedPrefixDoesNotFabricateMainlineSourceEvidence(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        var project = Guid.NewGuid();
        var environment = Guid.NewGuid();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({organization},'RESUME-ORG','Resume organization','Active',now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES({project},{organization},'RESUME-PROJ','Resume project','Active',now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status) VALUES({environment},{project},'TEST','Resume environment',false,0,'Active')");
            // Simulate an interrupted upgrade with no retained source-session evidence.
            await db.GetService<IMigrator>().MigrateAsync("20261103183543_Governance24CredentialCore");
        }

        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);

        await using var connection = await database.OpenAsync();
        await using var verify = new NpgsqlCommand("SELECT (SELECT state FROM environment_security_initializations WHERE environment_id=@environment)='Unknown' AND NOT EXISTS(SELECT 1 FROM environment_security_source_receipts WHERE environment_id=@environment)", connection);
        verify.Parameters.AddWithValue("environment", environment);
        Assert.True((bool)(await verify.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSourceScopeBlocksReceiptInsteadOfInventingEvidence(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var originalOrganization = Guid.NewGuid();
        var changedOrganization = Guid.NewGuid();
        var project = Guid.NewGuid();
        var environment = Guid.NewGuid();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({originalOrganization},'SOURCE-A','Original','Active',now(),now()),({changedOrganization},'SOURCE-B','Changed','Active',now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES({project},{originalOrganization},'SOURCE-P','Source project','Active',now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status) VALUES({environment},{project},'TEST','Source environment',false,0,'Active')");
        }

        if (sqlEntry)
        {
            var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
            var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
            const string marker = "$WEBAPI_PREFLIGHT$;";
            Assert.Equal(1, script.Split(marker, StringSplitOptions.None).Length - 1);
            script = script.Replace(marker, marker + "\nUPDATE projects SET organization_id=@changed WHERE id=@project;\n", StringComparison.Ordinal);
            await using var connection = await database.OpenAsync();
            await using var command = new NpgsqlCommand(script, connection);
            command.Parameters.AddWithValue("changed", changedOrganization);
            command.Parameters.AddWithValue("project", project);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("environment_source_receipt_scope_changed", error.MessageText);
        }
        else
        {
            await using var db = database.Context();
            await db.Database.OpenConnectionAsync();
            await GovernanceMigrationPreflight.ValidateAsync(db);
            await GovernanceMigrationSourceCapture.CaptureAsync(db);
            await using (var other = await database.OpenAsync())
            await using (var change = new NpgsqlCommand("UPDATE projects SET organization_id=@changed WHERE id=@project", other))
            {
                change.Parameters.AddWithValue("changed", changedOrganization);
                change.Parameters.AddWithValue("project", project);
                await change.ExecuteNonQueryAsync();
            }
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.MigrateAsync());
            Assert.Contains("environment_source_receipt_scope_changed", error.ToString());
        }

        await using var verifyConnection = await database.OpenAsync();
        // The SQL injection is inside the installer's transaction and rolls
        // back; the EF-path change was committed by a separate connection.
        await using var verify = new NpgsqlCommand("SELECT (SELECT organization_id FROM projects WHERE id=@project)=@expected AND EXISTS(SELECT 1 FROM environments WHERE id=@environment) AND to_regclass('public.environment_security_source_receipts') IS NULL", verifyConnection);
        verify.Parameters.AddWithValue("project", project);
        verify.Parameters.AddWithValue("expected", sqlEntry ? originalOrganization : changedOrganization);
        verify.Parameters.AddWithValue("environment", environment);
        Assert.True((bool)(await verify.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingClusterIdentityPermissionDoesNotCreateSourceEvidence(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var seed = database.Context())
            await seed.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var roleName = "gov24_capture_" + Guid.NewGuid().ToString("N");
        using var builder = new NpgsqlCommandBuilder();
        var role = builder.QuoteIdentifier(roleName);
        await using (var admin = await database.OpenAsync())
        await using (var setup = new NpgsqlCommand("CREATE ROLE " + role + " NOLOGIN; GRANT pg_read_all_stats TO " + role + "; GRANT USAGE ON SCHEMA public TO " + role + "; GRANT SELECT, UPDATE ON ALL TABLES IN SCHEMA public TO " + role + "; REVOKE EXECUTE ON FUNCTION pg_catalog.pg_control_system() FROM PUBLIC", admin))
            await setup.ExecuteNonQueryAsync();
        NpgsqlConnection.ClearAllPools();
        try
        {
            await using var db = database.Context();
            await db.Database.OpenConnectionAsync();
            await using (var assume = db.Database.GetDbConnection().CreateCommand())
            {
                assume.CommandText = "SET ROLE " + role;
                await assume.ExecuteNonQueryAsync();
            }
            try
            {
                await using (var identity = db.Database.GetDbConnection().CreateCommand())
                {
                    identity.CommandText = "SELECT system_identifier FROM pg_control_system()";
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => identity.ExecuteScalarAsync());
                    Assert.Equal("42501", denied.SqlState);
                }
                if (sqlEntry)
                {
                    var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
                    var script = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
                    const string marker = "$WEBAPI_PREFLIGHT$;";
                    var end = script.IndexOf(marker, StringComparison.Ordinal);
                    Assert.True(end >= 0);
                    await using var preflight = db.Database.GetDbConnection().CreateCommand();
                    preflight.CommandText = script[..(end + marker.Length)];
                    await preflight.ExecuteNonQueryAsync();
                }
                else
                {
                    await GovernanceMigrationPreflight.ValidateAsync(db);
                    await GovernanceMigrationSourceCapture.CaptureAsync(db);
                }
                await using var proof = db.Database.GetDbConnection().CreateCommand();
                proof.CommandText = "SELECT COALESCE(current_setting('webapi.gov24_source',true),'')=''";
                Assert.True((bool)(await proof.ExecuteScalarAsync())!);
            }
            finally
            {
                if (sqlEntry)
                {
                    // The sliced installer explicitly starts a transaction.
                    await using var rollback = db.Database.GetDbConnection().CreateCommand();
                    rollback.CommandText = "ROLLBACK";
                    await rollback.ExecuteNonQueryAsync();
                }
                await using var reset = db.Database.GetDbConnection().CreateCommand();
                reset.CommandText = "RESET ROLE";
                await reset.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var admin = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand("DROP OWNED BY " + role + "; DROP ROLE " + role, admin);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Fact]
    public async Task ManagedRunnerWithoutClusterIdentityPermissionPreservesUnknownAndCompletes()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var roleName = "gov24_runner_" + Guid.NewGuid().ToString("N");
        using var builder = new NpgsqlCommandBuilder();
        var role = builder.QuoteIdentifier(roleName);
        var roleCreated = false;
        var organization = Guid.NewGuid();
        var project = Guid.NewGuid();
        var environment = Guid.NewGuid();
        try
        {
            await using (var admin = await database.OpenAsync())
            {
                await using (var create = new NpgsqlCommand("CREATE ROLE " + role + " NOLOGIN", admin))
                    await create.ExecuteNonQueryAsync();
                roleCreated = true;
                await using var prepare = new NpgsqlCommand("GRANT pg_read_all_stats TO " + role + "; ALTER SCHEMA public OWNER TO " + role + "; REVOKE EXECUTE ON FUNCTION pg_catalog.pg_control_system() FROM PUBLIC", admin);
                await prepare.ExecuteNonQueryAsync();
            }
            NpgsqlConnection.ClearAllPools();
            await using (var setup = database.Context())
            {
                await setup.Database.OpenConnectionAsync();
                await using (var assume = setup.Database.GetDbConnection().CreateCommand())
                {
                    assume.CommandText = "SET ROLE " + role;
                    await assume.ExecuteNonQueryAsync();
                }
                Assert.False(await setup.Database.SqlQueryRaw<bool>("SELECT pg_catalog.has_function_privilege(current_user,'pg_catalog.pg_control_system()','EXECUTE') AS \"Value\"").SingleAsync());
                await setup.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({organization},'KEEP','Keep','Active',now(),now())");
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES({project},{organization},'KEEP','Keep','Active',now(),now())");
                await setup.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status) VALUES({environment},{project},'TEST','Keep',false,0,'Active')");
            }
            NpgsqlConnection.ClearAllPools();
            await using (var migration = ManagedMigrationContext(database))
            {
                await migration.Database.OpenConnectionAsync();
                await using (var assume = migration.Database.GetDbConnection().CreateCommand())
                {
                    assume.CommandText = "SET ROLE " + role;
                    await assume.ExecuteNonQueryAsync();
                }
                await MigrationRunnerAssertions.RunAsync(migration, TestBudget);
                Assert.Empty(await migration.Database.GetPendingMigrationsAsync());
            }
            NpgsqlConnection.ClearAllPools();
            await using var check = database.Context();
            Assert.Equal("Unknown", await check.Database.SqlQuery<string>($"SELECT state AS \"Value\" FROM public.environment_security_initializations WHERE environment_id={environment}").SingleAsync());
            Assert.False(await check.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM public.environment_security_source_receipts WHERE environment_id={environment}) AS \"Value\"").SingleAsync());
            Assert.Equal("Unknown", await check.Database.SqlQueryRaw<string>("SELECT identity_state AS \"Value\" FROM public.migration_validation_receipts").SingleAsync());
            Assert.Equal("Keep", await check.Database.SqlQuery<string>($"SELECT name AS \"Value\" FROM public.organizations WHERE id={organization}").SingleAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                if (roleCreated)
                {
                    await using var admin = await database.OpenAsync();
                    await using var cleanup = new NpgsqlCommand("REASSIGN OWNED BY " + role + " TO webapi; DROP OWNED BY " + role + "; DROP ROLE " + role, admin);
                    await cleanup.ExecuteNonQueryAsync();
                }
            });
        }
    }

    [Fact]
    public async Task CancelledIdentityPermissionProbeRestoresCallerTransactionAndSavepoint()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var seed = database.Context())
            await seed.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var roleName = "gov24_cancel_" + Guid.NewGuid().ToString("N");
        using var builder = new NpgsqlCommandBuilder();
        var role = builder.QuoteIdentifier(roleName);
        await using (var admin = await database.OpenAsync())
        await using (var create = new NpgsqlCommand("CREATE ROLE " + role + " NOLOGIN; GRANT USAGE ON SCHEMA public TO " + role + "; GRANT SELECT ON ALL TABLES IN SCHEMA public TO " + role + "; REVOKE EXECUTE ON FUNCTION pg_catalog.pg_control_system() FROM PUBLIC", admin))
            await create.ExecuteNonQueryAsync();
        NpgsqlConnection.ClearAllPools();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString)
                .AddInterceptors(new CancelAfterIdentityPermissionError(cancellation));
            await using var db = new WebApiDbContext(options.Options);
            await db.Database.OpenConnectionAsync();
            await using (var assume = db.Database.GetDbConnection().CreateCommand())
            {
                assume.CommandText = "SET ROLE " + role;
                await assume.ExecuteNonQueryAsync();
            }
            await using var transaction = await db.Database.BeginTransactionAsync();
            await transaction.CreateSavepointAsync("caller_guard");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GovernanceMigrationSourceCapture.CaptureAsync(db, cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync());
            await transaction.RollbackToSavepointAsync("caller_guard");
            await transaction.RollbackAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var admin = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand("DROP OWNED BY " + role + "; DROP ROLE " + role, admin);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Fact]
    public async Task CancelledRunningIdentityProbeRestoresCallerTransactionAndSavepoint()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var seed = database.Context())
            await seed.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        NpgsqlConnection.ClearAllPools();
        var delay = new DelayClusterIdentityQuery();
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(delay);
        using var cancellation = new CancellationTokenSource();
        await using var db = new WebApiDbContext(options.Options);
        await db.Database.OpenConnectionAsync();
        var pid = await db.Database.SqlQueryRaw<int>("SELECT pg_catalog.pg_backend_pid() AS \"Value\"").SingleAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await transaction.CreateSavepointAsync("caller_guard");
        var capture = GovernanceMigrationSourceCapture.CaptureAsync(db, cancellation.Token);
        try
        {
            var first = await Task.WhenAny(delay.Reached, capture).WaitAsync(TimeSpan.FromSeconds(20));
            if (first == capture) await capture;
            await using var observer = await database.OpenAsync();
            await using var observe = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE pid=@pid AND state='active' AND wait_event='PgSleep')", observer);
            observe.Parameters.AddWithValue("pid", pid);
            var elapsed = Stopwatch.StartNew();
            var active = false;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(5) && !(active = (bool)(await observe.ExecuteScalarAsync())!))
                await Task.Delay(25);
            Assert.True(active, "The isolated identity query must reach PostgreSQL before cancellation.");
            cancellation.Cancel();
            var failure = await Record.ExceptionAsync(() => capture);
            Assert.True(failure is OperationCanceledException or PostgresException { SqlState: "57014" });
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync());
            await transaction.RollbackToSavepointAsync("caller_guard");
            await transaction.RollbackAsync();
        }
        finally
        {
            cancellation.Cancel();
            try { await capture.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
    }

    [Fact]
    public async Task CancellationDuringIdentitySavepointReleaseKeepsCallerTransactionUsable()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var seed = database.Context())
            await seed.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        NpgsqlConnection.ClearAllPools();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelDuringSavepointRelease(cancellation);
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptor);
        await using var db = new WebApiDbContext(options.Options);
        await db.Database.OpenConnectionAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await transaction.CreateSavepointAsync("caller_guard");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GovernanceMigrationSourceCapture.CaptureAsync(db, cancellation.Token));
        Assert.True(interceptor.Released);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync());
        await transaction.RollbackToSavepointAsync("caller_guard");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task IdentitySavepointRecoveryTimeoutReportsRollbackRequired()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var seed = database.Context())
            await seed.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var roleName = "gov24_recover_" + Guid.NewGuid().ToString("N");
        using var builder = new NpgsqlCommandBuilder();
        var role = builder.QuoteIdentifier(roleName);
        await using (var admin = await database.OpenAsync())
        await using (var create = new NpgsqlCommand("CREATE ROLE " + role + " NOLOGIN; GRANT USAGE ON SCHEMA public TO " + role + "; GRANT SELECT ON ALL TABLES IN SCHEMA public TO " + role + "; REVOKE EXECUTE ON FUNCTION pg_catalog.pg_control_system() FROM PUBLIC", admin))
            await create.ExecuteNonQueryAsync();
        NpgsqlConnection.ClearAllPools();
        try
        {
            var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(new StallSavepointRecovery());
            await using var db = new WebApiDbContext(options.Options);
            await db.Database.OpenConnectionAsync();
            await using (var assume = db.Database.GetDbConnection().CreateCommand())
            {
                assume.CommandText = "SET ROLE " + role;
                await assume.ExecuteNonQueryAsync();
            }
            await using var transaction = await db.Database.BeginTransactionAsync();
            var elapsed = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GovernanceMigrationSourceCapture.CaptureAsync(db));
            Assert.Contains("source_capture_savepoint_recovery_failed", error.Message);
            var causes = Assert.IsType<AggregateException>(error.InnerException).InnerExceptions;
            Assert.Contains(causes, item => item is PostgresException { SqlState: "42501" });
            Assert.Contains(causes, item => item is OperationCanceledException);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15));
            await transaction.RollbackAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var admin = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand("DROP OWNED BY " + role + "; DROP ROLE " + role, admin);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Fact]
    public async Task SourceCaptureRejectsUnregisteredExternalTransactionBeforeIdentityProbe()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var seed = database.Context())
            await seed.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
        var roleName = "gov24_external_" + Guid.NewGuid().ToString("N");
        using var builder = new NpgsqlCommandBuilder();
        var role = builder.QuoteIdentifier(roleName);
        await using (var admin = await database.OpenAsync())
        await using (var create = new NpgsqlCommand("CREATE ROLE " + role + " NOLOGIN; GRANT USAGE ON SCHEMA public TO " + role + "; GRANT SELECT ON ALL TABLES IN SCHEMA public TO " + role + "; REVOKE EXECUTE ON FUNCTION pg_catalog.pg_control_system() FROM PUBLIC", admin))
            await create.ExecuteNonQueryAsync();
        NpgsqlConnection.ClearAllPools();
        try
        {
            await using var db = database.Context();
            await db.Database.OpenConnectionAsync();
            await using (var assume = db.Database.GetDbConnection().CreateCommand())
            {
                assume.CommandText = "SET ROLE " + role;
                await assume.ExecuteNonQueryAsync();
            }
            await using var external = await db.Database.GetDbConnection().BeginTransactionAsync();
            Assert.Null(db.Database.CurrentTransaction);
            await using (var marker = db.Database.GetDbConnection().CreateCommand())
            {
                marker.CommandText = "CREATE TEMP TABLE caller_marker (id integer); INSERT INTO caller_marker VALUES (7)";
                await marker.ExecuteNonQueryAsync();
            }
            var rejection = await Record.ExceptionAsync(() => GovernanceMigrationSourceCapture.CaptureAsync(db));
            Assert.True(rejection is InvalidOperationException or NotSupportedException);
            Assert.Equal(7, await db.Database.SqlQueryRaw<int>("SELECT id AS \"Value\" FROM pg_temp.caller_marker").SingleAsync());
            await external.RollbackAsync();
            Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT pg_catalog.to_regclass('pg_temp.caller_marker') IS NULL AS \"Value\"").SingleAsync());
            await db.Database.CloseConnectionAsync();
            Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await IsolatedFixtureRetention.CleanupAsync(async () =>
            {
                await using var admin = await database.OpenAsync();
                await using var cleanup = new NpgsqlCommand("DROP OWNED BY " + role + "; DROP ROLE " + role, admin);
                await cleanup.ExecuteNonQueryAsync();
            });
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExistingRouteModesAndLegacyApiGrantsSurviveGovernanceMigration(bool sqlEntry, bool ambiguousBinding)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var organization = Guid.NewGuid();
        var project = Guid.NewGuid();
        var environment = Guid.NewGuid();
        var user = Guid.NewGuid();
        var api = Guid.NewGuid();
        var version = Guid.NewGuid();
        var cluster = Guid.NewGuid();
        var application = Guid.NewGuid();
        var grant = Guid.NewGuid();
        var anonymousPolicy = Guid.NewGuid();
        var jwtPolicy = Guid.NewGuid();
        var disabledPolicy = Guid.NewGuid();
        var routes = new Dictionary<string, Guid>
        {
            ["apikey"] = Guid.NewGuid(), ["anonymous"] = Guid.NewGuid(),
            ["jwt"] = Guid.NewGuid(), ["disabled"] = Guid.NewGuid()
        };
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var key = rsa.ExportParameters(false);
        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = System.Text.Json.JsonSerializer.Serialize(new
        {
            mode = "JWT", issuer = "https://issuer.example", audiences = new[] { "api" },
            allowedAlgorithms = new[] { "RS256" }, allowedTokenTypes = new[] { "JWT" },
            clockSkewSeconds = 30, maxTokenLifetimeSeconds = 3600, applicationClaim = "azp",
            applicationMappings = new[] { new { claimValue = "fixture", applicationId = application } },
            jwks = new { keys = new[] { new { kid = "fixture-rsa", kty = "RSA", alg = "RS256", use = "sig", n = Base64Url(key.Modulus!), e = Base64Url(key.Exponent!) } } },
            forwardBearer = false
        });
        jwt = WebApi.Domain.Policies.PolicyConfigurationValidator.Normalize("authentication", jwt);
        const string anonymous = "{\"mode\":\"Anonymous\"}";
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261015020000_CredentialUsageTracking");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({organization},'ROUTE-ORG','Route organization','Active',now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES({project},{organization},'ROUTE-PROJ','Route project','Active',now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status) VALUES({environment},{project},'TEST','Test',false,0,'Active')");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,status,auth_source,created_at,updated_at) VALUES({user},'route-fixture','Route fixture','Active','local',now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO apis(id,organization_id,project_id,code,name,lifecycle_status,owner_user_id,created_at,updated_at) VALUES({api},{organization},{project},'ROUTE-API','Route API','Draft',{user},now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO api_versions(id,api_id,version,status,change_type,created_by,created_at) VALUES({version},{api},'1.0.0','Draft','compatible',{user},now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO upstream_clusters(id,project_id,environment_id,name,load_balancing_policy,health_check_enabled,health_check_path,health_check_interval_sec,status) VALUES({cluster},{project},{environment},'fixture-cluster','RoundRobin',false,'/health',30,'Active')");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO applications(id,organization_id,project_id,code,name,owner,status,created_at,updated_at) VALUES({application},{organization},{project},'ROUTE-APP','Route app','fixture','Active',now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO application_api_permissions(id,application_id,api_id,environment_id,valid_from,created_by,created_at,revision) VALUES({grant},{application},{api},{environment},now()-interval '1 day',{user},now(),7)");
            foreach (var route in routes)
            {
                var path = "/" + route.Key;
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO api_routes(id,api_version_id,environment_id,route_name,path,normalized_path,methods,cluster_id,priority,enabled,timeout_ms,created_at,updated_at) VALUES({route.Value},{version},{environment},{route.Key},{path},{path},ARRAY['GET']::varchar[],{cluster},100,true,30000,now(),now())");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO route_methods(route_id,method,environment_id,normalized_path) VALUES({route.Value},'GET',{environment},{path})");
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO policies(id,organization_id,project_id,name,type,config,enabled,created_at,updated_at) VALUES({anonymousPolicy},{organization},{project},'anonymous','authentication',{anonymous}::jsonb,true,now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO policies(id,organization_id,project_id,name,type,config,enabled,created_at,updated_at) VALUES({jwtPolicy},{organization},{project},'jwt','authentication',{jwt}::jsonb,true,now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO policies(id,organization_id,project_id,name,type,config,enabled,created_at,updated_at) VALUES({disabledPolicy},{organization},{project},'disabled','authentication',{anonymous}::jsonb,false,now(),now())");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO route_policy_bindings(route_id,policy_id,priority) VALUES({routes["anonymous"]},{anonymousPolicy},10),({routes["jwt"]},{jwtPolicy},10),({routes["disabled"]},{disabledPolicy},10)");
            if (ambiguousBinding)
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO route_policy_bindings(route_id,policy_id,priority) VALUES({routes["anonymous"]},{jwtPolicy},20)");
        }

        if (ambiguousBinding)
        {
            if (sqlEntry)
            {
                var error = await RunInstallSqlAsync(database);
                Assert.NotNull(error);
                Assert.Contains("route_governance_preflight_failed", error.MessageText);
            }
            else
            {
                var result = await RunMigratorAsync(database);
                Assert.NotEqual(0, result.ExitCode);
                Assert.True(result.RoutePreflightBlocked);
            }
            await using var check = await database.OpenAsync();
            await using var unchanged = new NpgsqlCommand("SELECT NOT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='api_routes' AND column_name='authentication_mode') AND (SELECT count(*) FROM route_policy_bindings WHERE route_id=@route)=2 AND EXISTS(SELECT 1 FROM application_api_permissions WHERE id=@grant AND revision=7)", check);
            unchanged.Parameters.AddWithValue("route", routes["anonymous"]);
            unchanged.Parameters.AddWithValue("grant", grant);
            Assert.True((bool)(await unchanged.ExecuteScalarAsync())!);
            return;
        }
        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);

        await using var connection = await database.OpenAsync();
        await using (var schema = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='api_routes' AND column_name='authentication_mode')", connection))
            Assert.True((bool)(await schema.ExecuteScalarAsync())!, "The route governance migration must exist and run.");
        await using (var routeQuery = new NpgsqlCommand("SELECT route_name,authentication_mode,api_id,authorization_revision FROM api_routes WHERE environment_id=@env", connection))
        {
            routeQuery.Parameters.AddWithValue("env", environment);
            await using var reader = await routeQuery.ExecuteReaderAsync();
            var actual = new Dictionary<string, string>();
            while (await reader.ReadAsync())
            {
                actual.Add(reader.GetString(0), reader.GetString(1));
                Assert.Equal(api, reader.GetGuid(2));
                Assert.Equal(1, reader.GetInt64(3));
            }
            Assert.Equal(4, actual.Count);
            Assert.Equal("ApiKey", actual["apikey"]);
            Assert.Equal("Anonymous", actual["anonymous"]);
            Assert.Equal("JWT", actual["jwt"]);
            Assert.Equal("ApiKey", actual["disabled"]);
        }
        await using var grantQuery = new NpgsqlCommand("SELECT mode='LegacyApi' AND application_id=@app AND api_id=@api AND environment_id=@env AND revision=7 FROM application_api_permissions WHERE id=@grant", connection);
        grantQuery.Parameters.AddWithValue("app", application);
        grantQuery.Parameters.AddWithValue("api", api);
        grantQuery.Parameters.AddWithValue("env", environment);
        grantQuery.Parameters.AddWithValue("grant", grant);
        Assert.True((bool)(await grantQuery.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPermissionMovePreservesIdentityAndStrictRouteGrantsRemainImmutable(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        if (sqlEntry) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database)).ExitCode);
        var organization=Guid.NewGuid();var project=Guid.NewGuid();var otherProject=Guid.NewGuid();var firstEnvironment=Guid.NewGuid();var secondEnvironment=Guid.NewGuid();
        var user=Guid.NewGuid();var firstApi=Guid.NewGuid();var secondApi=Guid.NewGuid();var foreignApi=Guid.NewGuid();var application=Guid.NewGuid();var grant=Guid.NewGuid();
        await using var db=database.Context();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES({organization},'MOVE-ORG','Move organization','Active',now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES({project},{organization},'MOVE-P','Project','Active',now(),now()),({otherProject},{organization},'MOVE-Q','Other project','Active',now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status) VALUES({firstEnvironment},{project},'TEST','Test',false,0,'Active'),({secondEnvironment},{project},'UAT','UAT',false,1,'Active')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO users(id,username,display_name,status,auth_source,created_at,updated_at) VALUES({user},'move-fixture','Move fixture','Active','local',now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO apis(id,organization_id,project_id,code,name,lifecycle_status,owner_user_id,created_at,updated_at) VALUES({firstApi},{organization},{project},'MOVE-A','A','Draft',{user},now(),now()),({secondApi},{organization},{project},'MOVE-B','B','Draft',{user},now(),now()),({foreignApi},{organization},{otherProject},'MOVE-C','C','Draft',{user},now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO applications(id,organization_id,project_id,code,name,owner,status,created_at,updated_at) VALUES({application},{organization},{project},'MOVE-APP','App','fixture','Active',now(),now())");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO application_api_permissions(id,application_id,api_id,environment_id,organization_id,project_id,mode,valid_from,created_by,created_at,revision) VALUES({grant},{application},{firstApi},{firstEnvironment},{organization},{project},'LegacyApi',now()-interval '1 day',{user},now(),7)");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE application_api_permissions SET api_id={secondApi},environment_id={secondEnvironment} WHERE id={grant}");
        var changed=await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM application_api_permissions WHERE id={grant} AND api_id={secondApi} AND environment_id={secondEnvironment} AND organization_id={organization} AND project_id={project} AND revision=8) AS \"Value\"").SingleAsync();
        Assert.True(changed);
        var badScope=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlInterpolatedAsync($"UPDATE application_api_permissions SET api_id={foreignApi} WHERE id={grant}"));
        Assert.Equal("23514",badScope.SqlState);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE application_api_permissions SET mode='RouteMethod' WHERE id={grant}");
        var strictMove=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlInterpolatedAsync($"UPDATE application_api_permissions SET api_id={firstApi} WHERE id={grant}"));
        Assert.Equal("23514",strictMove.SqlState);
        Assert.Contains("permission_scope_immutable",strictMove.MessageText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GovernanceTableWithoutHistoryBlocksBeforeAnyWrite(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var connection = await database.OpenAsync())
        await using (var command = new NpgsqlCommand("CREATE TABLE environment_security_states(id uuid PRIMARY KEY); CREATE TABLE keep_marker(id integer PRIMARY KEY); INSERT INTO keep_marker VALUES (1)", connection))
            await command.ExecuteNonQueryAsync();

        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            Assert.NotNull(error);
            Assert.Contains("governance_migration_preflight_failed", error.MessageText);
        }
        else
        {
            var result = await RunMigratorAsync(database);
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(result.PreflightBlocked);
        }

        await using var verify = await database.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NULL AND (SELECT count(*) FROM keep_marker)=1 AND (SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname='public')=2", verify);
        Assert.True((bool)(await check.ExecuteScalarAsync())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GovernanceColumnOutsideMainHistoryBlocksBeforeFurtherMigrations(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261003182012_InitialCore");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE application_credentials ADD COLUMN binding_mode text");
        }
        var before = await PublicTableCountAsync(database);

        await AssertPreflightBlockedAsync(database, sqlEntry);

        await using var check = database.Context();
        Assert.Single(await check.Database.GetAppliedMigrationsAsync());
        Assert.Equal(before, await PublicTableCountAsync(database));
        Assert.True(await check.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='application_credentials' AND column_name='binding_mode') AS \"Value\"").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgedCompleteHistoryWithoutCoreSchemaBlocksBeforeAnyWrite(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        var known = db.Database.GetMigrations().ToArray();
        await using (var connection = await database.OpenAsync())
        {
            await using var create = new NpgsqlCommand("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" character varying(150) PRIMARY KEY, \"ProductVersion\" character varying(32) NOT NULL); CREATE TABLE keep_marker(id integer PRIMARY KEY); INSERT INTO keep_marker VALUES (1)", connection);
            await create.ExecuteNonQueryAsync();
            foreach (var migration in known)
            {
                await using var insert = new NpgsqlCommand("INSERT INTO \"__EFMigrationsHistory\" VALUES (@id,'10.0.12')", connection);
                insert.Parameters.AddWithValue("id", migration);
                await insert.ExecuteNonQueryAsync();
            }
        }

        await AssertPreflightBlockedAsync(database, sqlEntry);

        Assert.Equal(known.Length, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(2, await PublicTableCountAsync(database));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM keep_marker").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedMainColumnTypeBlocksBeforeFurtherMigrations(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261003182012_InitialCore");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE organizations ALTER COLUMN name TYPE character varying(127)");
        }

        await AssertPreflightBlockedAsync(database, sqlEntry);

        await using var check = database.Context();
        Assert.Single(await check.Database.GetAppliedMigrationsAsync());
        Assert.Equal(127, await check.Database.SqlQueryRaw<int>("SELECT character_maximum_length::int AS \"Value\" FROM information_schema.columns WHERE table_schema='public' AND table_name='organizations' AND column_name='name'").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedMainIndexBlocksBeforeFurtherMigrations(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261003182012_InitialCore");
            await db.Database.ExecuteSqlRawAsync("CREATE INDEX governance_unknown_idx ON organizations(name)");
        }

        await AssertPreflightBlockedAsync(database, sqlEntry);

        await using var check = database.Context();
        Assert.Single(await check.Database.GetAppliedMigrationsAsync());
        Assert.True(await check.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.governance_unknown_idx') IS NOT NULL AS \"Value\"").SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedImmutableTriggerFunctionBlocksBeforeFurtherMigrations(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var db = database.Context())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261008030000_ExternalNotifications");
            await db.Database.ExecuteSqlRawAsync("CREATE OR REPLACE FUNCTION forbid_notification_profile_mutation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$");
        }

        await AssertPreflightBlockedAsync(database, sqlEntry);

        await using var check = database.Context();
        Assert.Equal("20261008030000_ExternalNotifications", (await check.Database.GetAppliedMigrationsAsync()).Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedCredentialScopeTriggerBlocksBeforeAnyFurtherMigration(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        // The managed recheck below must replay against the same durable
        // journal as the original migration, or history is unverified and the
        // run stops at PendingReview before any fingerprint check.
        var evidence = NewMigratorEvidenceDirectory();
        Assert.Equal(0, (await RunMigratorAsync(database, evidence)).ExitCode);
        await using (var db = database.Context())
            await db.Database.ExecuteSqlRawAsync("CREATE OR REPLACE FUNCTION credential_governance_scope_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$");

        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            Assert.NotNull(error);
            Assert.Equal("P0001", error.SqlState);
            Assert.Contains("governance_migration_preflight_failed", error.MessageText);
        }
        else
        {
            var result = await RunMigratorAsync(database, evidence);
            Assert.NotEqual(0, result.ExitCode);
            // Function tampering is caught by the generic governance schema
            // fingerprint preflight; the SQL installer reports the same
            // governance_migration_preflight_failed category for it.
            Assert.True(result.PreflightBlocked);
        }

        await using var check = database.Context();
        Assert.Contains("20261103183543_Governance24CredentialCore", await check.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletedMainSchemaCanBeRecheckedByEitherEntry(bool firstSql, bool secondSql)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        // A managed recheck must use the same durable journal as its first
        // managed run. Direct SQL does not create that external evidence.
        var evidence = firstSql ? null : NewMigratorEvidenceDirectory();
        if (firstSql) Assert.Null(await RunInstallSqlAsync(database));
        else Assert.Equal(0, (await RunMigratorAsync(database, evidence)).ExitCode);

        if (secondSql) Assert.Null(await RunInstallSqlAsync(database));
        else
        {
            var recheck = await RunMigratorAsync(database, evidence);
            if (firstSql)
            {
                Assert.Equal(3, recheck.ExitCode);
                Assert.True(recheck.PendingReview);
                await using var receiptCheck = database.Context();
                Assert.Equal(1, await receiptCheck.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.migration_validation_receipts").SingleAsync());
            }
            else Assert.Equal(0, recheck.ExitCode);
        }

        await using var check = database.Context();
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownExistingTableWithoutHistoryBlocksBeforeAnyWrite(bool sqlEntry)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using (var connection = await database.OpenAsync())
        await using (var command = new NpgsqlCommand("CREATE TABLE keep_marker(id integer PRIMARY KEY); INSERT INTO keep_marker VALUES (1)", connection))
            await command.ExecuteNonQueryAsync();

        await AssertPreflightBlockedAsync(database, sqlEntry);

        await using var verify = await database.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NULL AND (SELECT count(*) FROM keep_marker)=1 AND (SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname='public')=1", verify);
        Assert.True((bool)(await check.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task MigratorPreflightRejectsNonPublicSearchPath()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA alternate; SET search_path TO alternate,public");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GovernanceMigrationPreflight.ValidateAsync(db));
        Assert.Contains("governance_migration_preflight_failed", error.Message);
    }

    [Fact]
    public async Task InstallSqlRejectsNonPublicSearchPathBeforeCreatingTables()
    {
        var database = new PostgresDatabase();
        await database.InitializeAsync();
        Console.WriteLine("isolated-test-db-retained=" + new NpgsqlConnectionStringBuilder(database.ConnectionString).Database);
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var sql = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        await using var connection = await database.OpenAsync();
        await using (var setup = new NpgsqlCommand("CREATE SCHEMA alternate; SET search_path TO alternate,public", connection)) await setup.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("P0001", error.SqlState);
        Assert.Contains("governance_migration_preflight_failed", error.MessageText);
        await using (var rollback = new NpgsqlCommand("ROLLBACK", connection)) await rollback.ExecuteNonQueryAsync();
        await using var check = new NpgsqlCommand("SELECT count(*)::int FROM pg_catalog.pg_tables WHERE schemaname IN ('public','alternate')", connection);
        Assert.Equal(0, (int)(await check.ExecuteScalarAsync())!);
    }

    private static async Task SeedUnknownHistoryAsync(PostgresDatabase database)
    {
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" character varying(150) PRIMARY KEY, "ProductVersion" character varying(32) NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES (@migration, '10.0.12');
            CREATE TABLE keep_marker (id integer PRIMARY KEY, note text NOT NULL);
            INSERT INTO keep_marker VALUES (1, 'preserve');
            """, connection);
        command.Parameters.AddWithValue("migration", DraftMigration);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertUnchangedAsync(PostgresDatabase database)
    {
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM "__EFMigrationsHistory") = 1
               AND (SELECT count(*) FROM "__EFMigrationsHistory" WHERE "MigrationId" = @migration) = 1
               AND (SELECT note FROM keep_marker WHERE id = 1) = 'preserve'
               AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = 'public' AND tablename NOT IN ('__EFMigrationsHistory', 'keep_marker'));
            """, connection);
        command.Parameters.AddWithValue("migration", DraftMigration);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    private static async Task<PostgresException?> RunInstallSqlAsync(PostgresDatabase database)
    {
        NpgsqlConnection.ClearAllPools();
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var sql = await File.ReadAllTextAsync(Path.Combine(source, "deploy", "initial-core.sql"));
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        try { await command.ExecuteNonQueryAsync(); return null; }
        catch (PostgresException error) { return error; }
    }

    private static async Task AssertPreflightBlockedAsync(PostgresDatabase database, bool sqlEntry)
    {
        if (sqlEntry)
        {
            var error = await RunInstallSqlAsync(database);
            Assert.NotNull(error);
            Assert.Equal("P0001", error.SqlState);
            Assert.Contains("governance_migration_preflight_failed", error.MessageText);
        }
        else
        {
            var result = await RunMigratorAsync(database);
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(result.PreflightBlocked);
        }
    }

    private static async Task<int> PublicTableCountAsync(PostgresDatabase database)
    {
        await using var connection = await database.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*)::int FROM pg_catalog.pg_tables WHERE schemaname='public'", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static string NewMigratorEvidenceDirectory()
    {
        var source = Environment.GetEnvironmentVariable("WEBAPI_SOURCE_ROOT") ?? throw new InvalidOperationException("Isolated source root is required.");
        var evidence = Path.Combine(source, ".superpowers", "sdd", "2026-11-04-migration-budget-trace", "journal-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Isolated migrator evidence requires Linux.");
        File.SetUnixFileMode(evidence, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Console.WriteLine("isolated-migrator-evidence-retained=" + evidence);
        return evidence;
    }

    private static async Task<(int ExitCode, bool PreflightBlocked, bool CredentialPreflightBlocked, bool RoutePreflightBlocked, bool WriterFenceBlocked, bool SameIdentitySessionBlocked, bool NotificationPreflightBlocked, bool PendingReview)> RunMigratorAsync(PostgresDatabase database, string? evidence = null)
    {
        NpgsqlConnection.ClearAllPools();
        var dll = Environment.GetEnvironmentVariable("WEBAPI_MIGRATOR_DLL");
        Assert.True(File.Exists(dll), "A freshly built isolated migrator is required.");
        var db = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll!);
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
        start.Environment["DOTNET_ROOT"] = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "/usr/share/dotnet";
        start.Environment["WEBAPI_DB_HOST"] = db.Host!;
        start.Environment["WEBAPI_DB_NAME"] = db.Database!;
        start.Environment["WEBAPI_DB_USER"] = "webapi";
        start.Environment["WEBAPI_DB_PASSWORD_FILE"] = "/run/secrets/postgres-password";
        start.Environment["WEBAPI_MIGRATION_TRANSACTION_SECONDS"] = "900";
        start.Environment["WEBAPI_MIGRATION_SETTINGS_MAX_BYTES"] = "65536";
        start.Environment["WEBAPI_MIGRATION_RULE_MAX_BYTES"] = "16384";
        start.Environment["WEBAPI_MIGRATION_RULE_MAX_COUNT"] = "100000";
        start.Environment["WEBAPI_MIGRATION_TOTAL_MAX_BYTES"] = "67108864";
        start.Environment["WEBAPI_MIGRATION_RECEIPT_MAX_ROWS"] = "100000";
        evidence ??= NewMigratorEvidenceDirectory();
        Assert.True(Directory.Exists(evidence), "The isolated evidence directory must exist.");
        Console.WriteLine("isolated-migrator-database=" + db.Database + "; evidence=" + evidence);
        start.Environment["WEBAPI_MIGRATION_EVIDENCE_DIR"] = evidence;
        start.Environment["WEBAPI_MIGRATION_EVIDENCE_RECORD_MAX_BYTES"] = "8192";
        start.Environment["WEBAPI_MIGRATION_EVIDENCE_QUOTA_BYTES"] = "16777216";
        start.Environment["WEBAPI_MIGRATION_EVIDENCE_MIN_FREE_BYTES"] = "1048576";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Isolated migrator could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await WaitForMigratorExitAsync(process, timeout.Token);
        await Task.WhenAll(stdout, stderr);
        var output = (await stdout) + (await stderr);
        bool HasReason(string reason) => output.Contains("reason=" + reason + ";", StringComparison.Ordinal);
        return (process.ExitCode, HasReason("GovernancePreflight") || HasReason("OldWriterUnfenced") || HasReason("SameIdentitySession"),
            HasReason("CredentialPreflight"), HasReason("RoutePreflight"), HasReason("OldWriterUnfenced"),
            HasReason("SameIdentitySession"), HasReason("InvalidNotification"), HasReason("PendingReview"));
    }

    private static async Task WaitForMigratorExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The owned process may have exited between checking and killing it.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}

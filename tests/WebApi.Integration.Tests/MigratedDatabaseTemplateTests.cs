using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;

public sealed class MigratedDatabaseTemplateTests
{
    [Fact] public async Task ExplicitRetentionKeepsTemplateAndCloneForDiagnosis()
    {
        await using var template = new MigratedDatabaseTemplate(retain: true); await template.InitializeAsync();
        await using var clone = await template.CreateDatabaseAsync();
        var cloneName = new NpgsqlConnectionStringBuilder(clone.ConnectionString).Database!;
        try
        {
            await clone.DisposeAsync(); await template.DisposeAsync();
            Assert.True(await Exists(clone.ConnectionString, template.DatabaseName));
            Assert.True(await Exists(clone.ConnectionString, cloneName));
        }
        finally
        {
            // These two identities belong only to this test, which deliberately exercised retain.
            await DropOwned(clone.ConnectionString, cloneName); await DropOwned(clone.ConnectionString, template.DatabaseName);
        }
    }

    [Theory] [InlineData("server")] [InlineData("seed")]
    public async Task FailedSetupKeepsOriginalErrorAndCleansOwnedFixture(string phase)
    {
        await using var template = new MigratedDatabaseTemplate(); await template.InitializeAsync();
        ApiFixture? observed = null;
        var cleanupVerified = false;
        var failure = new InvalidOperationException("synthetic setup failure");
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiFixture.CreateAsync(async f => {
                observed = f;
                await f.InitializeAsync(phase == "server" ? _ => throw failure : null);
                await f.SeedCatalogAsync();
                throw failure;
            }, template));
            Assert.Same(failure, error); Assert.NotNull(observed);
            var name = new NpgsqlConnectionStringBuilder(observed.Database.ConnectionString).Database!;
            Assert.Equal(observed.Database.RetainsDatabase, await Exists(observed.Database.ConnectionString, name));
            Assert.False(Directory.Exists(observed.LoginProtectionDirectory));
            cleanupVerified = true;
        }
        finally { if (observed is not null && !cleanupVerified) await observed.DisposeAsync(); }
    }

    [Fact] public async Task HttpFixturesUseIndependentUsersScopesAndRealLogin()
    {
        await using var template = new MigratedDatabaseTemplate(); await template.InitializeAsync();
        await using var first = new ApiFixture(template); await using var second = new ApiFixture(template);
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
        await first.SeedCatalogAsync();
        using var login = await first.LoginAsync(); login.EnsureSuccessStatusCode();
        using var rejected = await second.LoginAsync("incorrect-password");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
        await using var db = second.Context();
        Assert.Empty(await db.Set<Organization>().ToArrayAsync());
        Assert.Equal(second.User.Id, (await db.Set<UserRecord>().SingleAsync()).Id);
        Assert.NotEqual(first.User.Id, second.User.Id);
    }

    [Fact] public async Task CloneHasTheSameSchemaAndMigrationHistoryAsRealMigration()
    {
        await using var template = new MigratedDatabaseTemplate(); await template.InitializeAsync();
        await using var clone = await template.CreateDatabaseAsync();
        await using var original = new PostgresDatabase(); await original.InitializeAsync();
        await using var db = original.Context(); await db.Database.MigrateAsync();
        await using var cloned = clone.Context();
        Assert.Equal(await db.Database.GetAppliedMigrationsAsync(), await cloned.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await cloned.Database.GetPendingMigrationsAsync());
        Assert.Equal(await Schema(original), await Schema(clone));
        Assert.Equal(await Definitions(original), await Definitions(clone));
        Assert.Empty(await cloned.Set<UserRecord>().ToArrayAsync());
        Assert.Empty(await cloned.Set<Organization>().ToArrayAsync());
    }

    [Fact] public async Task DefaultDatabaseStillStartsEmpty()
    {
        await using var database = new PostgresDatabase(); await database.InitializeAsync();
        Assert.Equal("[]", await Schema(database));
    }

    [Fact] public async Task ConcurrentClonesKeepWritesAndDropsIndependent()
    {
        var migrations = 0;
        await using var template = new MigratedDatabaseTemplate(async db => { Interlocked.Increment(ref migrations); await db.Database.MigrateAsync(); });
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => template.InitializeAsync()));
        var owned = new System.Collections.Concurrent.ConcurrentBag<PostgresDatabase>();
        try
        {
            var clones = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ => { var clone = await template.CreateDatabaseAsync(); owned.Add(clone); return clone; }));
            Assert.Equal(6, clones.Select(c => new NpgsqlConnectionStringBuilder(c.ConnectionString).Database).Distinct().Count());
            await Task.WhenAll(clones.Select(async c => { await using var db = c.Context(); db.Add(new Organization { Code = "SAME", Name = "isolated" }); await db.SaveChangesAsync(); }));
            foreach (var clone in clones) { await using var db = clone.Context(); Assert.Single(await db.Set<Organization>().ToArrayAsync()); }
            await clones[0].DisposeAsync();
            await using var later = await template.CreateDatabaseAsync(); await using var laterDb = later.Context();
            Assert.Empty(await laterDb.Set<Organization>().ToArrayAsync());
            await using var sibling = clones[1].Context(); Assert.Single(await sibling.Set<Organization>().ToArrayAsync());
            Assert.Equal(1, migrations);
        }
        finally { await Task.WhenAll(owned.Select(clone => clone.DisposeAsync().AsTask())); }
        foreach (var clone in owned) Assert.Equal(clone.RetainsDatabase, await Exists(clone.ConnectionString, new NpgsqlConnectionStringBuilder(clone.ConnectionString).Database!));
    }

    [Fact] public async Task TemplateIsClosedToConnectionsAndRemovedAtDisposal()
    {
        var template = new MigratedDatabaseTemplate();
        await using var cleanup = template;
        await template.InitializeAsync();
        await using var clone = await template.CreateDatabaseAsync();
        var source = new NpgsqlConnectionStringBuilder(clone.ConnectionString) { Database = template.DatabaseName, Pooling = false };
        await using var connection = new NpgsqlConnection(source.ConnectionString);
        var error = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync());
        Assert.Equal("55000", error.SqlState);
        await template.DisposeAsync();
        Assert.Equal(template.RetainsDatabase, await Exists(clone.ConnectionString, template.DatabaseName));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => template.CreateDatabaseAsync());
        await using var context = clone.Context(); Assert.Empty(await context.Set<Organization>().ToArrayAsync());
    }

    [Fact] public async Task SeparateInstancesNeverReuseADatabaseEvenWithTheSameCompiledSchema()
    {
        await using var first = new MigratedDatabaseTemplate(); await using var second = new MigratedDatabaseTemplate();
        await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());
        Assert.NotEqual(first.DatabaseName, second.DatabaseName);
        Assert.Matches("^[0-9a-f]{64}$", first.SchemaFingerprint);
        Assert.Equal(first.SchemaFingerprint, second.SchemaFingerprint);
    }

    [Fact] public async Task FailedMigrationRemovesPartialTemplateAndCannotProduceAClone()
    {
        await using var template = new MigratedDatabaseTemplate(async db => {
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE partial_template(value integer)");
            await db.Database.ExecuteSqlRawAsync("SELECT * FROM deliberately_missing_table");
        });
        var error = await Assert.ThrowsAsync<PostgresException>(() => template.InitializeAsync());
        Assert.Equal(PostgresErrorCodes.UndefinedTable, error.SqlState);
        await using var observer = new PostgresDatabase();
        Assert.Equal(template.RetainsDatabase, await Exists(observer.ConnectionString, template.DatabaseName));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => template.CreateDatabaseAsync());
    }

    [Fact] public async Task DisposalRacingWithClonesDoesNotLeaveTheTemplateOrPublishAfterDisposal()
    {
        await using var template = new MigratedDatabaseTemplate(); await template.InitializeAsync();
        var owned = new System.Collections.Concurrent.ConcurrentBag<PostgresDatabase>();
        var tasks = Enumerable.Range(0, 6).Select(async _ => {
            try { var clone = await template.CreateDatabaseAsync(); owned.Add(clone); return clone; }
            catch (ObjectDisposedException) { return null; }
        }).ToArray();
        try
        {
            await template.DisposeAsync();
            var clones = await Task.WhenAll(tasks);
            foreach (var clone in clones) if (clone is not null) { await using var db = clone.Context(); Assert.Empty(await db.Set<Organization>().ToArrayAsync()); }
        }
        finally
        {
            try { await Task.WhenAll(tasks); }
            finally { await Task.WhenAll(owned.Select(clone => clone.DisposeAsync().AsTask())); }
        }
        await using var observer = new PostgresDatabase();
        Assert.Equal(template.RetainsDatabase, await Exists(observer.ConnectionString, template.DatabaseName));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => template.CreateDatabaseAsync());
    }

    private static async Task<bool> Exists(string connectionString, string name)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=@name)", connection);
        command.Parameters.AddWithValue("name", name); return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DropOwned(string connectionString, string name)
    {
        Assert.Matches("^test_[0-9a-f]{32}$", name);
        var settings = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> Schema(PostgresDatabase database)
    {
        await using var connection = await database.OpenAsync();
        const string sql = """
            SELECT coalesce(json_agg(row_to_json(t) ORDER BY t.table_name,t.ordinal_position)::text,'[]')
            FROM (SELECT table_name,column_name,ordinal_position,data_type,is_nullable,column_default
                  FROM information_schema.columns WHERE table_schema='public') t
            """;
        await using var command = new NpgsqlCommand(sql, connection); return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> Definitions(PostgresDatabase database)
    {
        await using var connection = await database.OpenAsync();
        const string sql = """
            SELECT json_agg(row_to_json(t) ORDER BY kind,object,name,definition)::text FROM (
              SELECT 'constraint' kind,conrelid::regclass::text object,conname::text name,pg_get_constraintdef(oid,true) definition
                FROM pg_constraint WHERE connamespace='public'::regnamespace
              UNION ALL SELECT 'index',tablename,indexname,indexdef FROM pg_indexes WHERE schemaname='public'
              UNION ALL SELECT 'trigger',tgrelid::regclass::text,tgname,pg_get_triggerdef(oid,true)
                FROM pg_trigger WHERE NOT tgisinternal AND tgrelid IN (SELECT oid FROM pg_class WHERE relnamespace='public'::regnamespace)
              UNION ALL SELECT 'function',oid::regprocedure::text,proname,pg_get_functiondef(oid)
                FROM pg_proc WHERE pronamespace='public'::regnamespace AND prokind='f'
            ) t
            """;
        await using var command = new NpgsqlCommand(sql, connection); return (string)(await command.ExecuteScalarAsync())!;
    }
}

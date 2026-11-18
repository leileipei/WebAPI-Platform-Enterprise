using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Persistence;
namespace WebApi.Integration.Tests.Support;

// Test-only, explicitly owned by a test class; never a persistent/global cache.
public sealed class MigratedDatabaseTemplate : IAsyncDisposable
{
    private readonly PostgresDatabase database;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<WebApiDbContext, Task> migrate;
    private bool ready, closed, created;
    public string DatabaseName => database.Name;
    internal bool RetainsDatabase => database.RetainsDatabase;
    public string SchemaFingerprint { get; private set; } = "";

    public MigratedDatabaseTemplate(bool retain = false) : this(db => db.Database.MigrateAsync(), retain) { }
    internal MigratedDatabaseTemplate(Func<WebApiDbContext, Task> migrate, bool retain = false)
    { this.migrate = migrate; database = new PostgresDatabase(retain); }

    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (ready) return;
            try
            {
                database.FreezeConnectionSettings();
                created = true;
                await database.InitializeAsync();
                // No pooled source connection may survive migration and block CREATE DATABASE TEMPLATE.
                var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = false };
                await using (var context = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(settings.ConnectionString).Options))
                {
                    await migrate(context);
                    var identity = string.Join("\n", new[] {
                        typeof(WebApiDbContext).Assembly.ManifestModule.ModuleVersionId.ToString(),
                        typeof(DbContext).Assembly.ManifestModule.ModuleVersionId.ToString(),
                        typeof(NpgsqlConnection).Assembly.ManifestModule.ModuleVersionId.ToString(),
                        context.Database.ProviderName ?? "", settings.Options ?? ""
                    }.Concat(context.Database.GetMigrations()));
                    SchemaFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                }
                await using var admin = new NpgsqlConnection(database.AdministrativeConnectionString); await admin.OpenAsync();
                await using var seal = new NpgsqlCommand($"ALTER DATABASE {database.Name} ALLOW_CONNECTIONS false", admin);
                await seal.ExecuteNonQueryAsync();
                ready = true;
            }
            catch (Exception failure)
            {
                closed = true;
                try { await database.DisposeAsync(); created = false; }
                catch (Exception cleanup) { throw new AggregateException("Template initialization and cleanup failed.", failure, cleanup); }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<PostgresDatabase> CreateDatabaseAsync()
    {
        await InitializeAsync();
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var clone = new PostgresDatabase(database.ConnectionString, database.RetainsDatabase);
            try { await clone.InitializeFromTemplateAsync(database.Name); return clone; }
            catch (Exception failure)
            {
                try { await clone.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException("Template clone and cleanup failed.", failure, cleanup); }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            closed = true;
            if (created) { await database.DisposeAsync(); created = false; }
        }
        finally { gate.Release(); }
    }
}

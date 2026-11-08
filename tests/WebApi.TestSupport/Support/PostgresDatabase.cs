using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Persistence;
namespace WebApi.Integration.Tests.Support;
public sealed class PostgresDatabase : IAsyncDisposable
{
    private readonly string name = "test_" + Guid.NewGuid().ToString("N");
    private readonly bool retain;
    public PostgresDatabase(bool retain = false) => this.retain = retain || Environment.GetEnvironmentVariable("WEBAPI_TEST_RETAIN_DATABASES") == "1";
    private string Connection(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder {
            Host = Environment.GetEnvironmentVariable("WEBAPI_TEST_DB_HOST") ?? "postgres",
            Username = "webapi", Database = database,
            Password = File.ReadAllText("/run/secrets/postgres-password").Trim()
        };
        // The opt-in is used only by the isolated legacy SQL regression.
        // Product connections and tests of missing SQL settings have no defaults.
        if (Environment.GetEnvironmentVariable("WEBAPI_TEST_SQL_MIGRATION_LIMITS") == "1")
            builder.Options = "-c webapi.migration_transaction_seconds=900 -c webapi.migration_receipt_max_rows=100000";
        return builder.ConnectionString;
    }
    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(Connection("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        if (retain) Console.WriteLine("isolated-test-db-retained=" + name);
    }
    public string ConnectionString => Connection(name);
    public WebApiDbContext Context() => new(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(Connection(name)).Options);
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(Connection(name)); await c.OpenAsync(); return c;
    }
    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (retain) return;
        await using var connection = new NpgsqlConnection(Connection("postgres")); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

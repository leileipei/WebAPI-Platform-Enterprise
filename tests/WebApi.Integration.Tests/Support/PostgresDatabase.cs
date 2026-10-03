using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Persistence;
namespace WebApi.Integration.Tests.Support;
public sealed class PostgresDatabase : IAsyncDisposable
{
    private readonly string name = "test_" + Guid.NewGuid().ToString("N");
    private string Connection(string database) => new NpgsqlConnectionStringBuilder {
        Host = Environment.GetEnvironmentVariable("WEBAPI_TEST_DB_HOST") ?? "postgres",
        Username = "webapi", Database = database,
        Password = File.ReadAllText("/run/secrets/postgres-password").Trim()
    }.ConnectionString;
    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(Connection("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
    }
    public WebApiDbContext Context() => new(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(Connection(name)).Options);
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(Connection(name)); await c.OpenAsync(); return c;
    }
    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(Connection("postgres")); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

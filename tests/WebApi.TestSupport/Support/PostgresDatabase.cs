using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Persistence;
namespace WebApi.Integration.Tests.Support;

internal enum ContractDatabaseGroup
{
    Shared,
    GovernanceSecurityAck,
    CredentialImpact,
    ImportSession
}

public sealed class PostgresDatabase : IAsyncDisposable
{
    private readonly string name = "test_" + Guid.NewGuid().ToString("N");
    private readonly bool retain;
    private readonly string? groupHost;
    private string? connectionSettings;
    public PostgresDatabase(bool retain = false) => this.retain = retain || Environment.GetEnvironmentVariable("WEBAPI_TEST_RETAIN_DATABASES") == "1";
    internal PostgresDatabase(ContractDatabaseGroup group) : this()
    {
        if (Environment.GetEnvironmentVariable("WEBAPI_CONTRACT_SPLIT_POSTGRES") != "1") return;
        groupHost = group switch
        {
            ContractDatabaseGroup.Shared => null,
            ContractDatabaseGroup.GovernanceSecurityAck => "postgres-security",
            ContractDatabaseGroup.CredentialImpact => "postgres-credential",
            ContractDatabaseGroup.ImportSession => "postgres-import",
            _ => throw new ArgumentOutOfRangeException(nameof(group))
        };
    }
    internal PostgresDatabase(string connectionSettings, bool retain) { this.connectionSettings = connectionSettings; this.retain = retain; }
    internal bool RetainsDatabase => retain;
    internal string Name => name;
    internal string AdministrativeConnectionString => Connection("postgres");
    internal void FreezeConnectionSettings() => connectionSettings ??= Connection(name);
    private string Connection(string database)
    {
        if (connectionSettings is not null)
            return new NpgsqlConnectionStringBuilder(connectionSettings) { Database = database }.ConnectionString;
        var builder = new NpgsqlConnectionStringBuilder {
            Host = groupHost ?? Environment.GetEnvironmentVariable("WEBAPI_TEST_DB_HOST") ?? "postgres",
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
    internal async Task InitializeFromTemplateAsync(string templateName)
    {
        // Both identifiers are owned random names, not user input.
        await using var connection = new NpgsqlConnection(Connection("postgres")); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE {templateName}", connection);
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

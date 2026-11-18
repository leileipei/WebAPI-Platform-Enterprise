using Npgsql;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ContractDatabaseGroupTests
{
    [Fact]
    public async Task ConfiguredDatabaseGroupsUseSeparatePostgresClusters()
    {
        var grouped = Environment.GetEnvironmentVariable("WEBAPI_CONTRACT_SPLIT_POSTGRES") == "1";
        var sharedHost = Environment.GetEnvironmentVariable("WEBAPI_TEST_DB_HOST") ?? "postgres";
        var groups = new Dictionary<ContractDatabaseGroup, string>
        {
            [ContractDatabaseGroup.Shared] = sharedHost,
            [ContractDatabaseGroup.GovernanceSecurityAck] = "postgres-security",
            [ContractDatabaseGroup.CredentialImpact] = "postgres-credential",
            [ContractDatabaseGroup.ImportSession] = "postgres-import"
        };
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (group, host) in groups)
        {
            var database = new PostgresDatabase(group);
            var settings = new NpgsqlConnectionStringBuilder(database.AdministrativeConnectionString) { Pooling = false };
            Assert.Equal(grouped ? host : sharedHost, settings.Host);
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync();
            await using var identity = new NpgsqlCommand("SELECT system_identifier::text FROM pg_catalog.pg_control_system()", connection);
            identities.Add(Assert.IsType<string>(await identity.ExecuteScalarAsync()));
        }
        Assert.Equal(grouped ? groups.Count : 1, identities.Count);
    }
}

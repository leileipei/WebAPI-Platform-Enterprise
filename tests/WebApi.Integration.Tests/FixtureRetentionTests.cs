using Npgsql;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

[Collection("MigrationIsolation")]
public sealed class FixtureRetentionTests
{
    [Fact]
    public async Task RetentionSkipsCleanupAfterARecordedFailureAndKeepsTheOwnedRole()
    {
        var database = new PostgresDatabase(retain: true);
        await database.InitializeAsync();
        var role = "gov24_retain_" + Guid.NewGuid().ToString("N");
        Console.WriteLine("isolated-retained-role=" + role);
        await using var admin = await database.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{role}\" NOLOGIN", admin)) await create.ExecuteNonQueryAsync();
        var cleanupCalled = false;
        try
        {
            throw new InvalidOperationException("isolated expected fixture failure");
        }
        catch (InvalidOperationException error)
        {
            Assert.Equal("isolated expected fixture failure", error.Message);
        }
        finally
        {
            await IsolatedFixtureRetention.CleanupAsync(() => { cleanupCalled = true; return Task.CompletedTask; }, retain: true);
        }
        Assert.False(cleanupCalled);
        await using var check = new NpgsqlCommand("SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname=@role AND NOT rolcanlogin", admin);
        check.Parameters.AddWithValue("role", role);
        Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);
    }
}

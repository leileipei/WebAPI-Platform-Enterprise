using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ContractManagementMigrationTests
{
    [Fact] public async Task MigrationAddsOnlyThreeTablesAndKeepsEveryExistingRowByteEquivalent()
    {
        await using var database = new PostgresDatabase(); await database.InitializeAsync();
        await using var db = database.Context();
        var previous = db.Database.GetMigrations().TakeWhile(x => !x.EndsWith("_ContractManagement", StringComparison.Ordinal)).Last();
        await db.GetService<IMigrator>().MigrateAsync(previous);
        db.Add(new UserRecord { Username = "retained", DisplayName = "保留用户", PasswordHash = "isolated-synthetic-hash", SecurityStamp = "retained-stamp" });
        db.Add(new Organization { Code = "RETAINED", Name = "保留组织" }); await db.SaveChangesAsync();
        var before = await Snapshot(database);
        await db.Database.MigrateAsync();
        var after = await Snapshot(database);
        Assert.Equal(3, after.Keys.Except(before.Keys).Count());
        Assert.Equal(["api_import_previews", "api_version_contract_sources", "project_import_source_policies"], after.Keys.Except(before.Keys).Order(StringComparer.Ordinal));
        foreach (var table in before) Assert.Equal(table.Value, after[table.Key]);
        // Empty new tables can be rolled back in this isolated migration fixture without touching old rows.
        await db.GetService<IMigrator>().MigrateAsync(previous);
        var rolledBack = await Snapshot(database);
        Assert.Equal(before.Count, rolledBack.Count);
        foreach (var table in before) Assert.Equal(table.Value, rolledBack[table.Key]);
    }
    private static async Task<Dictionary<string, string>> Snapshot(PostgresDatabase database)
    {
        await using var connection = await database.OpenAsync();
        var tables = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename <> '__EFMigrationsHistory' ORDER BY tablename", connection))
        await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        var result = new Dictionary<string, string>();
        foreach (var name in tables) {
            var escaped = name.Replace("\"", "\"\"", StringComparison.Ordinal);
            await using var command = new NpgsqlCommand($"SELECT to_jsonb(t)::text FROM \"{escaped}\" t ORDER BY to_jsonb(t)::text", connection);
            await using var reader = await command.ExecuteReaderAsync(); var rows = new List<string>(); while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            result[name] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
        }
        return result;
    }
}

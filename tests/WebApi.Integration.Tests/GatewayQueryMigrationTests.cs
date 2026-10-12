using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class GatewayQueryMigrationTests
{
    // The combined model includes delivery columns; the index-only upgrade starts
    // at the latest delivery migration immediately preceding the four indexes.
    private const string BaselineMigration="20261010010000_PipelineProjectionCursor";
    private const string IndexMigration="20261010091247_GatewayQueryIndexes";
    private static readonly string[] ApprovedIndexes=["ix_api_routes_environment_order_path_id","ix_alert_events_organization_project_started_id","ix_alert_rules_organization_name_id","ix_policies_organization_name_id"];
    // These fail if an index migration adds business DDL, loses records/order,
    // omits an approved index or checks expression columns without its definition.
    [Fact]
    public async Task MigrationAddsOnlyApprovedIndexesAndPreservesData()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync(BaselineMigration);await SeedAsync(db);
        var before=await MigrationProbe.InspectAsync(db);var order=await RouteOrderAsync(db);
        // Verify this historical index migration explicitly; later business DDL must
        // not be silently admitted through the index-only diagnostic gate.
        await db.GetService<IMigrator>().MigrateAsync(IndexMigration);var after=await MigrationProbe.InspectAsync(db);
        Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);
        Assert.Equal(ApprovedIndexes.Order(),after.Indexes.Where(i=>!before.Indexes.Any(b=>b.Name==i.Name&&b.Table==i.Table)).Select(i=>i.Name).Order());
        Assert.All(before.Indexes,i=>Assert.Contains(after.Indexes,a=>a.Name==i.Name&&a.Table==i.Table&&a.DefinitionHash==i.DefinitionHash&&a.Valid&&a.Ready));
        Assert.Equal(order,await RouteOrderAsync(db));Assert.Equal(before.Tables.Select(t=>(t.Name,t.Columns.Count,t.Rows,t.DataHash)),after.Tables.Select(t=>(t.Name,t.Columns.Count,t.Rows,t.DataHash)));
    }
    [Fact]
    public async Task UpgradeFailureRollsBackNewIndexesAndKeepsRecords()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync(BaselineMigration);await SeedAsync(db);var before=await MigrationProbe.InspectAsync(db);
        var assembly=db.GetService<IMigrationsAssembly>();var migration=assembly.CreateMigration(assembly.Migrations[IndexMigration],db.Database.ProviderName!);
        var fault=await MigrationProbe.FailAfterFirstIndexAsync(db,migration.UpOperations);
        Assert.False(fault.NoMigrationNeeded);Assert.True(fault.FailureInjected);Assert.Equal("42601",fault.SqlState);Assert.True(fault.Complete);
        Assert.True(fault.DataUnchanged);Assert.True(fault.IndexesUnchanged);var after=await MigrationProbe.InspectAsync(db);
        Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);Assert.Equal(before.AppliedMigrations,after.AppliedMigrations);
        Assert.Equal(CanonicalJson.Serialize(before.Indexes.Select(i=>i with {Bytes=0}).ToArray()),CanonicalJson.Serialize(after.Indexes.Select(i=>i with {Bytes=0}).ToArray()));
    }
    [Fact]
    public async Task EmptyDatabaseMatchesEfModelAndApprovedExpressionIndex()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        var installed=await MigrationProbe.ApplyAsync(db,false);Assert.True(installed.Complete);Assert.True(installed.After.ModelMatched);
        Assert.False(installed.After.PendingModelChanges);Assert.Empty(installed.After.PendingMigrations);
        Assert.All(ApprovedIndexes,name=>Assert.Contains(installed.After.Indexes,i=>i.Name==name&&i.Valid&&i.Ready));
        var expression=Assert.Single(installed.After.Indexes,i=>i.Name=="ix_api_routes_environment_order_path_id");
        Assert.Equal(new string?[]{"environment_id",null,"path","id"},expression.Columns);
        Assert.Equal("a552f9d4edb81648f93a204b59d85e4e1db041439f35a7d4d9885949e18b89cd",expression.DefinitionHash);
        // Same name/key slots with the wrong expression must fail the model gate.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX ix_api_routes_environment_order_path_id; CREATE INDEX ix_api_routes_environment_order_path_id ON api_routes (environment_id, (priority * 2000000), path, id)");
        Assert.False((await MigrationProbe.InspectAsync(db)).ModelMatched);
    }
    [Fact]
    public async Task DownRemovesOnlyNewIndexesAndPreservesMixedRouteRecords()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync(BaselineMigration);await SeedAsync(db);var before=await MigrationProbe.InspectAsync(db);
        await db.GetService<IMigrator>().MigrateAsync(IndexMigration);var indexed=await MigrationProbe.InspectAsync(db);Assert.All(ApprovedIndexes,name=>Assert.Contains(indexed.Indexes,i=>i.Name==name));
        await db.GetService<IMigrator>().MigrateAsync(BaselineMigration);var after=await MigrationProbe.InspectAsync(db);
        Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);Assert.Equal(before.AppliedMigrations,after.AppliedMigrations);
        Assert.Equal(CanonicalJson.Serialize(before.Indexes.Select(i=>i with {Bytes=0}).ToArray()),CanonicalJson.Serialize(after.Indexes.Select(i=>i with {Bytes=0}).ToArray()));
    }
    [Fact]
    public async Task GeneratedSqlUpgradesExistingDataTwiceAndMatchesActualModel()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync(BaselineMigration);await SeedAsync(db);
        var before=await MigrationProbe.InspectAsync(db);var order=await RouteOrderAsync(db);
        var script=await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"initial-core.sql"));
        await using var connection=await database.OpenAsync();
        for(var attempt=0;attempt<2;attempt++){
            await using var command=new NpgsqlCommand(script,connection);await command.ExecuteNonQueryAsync();
            var after=await MigrationProbe.InspectAsync(db);
            Assert.True(after.ModelMatched);Assert.False(after.PendingModelChanges);Assert.Empty(after.PendingMigrations);
            Assert.Equal(before.Tables.Select(t=>(t.Name,t.Columns.Count,t.Rows,t.DataHash)),after.Tables.Where(t=>before.Tables.Any(b=>b.Name==t.Name)).Select(t=>(t.Name,t.Columns.Count,t.Rows,t.DataHash)));Assert.Equal(order,await RouteOrderAsync(db));
            Assert.All(before.Indexes,i=>Assert.Contains(after.Indexes,a=>a.Name==i.Name&&a.Table==i.Table&&a.DefinitionHash==i.DefinitionHash&&a.Valid&&a.Ready));
            Assert.Equal(ApprovedIndexes.Order(),after.Indexes.Where(i=>i.Table!="release_frozen_contracts"&&!before.Indexes.Any(b=>b.Name==i.Name&&b.Table==i.Table)).Select(i=>i.Name).Order());
            Assert.Contains(after.Indexes,i=>i.Name=="ix_frozen_contract_lookup"&&i.Valid&&i.Ready);
        }
    }
    [Fact] public async Task IndexOnlyGateRejectsPendingFrozenContractBusinessMigration()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.GetService<IMigrator>().MigrateAsync(IndexMigration);await SeedAsync(db);
        var before=await MigrationProbe.InspectAsync(db);await Assert.ThrowsAsync<InvalidDataException>(()=>MigrationProbe.ApplyAsync(db,true));var after=await MigrationProbe.InspectAsync(db);
        Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);Assert.Equal(before.AppliedMigrations,after.AppliedMigrations);
    }
    private static Task<Guid[]> RouteOrderAsync(WebApiDbContext db)=>db.Set<ApiRoute>().AsNoTracking().OrderBy(r=>(r.NormalizedPath.Contains("{*}")?2:r.NormalizedPath.Contains("{}")?1:0)*2000000-r.Priority).ThenBy(r=>r.Path).ThenBy(r=>r.Id).Select(r=>r.Id).ToArrayAsync();
    private static async Task SeedAsync(WebApiDbContext db)
    {
        if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Migration fixtures require Unix private-file modes.");
        var directory=Path.Combine(Path.GetTempPath(),"gateway-index-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try{
            var now=DateTimeOffset.UtcNow;await PerformanceDataset.CreateAsync(db,new DatasetSpec(100,20261009,now.AddMinutes(-1),now.AddHours(20)),directory,CancellationToken.None);
            var routes=await db.Set<ApiRoute>().ToArrayAsync();for(var i=0;i<routes.Length;i++){routes[i].NormalizedPath=(i%3) switch {0=>"/exact/"+i,1=>"/parameter/{}/"+i,_=>"/catchall/{*}/"+i};routes[i].Priority=i%7;}
            await db.SaveChangesAsync();
        }finally{Directory.Delete(directory,true);}
    }
}

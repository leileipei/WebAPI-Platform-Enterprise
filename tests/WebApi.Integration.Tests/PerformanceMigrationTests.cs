using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PerformanceMigrationTests
{
    private static async Task<object> CompatibilityCall(string method,params object[] arguments)
    {
        var entry=typeof(MigrationProbe).GetMethod(method);
        Assert.NotNull(entry);
        var task=Assert.IsAssignableFrom<Task>(entry.Invoke(null,arguments));await task;
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
    [Fact]
    public async Task BusinessCompatibilityPreservesOriginalColumnsWhileAcceptingNewSchema()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync("20261008030000_ExternalNotifications");
        const string canary="compatibility-private-record";db.Add(new Organization{Code="compatibility",Name=canary});await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var original=await CompatibilityCall("CaptureLegacyAsync",db,CancellationToken.None);
        var json=JsonSerializer.Serialize(original,CanonicalJson.Options);Assert.DoesNotContain(canary,json);
        var upgraded=await CompatibilityCall("ApplyCompatibleAsync",db,original,CancellationToken.None);
        using var proof=JsonDocument.Parse(JsonSerializer.Serialize(upgraded,CanonicalJson.Options));var root=proof.RootElement;
        Assert.True(root.GetProperty("complete").GetBoolean());
        Assert.Equal(root.GetProperty("legacyBefore").GetProperty("fingerprint").GetString(),root.GetProperty("legacyAfter").GetProperty("fingerprint").GetString());
        Assert.NotEqual(root.GetProperty("before").GetProperty("businessFingerprint").GetString(),root.GetProperty("after").GetProperty("businessFingerprint").GetString());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(canary,(await db.Set<Organization>().AsNoTracking().SingleAsync(o=>o.Code=="compatibility")).Name);
        var repeat=await CompatibilityCall("ApplyCompatibleAsync",db,original,CancellationToken.None);
        using var second=JsonDocument.Parse(JsonSerializer.Serialize(repeat,CanonicalJson.Options));Assert.True(second.RootElement.GetProperty("complete").GetBoolean());
    }
    [Theory]
    [InlineData("data")]
    [InlineData("extra-column")]
    [InlineData("type")]
    [InlineData("missing-column")]
    [InlineData("index")]
    [InlineData("primary")]
    [InlineData("history")]
    public async Task BusinessCompatibilityRejectsChangedBaselineBeforeApplyingMigration(string fault)
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync("20261008030000_ExternalNotifications");
        db.Add(new Organization{Code="compatibility",Name="unchanged"});await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var original=await CompatibilityCall("CaptureLegacyAsync",db,CancellationToken.None);
        var sql=fault switch{
            "data"=>"UPDATE organizations SET name='changed' WHERE code='compatibility'",
            "extra-column"=>"ALTER TABLE organizations ADD COLUMN compatibility_unknown_column text",
            "type"=>"ALTER TABLE organizations ALTER COLUMN name TYPE text",
            "missing-column"=>"ALTER TABLE organizations DROP COLUMN name",
            "primary"=>"ALTER TABLE organizations DROP CONSTRAINT \"PK_organizations\" CASCADE; ALTER TABLE organizations ADD PRIMARY KEY(code)",
            "history"=>"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"=(SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" LIMIT 1)",
            _=>"DROP INDEX \"IX_organizations_code\""
        };await db.Database.ExecuteSqlRawAsync(sql);
        var history=(await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(()=>CompatibilityCall("ApplyCompatibleAsync",db,original,CancellationToken.None));
        Assert.Equal(history,await db.Database.GetAppliedMigrationsAsync());
    }
    [Theory]
    [InlineData("new-column-type")]
    [InlineData("new-column-nullability")]
    [InlineData("new-table-type")]
    [InlineData("new-table-nullability")]
    public async Task RepeatedCompatibilityRejectsTargetSchemaDriftOutsideLegacyProjection(string fault)
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync("20261008030000_ExternalNotifications");
        var original=await CompatibilityCall("CaptureLegacyAsync",db,CancellationToken.None);
        await CompatibilityCall("ApplyCompatibleAsync",db,original,CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync(fault switch{
            "new-column-type"=>"ALTER TABLE release_verifications ALTER COLUMN profile_hash TYPE text",
            "new-column-nullability"=>"ALTER TABLE release_verifications ALTER COLUMN profile_hash SET NOT NULL",
            "new-table-type"=>"ALTER TABLE release_pipelines ALTER COLUMN name TYPE text",
            _=>"ALTER TABLE release_pipelines ALTER COLUMN name DROP NOT NULL"
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>CompatibilityCall("ApplyCompatibleAsync",db,original,CancellationToken.None));
    }
    [Fact]
    public async Task CurrentContractChecksFullTargetSchemaWithoutMigration()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        const string canary="current-contract-private-canary";db.Add(new Organization{Code="current-contract",Name=canary});await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var before=await MigrationProbe.InspectAsync(db);
        var contract=Assert.IsType<LegacyContract>(await CompatibilityCall("CaptureCurrentContractAsync",db,CancellationToken.None));
        var after=await MigrationProbe.InspectAsync(db);
        Assert.Equal(before.AppliedMigrations,contract.AppliedMigrations);Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);
        Assert.Equal(before.Tables.Select(t=>t.Name).Order(),contract.Tables.Select(t=>t.Name).Order());
        Assert.All(contract.Tables,t=>{Assert.Equal(t.Columns.Count,t.ColumnTypes.Count);Assert.Equal(t.Columns.Count,t.NotNull.Count);Assert.NotEmpty(t.Primary);});
        Assert.DoesNotContain(canary,JsonSerializer.Serialize(contract,CanonicalJson.Options));
        Assert.Equal(canary,(await db.Set<Organization>().SingleAsync()).Name);
    }
    [Theory]
    [InlineData("character",true)]
    [InlineData("char-alias",true)]
    [InlineData("shorter",false)]
    [InlineData("varying",false)]
    [InlineData("text",false)]
    public async Task CurrentContractPreservesFixedCharacterLengthAndType(string shape,bool accepted)
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(shape switch{
            "character"=>"ALTER TABLE credential_usage_batches ALTER COLUMN core_hash TYPE character(64)",
            "char-alias"=>"ALTER TABLE credential_usage_batches ALTER COLUMN core_hash TYPE char(64)",
            "shorter"=>"ALTER TABLE credential_usage_batches ALTER COLUMN core_hash TYPE character(63)",
            "varying"=>"ALTER TABLE credential_usage_batches ALTER COLUMN core_hash TYPE character varying(64)",
            _=>"ALTER TABLE credential_usage_batches ALTER COLUMN core_hash TYPE text"
        });
        if(accepted){
            var contract=await MigrationProbe.CaptureCurrentContractAsync(db);
            var table=Assert.Single(contract.Tables,t=>t.Name=="credential_usage_batches");
            Assert.Equal("character(64)",table.ColumnTypes[table.Columns.ToList().IndexOf("core_hash")]);
        }else await Assert.ThrowsAsync<InvalidDataException>(()=>MigrationProbe.CaptureCurrentContractAsync(db));
    }
    [Theory]
    [InlineData("varchar")]
    [InlineData("not-null")]
    [InlineData("new-table-field")]
    [InlineData("primary")]
    public async Task CurrentContractRejectsTargetDrift(string fault)
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        await CompatibilityCall("CaptureCurrentContractAsync",db,CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync(fault switch{
            "varchar"=>"ALTER TABLE organizations ALTER COLUMN name TYPE text",
            "not-null"=>"ALTER TABLE organizations ALTER COLUMN name DROP NOT NULL",
            "new-table-field"=>"ALTER TABLE release_pipelines ADD COLUMN unapproved_field text",
            _=>"ALTER TABLE organizations DROP CONSTRAINT \"PK_organizations\" CASCADE; ALTER TABLE organizations ADD PRIMARY KEY(code)"
        });
        var history=(await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(()=>CompatibilityCall("CaptureCurrentContractAsync",db,CancellationToken.None));
        Assert.Equal(history,await db.Database.GetAppliedMigrationsAsync());
    }
    [Fact]
    public async Task CurrentContractAllowsApprovedIndexAbsence()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        var before=Assert.IsType<LegacyContract>(await CompatibilityCall("CaptureCurrentContractAsync",db,CancellationToken.None));
        await db.Database.ExecuteSqlRawAsync("DROP INDEX ix_api_routes_environment_order_path_id; DROP INDEX ix_policies_organization_name_id; DROP INDEX ix_alert_rules_organization_name_id; DROP INDEX ix_alert_events_organization_project_started_id");
        var after=Assert.IsType<LegacyContract>(await CompatibilityCall("CaptureCurrentContractAsync",db,CancellationToken.None));
        var inspection=await MigrationProbe.InspectAsync(db);
        Assert.False(inspection.ModelMatched);Assert.Equal(before.Fingerprint,after.Fingerprint);Assert.Equal(before.SchemaHash,after.SchemaHash);
        Assert.Equal(before.AppliedMigrations,after.AppliedMigrations);Assert.Equal(before.Indexes.Count-4,after.Indexes.Count);
    }
    private const string ApprovedRouteIndexSql="CREATE INDEX ix_api_routes_environment_order_path_id ON api_routes (environment_id, ((CASE WHEN normalized_path LIKE '%{*}%' THEN 2 WHEN normalized_path LIKE '%{}%' THEN 1 ELSE 0 END) * 2000000 - priority), path, id);";
    [Fact]
    public async Task ApprovedExpressionIndexUsesActualTransactionalDdlAndRollsBackOnFault()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync("20261008030000_ExternalNotifications");
        db.Add(new Organization{Code="expression-probe",Name="Keep expression record"});await db.SaveChangesAsync();
        var before=await MigrationProbe.InspectAsync(db);
        var fault=await MigrationProbe.FailAfterFirstIndexAsync(db,new MigrationOperation[]{new SqlOperation{Sql=ApprovedRouteIndexSql}});
        Assert.False(fault.NoMigrationNeeded);Assert.True(fault.FailureInjected);Assert.Equal("42601",fault.SqlState);Assert.True(fault.Complete);
        var after=await MigrationProbe.InspectAsync(db);Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);
        Assert.DoesNotContain(after.Indexes,i=>i.Name=="ix_api_routes_environment_order_path_id");
    }
    [Theory]
    [InlineData("UPDATE organizations SET name='bad-write'",false)]
    [InlineData("CREATE INDEX arbitrary_index ON organizations(name)",false)]
    [InlineData("CREATE INDEX ix_api_routes_environment_order_path_id ON api_routes (environment_id, (priority * 2000000), path, id)",false)]
    [InlineData(ApprovedRouteIndexSql+" DROP TABLE organizations",false)]
    [InlineData(ApprovedRouteIndexSql,true)]
    public async Task ExpressionGateRejectsAllUnapprovedOrNontransactionalSqlBeforeAnyMutation(string sql,bool nontransactional)
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync("20261008030000_ExternalNotifications");
        db.Add(new Organization{Code="gate-probe",Name="Keep gate record"});await db.SaveChangesAsync();var before=await MigrationProbe.InspectAsync(db);
        MigrationOperation[] batch=[new CreateIndexOperation{Name="gate_first_org_name",Table="organizations",Columns=["name"]},new SqlOperation{Sql=sql,SuppressTransaction=nontransactional}];
        await Assert.ThrowsAsync<InvalidDataException>(()=>MigrationProbe.FailAfterFirstIndexAsync(db,batch));
        var after=await MigrationProbe.InspectAsync(db);Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);
        Assert.DoesNotContain(after.Indexes,i=>i.Name=="gate_first_org_name");
    }
    [Fact]
    public async Task AtomicIndexGateChecksLaterConcurrentIndexesBeforeExecutingFirst()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.GetService<IMigrator>().MigrateAsync("20261008030000_ExternalNotifications");
        var later=new CreateIndexOperation{Name="gate_later_org_code",Table="organizations",Columns=["code"]};later["Npgsql:CreatedConcurrently"]=true;
        var before=await MigrationProbe.InspectAsync(db);
        MigrationOperation[] batch=[new CreateIndexOperation{Name="gate_first_org_name",Table="organizations",Columns=["name"]},later];
        await Assert.ThrowsAsync<InvalidDataException>(()=>MigrationProbe.FailAfterFirstIndexAsync(db,batch));
        var after=await MigrationProbe.InspectAsync(db);Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);
        Assert.DoesNotContain(after.Indexes,i=>i.Name=="gate_first_org_name"||i.Name=="gate_later_org_code");
    }
    [Fact]
    public async Task EmptyInstallMatchesActualModelIndexesAndMigrationHistory()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        var evidence=await MigrationProbe.ApplyAsync(db,false);
        Assert.True(evidence.Complete);Assert.False(evidence.Before.ModelMatched);Assert.True(evidence.After.ModelMatched);
        Assert.False(evidence.After.PendingModelChanges);Assert.Empty(evidence.After.PendingMigrations);
        Assert.Equal(db.Database.GetMigrations(),evidence.After.AppliedMigrations);Assert.True(evidence.After.Tables.Count>20);Assert.True(evidence.After.Indexes.Count>20);
        var index=Assert.Single(evidence.After.Indexes,i=>i.Name=="IX_release_records_environment_id_created_at_id");
        Assert.Equal(new[]{"environment_id","created_at","id"},index.Columns);Assert.Equal(new[]{false,true,true},index.Descending);Assert.True(index.Valid);Assert.True(index.Ready);Assert.True(index.Bytes>0);
    }
    [Fact]
    public async Task FingerprintIncludesActualDataAndIgnoresIndexesWithoutPublishingValues()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        const string canary="migration-private-record-canary";db.Add(new Organization{Code="migration-probe",Name=canary});await db.SaveChangesAsync();
        var before=await MigrationProbe.InspectAsync(db);Assert.Equal(1,Assert.Single(before.Tables,t=>t.Name=="organizations").Rows);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX performance_probe_org_name ON organizations (name)");
        var indexed=await MigrationProbe.InspectAsync(db);Assert.Equal(before.BusinessFingerprint,indexed.BusinessFingerprint);Assert.Contains(indexed.Indexes,i=>i.Name=="performance_probe_org_name");
        await db.Database.ExecuteSqlRawAsync("UPDATE organizations SET name='changed-private-value' WHERE code='migration-probe'");
        var changed=await MigrationProbe.InspectAsync(db);Assert.NotEqual(before.BusinessFingerprint,changed.BusinessFingerprint);
        var json=JsonSerializer.Serialize(new[]{before,indexed,changed},CanonicalJson.Options);Assert.DoesNotContain(canary,json);Assert.DoesNotContain("changed-private-value",json);Assert.DoesNotContain("CREATE INDEX",json);Assert.DoesNotContain(database.ConnectionString,json);
    }
    [Fact]
    public async Task InjectedFailureRollsBackActualEfIndexDdlAndPreservesRecords()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync("20261006165518_ContractManagement");db.Add(new Organization{Code="rollback-probe",Name="Keep record"});await db.SaveChangesAsync();
        var migrations=db.GetService<IMigrationsAssembly>();var migration=migrations.CreateMigration(migrations.Migrations["20261008020000_ApprovalInboxIndexes"],db.Database.ProviderName!);
        var operations=migration.UpOperations.Cast<CreateIndexOperation>().ToArray();var before=await MigrationProbe.InspectAsync(db);
        Assert.DoesNotContain(before.Indexes,i=>i.Name==operations[0].Name);
        var evidence=await MigrationProbe.FailAfterFirstIndexAsync(db,operations);
        Assert.True(evidence.FailureInjected);Assert.Equal("42601",evidence.SqlState);Assert.True(evidence.DataUnchanged);Assert.True(evidence.IndexesUnchanged);Assert.True(evidence.Complete);
        var after=await MigrationProbe.InspectAsync(db);Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);Assert.DoesNotContain(after.Indexes,i=>i.Name==operations[0].Name);
        Assert.Equal("Keep record",(await db.Set<Organization>().SingleAsync()).Name);
    }
    [Fact]
    public async Task IndexOnlyGateRejectsPendingBusinessMigrationsBeforeMutation()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();
        await db.GetService<IMigrator>().MigrateAsync("20261006165518_ContractManagement");var before=await MigrationProbe.InspectAsync(db);
        await Assert.ThrowsAsync<InvalidDataException>(()=>MigrationProbe.ApplyAsync(db,true));
        var after=await MigrationProbe.InspectAsync(db);Assert.Equal(before.AppliedMigrations,after.AppliedMigrations);Assert.Equal(before.BusinessFingerprint,after.BusinessFingerprint);
        await Assert.ThrowsAsync<InvalidDataException>(()=>MigrationProbe.FailPendingIndexesAsync(db));
    }
    [Fact]
    public async Task NoPendingIndexesAreReportedWithoutInventingFaultMigration()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.Database.MigrateAsync();
        var before=await MigrationProbe.InspectAsync(db);var failure=await MigrationProbe.FailPendingIndexesAsync(db);var apply=await MigrationProbe.ApplyAsync(db,true);
        Assert.True(failure.NoMigrationNeeded);Assert.False(failure.FailureInjected);Assert.Null(failure.SqlState);Assert.True(failure.Complete);Assert.True(apply.Complete);
        Assert.Equal(before.BusinessFingerprint,apply.After.BusinessFingerprint);
    }
}

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PersistenceTests
{
    private static readonly string[] SourceTables = ["organizations","projects","environments","api_groups","apis","api_versions","api_routes","api_parameters","api_schemas","upstream_clusters","upstream_destinations","policies","route_policy_bindings","applications","application_credentials","application_api_permissions","users","roles","permissions","user_roles","role_permissions","user_project_scopes","approval_flows","approval_steps","approval_tasks","release_records","release_items","gateway_config_versions","gateway_config_snapshots","gateway_nodes","gateway_node_events","audit_logs"];
    [Fact] public async Task FreshDatabaseMigrates()
    {
        await using var db = new PostgresDatabase(); await db.InitializeAsync();
        await using var context = db.Context(); await context.Database.MigrateAsync();
        await using var c = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*)::int FROM information_schema.tables WHERE table_schema='public' AND table_name=ANY(@tables)", c);
        cmd.Parameters.AddWithValue("tables", SourceTables);
        Assert.Equal(32, (int)(await cmd.ExecuteScalarAsync())!);
    }
    [Fact] public async Task DuplicateApiVersionRejected()
    {
        await using var db = new PostgresDatabase(); await db.InitializeAsync();
        await using var context = db.Context(); await context.Database.MigrateAsync();
        await using var c = await db.OpenAsync();
        const string seed = """
        INSERT INTO users(id,username,display_name,status,auth_source,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000001','owner','Owner','Active','local',now(),now());
        INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000002','ORG','Org','Active',now(),now());
        INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','P','Project','Active',now(),now());
        INSERT INTO apis(id,organization_id,project_id,code,name,lifecycle_status,owner_user_id,created_at,updated_at,version_no) VALUES('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000003','API','Api','Draft','00000000-0000-0000-0000-000000000001',now(),now(),1);
        INSERT INTO api_versions(id,api_id,version,status,change_type,created_by,created_at) VALUES('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000004','1.0.0','Draft','compatible','00000000-0000-0000-0000-000000000001',now());
        """;
        await using var cmd = new NpgsqlCommand(seed,c); await cmd.ExecuteNonQueryAsync();
        await using var duplicate = new NpgsqlCommand("INSERT INTO api_versions(id,api_id,version,status,change_type,created_by,created_at) SELECT '00000000-0000-0000-0000-000000000006'::uuid,api_id,version,status,change_type,created_by,created_at FROM api_versions",c);
        var error = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation,error.SqlState);
    }
    [Fact] public async Task SnapshotRawBytesRoundTrip()
    {
        await using var db = new PostgresDatabase(); await db.InitializeAsync();
        await using var context = db.Context(); await context.Database.MigrateAsync();
        await using var c = await db.OpenAsync();
        const string setup = """
        INSERT INTO users(id,username,display_name,status,auth_source,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000001','owner','Owner','Active','local',now(),now());
        INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000002','ORG','Org','Active',now(),now());
        INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','P','Project','Active',now(),now());
        INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status) VALUES('00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-000000000003','TEST','Test',false,0,'Active');
        INSERT INTO gateway_config_versions(id,environment_id,version_no,status,created_by,created_at) VALUES('00000000-0000-0000-0000-000000000008','00000000-0000-0000-0000-000000000007',1,'Validated','00000000-0000-0000-0000-000000000001',now());
        """;
        await using var setupCmd = new NpgsqlCommand(setup,c); await setupCmd.ExecuteNonQueryAsync();
        var original = System.Text.Encoding.UTF8.GetBytes("{ \"名字\": \"订单\", \"n\": 1 }\n");
        await using var insert = new NpgsqlCommand("INSERT INTO gateway_config_snapshots(config_version_id,payload,payload_bytes,size_bytes,created_at) VALUES('00000000-0000-0000-0000-000000000008','{}',@bytes,@size,now())",c);
        insert.Parameters.AddWithValue("bytes",original); insert.Parameters.AddWithValue("size",(long)original.Length); await insert.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT payload_bytes FROM gateway_config_snapshots",c);
        var saved = (byte[])(await read.ExecuteScalarAsync())!;
        Assert.Equal(original,saved); Assert.Equal(SHA256.HashData(original),SHA256.HashData(saved));
    }
    [Fact] public async Task MigratorRunsTwiceWithoutDataLoss()
    {
        await using var db = new PostgresDatabase(); await db.InitializeAsync();
        await using var context = db.Context(); await context.Database.MigrateAsync();
        await using var c = await db.OpenAsync();
        await using var insert = new NpgsqlCommand("INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000002','KEEP','Keep','Active',now(),now())",c); await insert.ExecuteNonQueryAsync();
        await context.Database.MigrateAsync();
        await using var read = new NpgsqlCommand("SELECT name FROM organizations WHERE code='KEEP'",c);
        Assert.Equal("Keep",await read.ExecuteScalarAsync()); Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }
    [Fact] public async Task GeneratedSqlCanRunTwiceOnFreshDatabase()
    {
        await using var db = new PostgresDatabase(); await db.InitializeAsync();
        await using var c = await db.OpenAsync();
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"initial-core.sql"));
        for(var i=0;i<2;i++) { await using var command = new NpgsqlCommand(script,c); await command.ExecuteNonQueryAsync(); }
        await using var query = new NpgsqlCommand("SELECT count(*)::int FROM information_schema.tables WHERE table_schema='public' AND table_name=ANY(@tables)",c);
        query.Parameters.AddWithValue("tables",SourceTables);
        Assert.Equal(32,(int)(await query.ExecuteScalarAsync())!);
        await using var pipelineTables = new NpgsqlCommand("SELECT count(*)::int FROM information_schema.tables WHERE table_schema='public' AND table_name=ANY(@tables)",c);
        pipelineTables.Parameters.AddWithValue("tables",new[]{"release_pipelines","release_pipeline_versions","release_pipeline_runs","release_pipeline_run_stages","release_pipeline_stage_attempts","release_pipeline_events"});
        Assert.Equal(6,(int)(await pipelineTables.ExecuteScalarAsync())!);
        await using var context = db.Context(); Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }
}

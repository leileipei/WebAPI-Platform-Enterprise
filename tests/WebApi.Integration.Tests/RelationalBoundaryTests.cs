using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class RelationalBoundaryTests
{
    [Fact] public async Task ApiProjectMustBelongToOrganization()
    {
        await using var db = new PostgresDatabase(); await db.InitializeAsync();
        await using var context = db.Context(); await context.Database.MigrateAsync();
        await using var c = await db.OpenAsync();
        const string setup = """
        INSERT INTO users(id,username,display_name,status,auth_source,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000001','owner','Owner','Active','local',now(),now());
        INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000002','ORG','Org','Active',now(),now()),('00000000-0000-0000-0000-000000000009','OTHER','Other','Active',now(),now());
        INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','P','Project','Active',now(),now());
        """;
        await using var seed = new NpgsqlCommand(setup,c); await seed.ExecuteNonQueryAsync();
        await using var bad = new NpgsqlCommand("INSERT INTO apis(id,organization_id,project_id,code,name,lifecycle_status,owner_user_id,created_at,updated_at,version_no) VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000009','00000000-0000-0000-0000-000000000003','BAD','Bad','Draft','00000000-0000-0000-0000-000000000001',now(),now(),1)",c);
        var error = await Assert.ThrowsAsync<PostgresException>(()=>bad.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,error.SqlState);
    }
    [Fact] public async Task ScopeEnvironmentMustBelongToProject()
    {
        await using var db = new PostgresDatabase(); await db.InitializeAsync();
        await using var context = db.Context(); await context.Database.MigrateAsync();
        await using var c = await db.OpenAsync();
        const string setup = """
        INSERT INTO users(id,username,display_name,status,auth_source,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000001','owner','Owner','Active','local',now(),now());
        INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000002','ORG','Org','Active',now(),now());
        INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','P','Project','Active',now(),now()),('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000002','P2','Project2','Active',now(),now());
        INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status) VALUES('00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-000000000003','TEST','Test',false,0,'Active');
        """;
        await using var seed = new NpgsqlCommand(setup,c); await seed.ExecuteNonQueryAsync();
        await using var bad = new NpgsqlCommand("INSERT INTO user_project_scopes(id,user_id,organization_id,project_id,environment_id,access_mode) VALUES(gen_random_uuid(),'00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000007','read_write')",c);
        var error=await Assert.ThrowsAsync<PostgresException>(()=>bad.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,error.SqlState);
    }
    [Fact] public async Task MethodPathConflictIsRejectedAtomically()
    {
        await using var db = new PostgresDatabase(); await db.InitializeAsync();
        await using var context = db.Context(); await context.Database.MigrateAsync();
        await using var c = await db.OpenAsync();
        // Seed a complete valid resource graph before testing the conflict.
        await using var seed = new NpgsqlCommand("""
        INSERT INTO users(id,username,display_name,status,auth_source,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000001','owner','Owner','Active','local',now(),now());
        INSERT INTO organizations(id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000002','ORG','Org','Active',now(),now());
        INSERT INTO projects(id,organization_id,code,name,status,created_at,updated_at) VALUES('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','P','Project','Active',now(),now());
        INSERT INTO environments(id,project_id,code,name,is_production,sort_order,status) VALUES('00000000-0000-0000-0000-000000000007','00000000-0000-0000-0000-000000000003','TEST','Test',false,0,'Active');
        INSERT INTO apis(id,organization_id,project_id,code,name,lifecycle_status,owner_user_id,created_at,updated_at,version_no) VALUES('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-000000000003','API','Api','Draft','00000000-0000-0000-0000-000000000001',now(),now(),1);
        INSERT INTO api_versions(id,api_id,version,status,change_type,created_by,created_at) VALUES('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000004','1','Draft','compatible','00000000-0000-0000-0000-000000000001',now());
        INSERT INTO upstream_clusters(id,project_id,environment_id,name,load_balancing_policy,health_check_enabled,health_check_path,health_check_interval_sec,status) VALUES('00000000-0000-0000-0000-000000000008','00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000007','Backend','RoundRobin',false,'/',30,'Active');
        INSERT INTO api_routes(id,api_version_id,environment_id,route_name,path,normalized_path,methods,cluster_id,priority,enabled,timeout_ms,created_at,updated_at) SELECT id,'00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000007','Route','/orders','/orders',ARRAY['GET'],'00000000-0000-0000-0000-000000000008',0,true,30000,now(),now() FROM unnest(ARRAY['00000000-0000-0000-0000-000000000009'::uuid,'00000000-0000-0000-0000-000000000010'::uuid]) id;
        INSERT INTO route_methods(route_id,method,environment_id,normalized_path) VALUES('00000000-0000-0000-0000-000000000009','GET','00000000-0000-0000-0000-000000000007','/orders');
        """,c); await seed.ExecuteNonQueryAsync();
        await using var bad = new NpgsqlCommand("INSERT INTO route_methods(route_id,method,environment_id,normalized_path) VALUES('00000000-0000-0000-0000-000000000010','GET','00000000-0000-0000-0000-000000000007','/orders')",c);
        var error=await Assert.ThrowsAsync<PostgresException>(()=>bad.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation,error.SqlState);
    }
}

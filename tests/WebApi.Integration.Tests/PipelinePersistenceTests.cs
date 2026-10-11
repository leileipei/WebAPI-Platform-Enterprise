using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PipelinePersistenceTests
{
    [Fact] public async Task CatalogAddsThreeAdminGrantsAndPreservesExplicitGrants()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();await using var db=api.Context();
        await PermissionCatalog.SeedAsync(db);await PermissionCatalog.SeedAsync(db);
        string[] added=["pipeline.read","pipeline.manage","pipeline.run"];
        var permissions=await db.Set<Permission>().Where(p=>added.Contains(p.Code)).ToArrayAsync();Assert.Equal(3,permissions.Length);
        var roles=await db.Set<Role>().Where(r=>r.OrganizationId==null).ToArrayAsync();
        foreach(var role in roles)Assert.Equal(role.Code=="PlatformAdmin"?3:0,await db.Set<RolePermission>().CountAsync(r=>r.RoleId==role.Id&&permissions.Select(p=>p.Id).Contains(r.PermissionId)));
        var project=roles.Single(r=>r.Code=="ProjectAdmin");var permission=permissions.Single(p=>p.Code=="pipeline.run");
        db.Add(new RolePermission{RoleId=project.Id,PermissionId=permission.Id});await db.SaveChangesAsync();await PermissionCatalog.SeedAsync(db);
        Assert.True(await db.Set<RolePermission>().AnyAsync(r=>r.RoleId==project.Id&&r.PermissionId==permission.Id));
    }
    [Fact] public async Task MigrationCreatesSixScopedTablesAndNullableLegacyContext()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await using var c=await api.Database.OpenAsync();
        foreach(var table in new[]{"release_pipelines","release_pipeline_versions","release_pipeline_runs","release_pipeline_run_stages","release_pipeline_stage_attempts","release_pipeline_events"})
        {await using var cmd=new NpgsqlCommand("select to_regclass(@table)::text",c);cmd.Parameters.AddWithValue("table",table);Assert.Equal(table,await cmd.ExecuteScalarAsync());}
        await using var columns=new NpgsqlCommand("select count(*) from information_schema.columns where table_name in ('release_promotions','release_verifications','release_test_acceptances') and column_name in ('pipeline_run_stage_id','stage_attempt_id') and is_nullable='YES'",c);Assert.Equal(6L,await columns.ExecuteScalarAsync());
    }
    [Fact] public async Task DatabaseGuardsStageOrderAndSingleNonterminalRun()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();await using var c=await api.Database.OpenAsync();
        await RequireTablesAsync(c);
        var ids=await SeedAsync(api,c);
        await using(var duplicate=new NpgsqlCommand($"insert into release_pipeline_run_stages(id,run_id,project_id,stage_order,environment_id,profile_json,profile_hash) values(gen_random_uuid(),'{ids.Run}','{api.Project.Id}',1,'{api.Environment.Id}','{{}}','{new string('b',64)}')",c))
            Assert.Equal("23505",(await Assert.ThrowsAsync<PostgresException>(()=>duplicate.ExecuteNonQueryAsync())).SqlState);
        await using(var duplicate=new NpgsqlCommand($"insert into release_pipeline_runs(id,organization_id,project_id,pipeline_version_id,definition_hash,root_artifact_id,root_artifact_hash,source_environment_id,source_release_id,source_config_version,source_deployment_sequence,policy_revision,created_by) select gen_random_uuid(),organization_id,project_id,pipeline_version_id,definition_hash,root_artifact_id,root_artifact_hash,source_environment_id,source_release_id,source_config_version,source_deployment_sequence,policy_revision,created_by from release_pipeline_runs where id='{ids.Run}'",c))
            Assert.Equal("23505",(await Assert.ThrowsAsync<PostgresException>(()=>duplicate.ExecuteNonQueryAsync())).SqlState);
    }
    [Fact] public async Task StagePredecessorCannotComeFromDifferentRunAndVersionsCannotMutate()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();await using var c=await api.Database.OpenAsync();await RequireTablesAsync(c);var ids=await SeedAsync(api,c);
        var otherRun=Guid.NewGuid();var otherStage=Guid.NewGuid();
        await using(var other=new NpgsqlCommand($"insert into release_pipeline_runs(id,organization_id,project_id,pipeline_version_id,definition_hash,root_artifact_id,root_artifact_hash,source_environment_id,source_release_id,source_config_version,source_deployment_sequence,policy_revision,created_by,status) select '{otherRun}',organization_id,project_id,pipeline_version_id,definition_hash,root_artifact_id,root_artifact_hash,source_environment_id,source_release_id,source_config_version,source_deployment_sequence,policy_revision,created_by,'Cancelled' from release_pipeline_runs where id='{ids.Run}';insert into release_pipeline_run_stages(id,run_id,project_id,stage_order,environment_id,profile_json,profile_hash) values('{otherStage}','{otherRun}','{api.Project.Id}',1,'{api.Environment.Id}','{{}}','{new string('b',64)}')",c))await other.ExecuteNonQueryAsync();
        await using(var wrong=new NpgsqlCommand($"update release_pipeline_run_stages set source_stage_id='{otherStage}' where id='{ids.Stage}'",c))
            Assert.Equal("23503",(await Assert.ThrowsAsync<PostgresException>(()=>wrong.ExecuteNonQueryAsync())).SqlState);
        await using(var mutate=new NpgsqlCommand($"update release_pipeline_versions set definition_hash='{new string('c',64)}' where id='{ids.Version}'",c))
            Assert.Equal("23514",(await Assert.ThrowsAsync<PostgresException>(()=>mutate.ExecuteNonQueryAsync())).SqlState);
    }
    [Fact] public async Task DatabaseKeepsOneCurrentAttemptAndImmutableEvents()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();await using var c=await api.Database.OpenAsync();await RequireTablesAsync(c);var ids=await SeedAsync(api,c);var attempt=Guid.NewGuid();var evt=Guid.NewGuid();
        await using(var insert=new NpgsqlCommand($"insert into release_pipeline_stage_attempts(id,run_stage_id,run_id,project_id,environment_id,attempt_no,activated_at,deadline_at) values('{attempt}','{ids.Stage}','{ids.Run}','{api.Project.Id}','{api.Environment.Id}',1,now(),now()+interval '1 day');insert into release_pipeline_events(id,run_id,project_id,stage_id,attempt_id,from_status,to_status,reason_code,actor_id) values('{evt}','{ids.Run}','{api.Project.Id}','{ids.Stage}','{attempt}','','Active','created','{api.User.Id}')",c))await insert.ExecuteNonQueryAsync();
        await using(var duplicate=new NpgsqlCommand($"insert into release_pipeline_stage_attempts(id,run_stage_id,run_id,project_id,environment_id,attempt_no,activated_at,deadline_at) values(gen_random_uuid(),'{ids.Stage}','{ids.Run}','{api.Project.Id}','{api.Environment.Id}',2,now(),now()+interval '1 day')",c))
            Assert.Equal("23505",(await Assert.ThrowsAsync<PostgresException>(()=>duplicate.ExecuteNonQueryAsync())).SqlState);
        await using(var missingProfile=new NpgsqlCommand($"insert into release_verifications(id,organization_id,project_id,artifact_id,release_id,environment_id,config_version,deployment_sequence,snapshot_hash,access_address_revision,policy_revision,phase,type,result,is_manual,started_at,finished_at,expires_at,created_by,created_at,pipeline_run_stage_id,stage_attempt_id) select gen_random_uuid(),organization_id,project_id,root_artifact_id,source_release_id,source_environment_id,1,1,'{new string('a',64)}',1,1,'SourceTest','InterfaceFunction','Passed',true,now(),now(),now()+interval '1 day','{api.User.Id}',now(),'{ids.Stage}','{attempt}' from release_pipeline_runs where id='{ids.Run}'",c))
            Assert.Equal("23514",(await Assert.ThrowsAsync<PostgresException>(()=>missingProfile.ExecuteNonQueryAsync())).SqlState);
        await using(var mutate=new NpgsqlCommand($"update release_pipeline_events set reason_code='rewritten' where id='{evt}'",c))
            Assert.Equal("23514",(await Assert.ThrowsAsync<PostgresException>(()=>mutate.ExecuteNonQueryAsync())).SqlState);
    }
    [Fact] public async Task UpgradeFromDeliveryPreservesOldPolicyAndReleaseProjection()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.GetService<IMigrator>().MigrateAsync("20261009020000_ReleaseDelivery");
        var user=new UserRecord{Username="retained",DisplayName="保留",PasswordHash="fixture",SecurityStamp="fixture"};var org=new Organization{Code="KEEP",Name="保留"};var project=new Project{OrganizationId=org.Id,Code="KEEP",Name="保留"};var source=new EnvironmentRecord{ProjectId=project.Id,Code="TEST",Name="测试"};var target=new EnvironmentRecord{ProjectId=project.Id,Code="PROD",Name="生产",IsProduction=true};
        db.AddRange(user,org,project,source,target);await db.SaveChangesAsync();await using var c=await database.OpenAsync();
        await using(var seed=new NpgsqlCommand($"insert into project_delivery_policies(id,organization_id,project_id,source_environment_id,target_environment_id,mode,updated_by,updated_at) values(gen_random_uuid(),'{org.Id}','{project.Id}','{source.Id}','{target.Id}','PromotionRequired','{user.Id}',now());insert into release_records(id,environment_id,release_no,from_config_version,to_config_version,release_type,status,requested_by,created_at) values(gen_random_uuid(),'{target.Id}','KEEP',0,4,'publish','Succeeded','{user.Id}',now())",c))await seed.ExecuteNonQueryAsync();
        async Task<string> Read(string table){await using var cmd=new NpgsqlCommand($"select (to_jsonb(t)-'active_pipeline_version_id')::text from {table} t",c);return (string)(await cmd.ExecuteScalarAsync())!;}
        var policy=await Read("project_delivery_policies");var release=await Read("release_records");await db.Database.MigrateAsync();Assert.Equal(policy,await Read("project_delivery_policies"));Assert.Equal(release,await Read("release_records"));Assert.False(db.Database.HasPendingModelChanges());await RequireTablesAsync(c);
    }
    private static async Task RequireTablesAsync(NpgsqlConnection c){await using var cmd=new NpgsqlCommand("select to_regclass('release_pipeline_runs')::text",c);Assert.Equal("release_pipeline_runs",await cmd.ExecuteScalarAsync());}
    private static async Task<(Guid Run,Guid Stage,Guid Version)> SeedAsync(ApiFixture api,NpgsqlConnection c)
    {
        var pipeline=Guid.NewGuid();var version=Guid.NewGuid();var run=Guid.NewGuid();var stage=Guid.NewGuid();var release=Guid.NewGuid();var artifact=Guid.NewGuid();var hash=new string('a',64);
        await using var cmd=new NpgsqlCommand($$"""
        insert into release_records(id,environment_id,release_no,from_config_version,to_config_version,release_type,status,requested_by,created_at) values('{{release}}','{{api.Environment.Id}}','ROOT',0,1,'publish','Succeeded','{{api.User.Id}}',now());
        insert into release_artifacts(id,organization_id,project_id,source_environment_id,source_release_id,canonical_content,artifact_hash,source_snapshot_hash,created_by,created_at) values('{{artifact}}','{{api.Organization.Id}}','{{api.Project.Id}}','{{api.Environment.Id}}','{{release}}','{}','{{hash}}','{{hash}}','{{api.User.Id}}',now());
        insert into release_pipelines(id,organization_id,project_id,name,description,draft_json,created_by,updated_by) values('{{pipeline}}','{{api.Organization.Id}}','{{api.Project.Id}}','流水线','','{}','{{api.User.Id}}','{{api.User.Id}}');
        insert into release_pipeline_versions(id,pipeline_id,project_id,version_no,content_json,definition_hash,created_by) values('{{version}}','{{pipeline}}','{{api.Project.Id}}',1,'{}','{{hash}}','{{api.User.Id}}');
        insert into release_pipeline_runs(id,organization_id,project_id,pipeline_version_id,definition_hash,root_artifact_id,root_artifact_hash,source_environment_id,source_release_id,source_config_version,source_deployment_sequence,policy_revision,created_by) values('{{run}}','{{api.Organization.Id}}','{{api.Project.Id}}','{{version}}','{{hash}}','{{artifact}}','{{hash}}','{{api.Environment.Id}}','{{release}}',1,1,1,'{{api.User.Id}}');
        insert into release_pipeline_run_stages(id,run_id,project_id,stage_order,environment_id,profile_json,profile_hash) values('{{stage}}','{{run}}','{{api.Project.Id}}',1,'{{api.Environment.Id}}','{}','{{hash}}');
        """,c);await cmd.ExecuteNonQueryAsync();return(run,stage,version);
    }
}

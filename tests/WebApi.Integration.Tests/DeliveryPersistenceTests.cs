using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class DeliveryPersistenceTests
{
    [Fact] public async Task UpgradePreservesOrganizationWideApplicationAndLegacyReleaseBytes()
    {
        await using var database=new PostgresDatabase();await database.InitializeAsync();await using var db=database.Context();await db.GetService<IMigrator>().MigrateAsync("20261009011000_ReleaseAccessContexts");
        var user=new UserRecord{Username="retained",DisplayName="保留",PasswordHash="synthetic",SecurityStamp="retained"};var org=new Organization{Code="RETAINED",Name="保留组织"};var project=new Project{OrganizationId=org.Id,Code="KEEP",Name="保留项目"};var env=new EnvironmentRecord{ProjectId=project.Id,Code="PROD",Name="保留生产",IsProduction=true};var app=new ApplicationRecord{OrganizationId=org.Id,Code="ORG_APP",Name="组织应用",Owner="保留",ProjectId=null};db.AddRange(user,org,project,env);await db.SaveChangesAsync();
        await using var connection=await database.OpenAsync();await using(var oldAppInsert=new NpgsqlCommand($"insert into applications(id,organization_id,project_id,code,name,owner,status,created_at,updated_at) values('{app.Id}','{org.Id}',null,'ORG_APP','保留组织应用','保留','Active',now(),now())",connection))await oldAppInsert.ExecuteNonQueryAsync();await using(var cmd=new NpgsqlCommand($"insert into release_records(id,environment_id,release_no,from_config_version,to_config_version,release_type,status,requested_by,created_at) values(gen_random_uuid(),'{env.Id}','retained',0,1,'publish','Succeeded','{user.Id}',now())",connection))await cmd.ExecuteNonQueryAsync();
        async Task<string> Read(string table){await using var cmd=new NpgsqlCommand($"select (to_jsonb(t)-'artifact_id'-'promotion_id'-'source_release_id')::text from {table} t",connection);return (string)(await cmd.ExecuteScalarAsync())!;}
        var oldRelease=await Read("release_records");var oldApp=await Read("applications");await db.Database.MigrateAsync();Assert.Equal(oldRelease,await Read("release_records"));Assert.Equal(oldApp,await Read("applications"));await db.Database.MigrateAsync();Assert.False(db.Database.HasPendingModelChanges());
        await using var nullable=new NpgsqlCommand("select artifact_id is null and promotion_id is null and source_release_id is null from release_records",connection);Assert.Equal(true,await nullable.ExecuteScalarAsync());
    }
    private static string Url(ApiFixture api)=>$"/api/v1/projects/{api.Project.Id}/delivery-policy";
    private static object Body(Guid source,Guid target,string mode="PromotionRequired",int minutes=1440,string[]? types=null)=>new{sourceEnvironmentId=source,targetEnvironmentId=target,mode,requiredTestTypes=types??["InterfaceFunction","Integration","ContractCompatibility"],verificationValidityMinutes=minutes};
    private static async Task<EnvironmentRecord> Setup(ApiFixture api)
    {
        await api.InitializeAsync();await api.SeedReleaseAsync();
        var source=new EnvironmentRecord{ProjectId=api.Project.Id,Code="SOURCE",Name="测试来源"};
        await using(var db=api.Context()){db.Add(source);var target=await db.Set<EnvironmentRecord>().SingleAsync();target.GatewayPublicUrl="https://prod.example";await db.SaveChangesAsync();}
        using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();return source;
    }
    [Fact] public async Task ExistingProjectDefaultsToLegacyWithoutChangingReleases()
    {
        await using var api=new ApiFixture();await Setup(api);var legacy=new ReleaseRecord{EnvironmentId=api.Environment.Id,ReleaseNo="legacy",ReleaseType="publish",RequestedBy=api.User.Id,Status="Succeeded"};
        await using(var db=api.Context()){db.Add(legacy);await db.SaveChangesAsync();}
        using var response=await api.Client.GetAsync(Url(api));response.EnsureSuccessStatusCode();var dto=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("Legacy",dto.GetProperty("mode").GetString());Assert.Equal(0,dto.GetProperty("revision").GetInt64());Assert.Equal(1440,dto.GetProperty("verificationValidityMinutes").GetInt32());Assert.Equal(3,dto.GetProperty("requiredTestTypes").GetArrayLength());
        await using var check=api.Context();Assert.Equal("Succeeded",(await check.Set<ReleaseRecord>().SingleAsync()).Status);
    }
    [Theory] [InlineData("cross_project")] [InlineData("disabled_source")] [InlineData("disabled_target")] [InlineData("production_source")] [InlineData("nonproduction_target")] [InlineData("same_environment")]
    public async Task InvalidConnectionCannotBeSaved(string reason)
    {
        await using var api=new ApiFixture();var source=await Setup(api);
        await using(var db=api.Context()){
            var e=await db.Set<EnvironmentRecord>().SingleAsync(x=>x.Id==source.Id);
            if(reason=="cross_project"){var p=new Project{OrganizationId=api.Organization.Id,Code="OTHER",Name="其他"};db.Add(p);source=new EnvironmentRecord{ProjectId=p.Id,Code="FOREIGN_SOURCE",Name="其他项目来源"};db.Add(source);}
            if(reason=="disabled_source")e.Status="Disabled";if(reason=="production_source")e.IsProduction=true;
            if(reason=="disabled_target")(await db.Set<EnvironmentRecord>().SingleAsync(x=>x.Id==api.Environment.Id)).Status="Disabled";
            if(reason=="nonproduction_target")(await db.Set<EnvironmentRecord>().SingleAsync(x=>x.Id==api.Environment.Id)).IsProduction=false;
            await db.SaveChangesAsync();
        }
        using var response=await api.WriteAsync(HttpMethod.Put,Url(api),Body(reason=="same_environment"?api.Environment.Id:source.Id,api.Environment.Id),"\"0\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
    }
    [Theory] [InlineData("missing_entry")] [InlineData("disabled_flow")] [InlineData("missing_flow")]
    public async Task EnablingRequiresPublicEntryAndProductionApproval(string reason)
    {
        await using var api=new ApiFixture();var source=await Setup(api);await using(var db=api.Context()){
            var target=await db.Set<EnvironmentRecord>().SingleAsync(x=>x.Id==api.Environment.Id);
            if(reason=="missing_entry")target.GatewayPublicUrl=null;if(reason=="missing_flow")target.ReleasePolicyId=null;if(reason=="disabled_flow")(await db.Set<ApprovalFlow>().SingleAsync()).Enabled=false;await db.SaveChangesAsync();
        }
        using var response=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id),"\"0\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
    }
    [Theory] [InlineData("WaitingApproval")] [InlineData("Ready")] [InlineData("Building")] [InlineData("Publishing")]
    public async Task EnablingCannotReclassifyAnExistingActiveRelease(string status)
    {
        await using var api=new ApiFixture();var source=await Setup(api);await using(var db=api.Context()){db.Add(new ReleaseRecord{EnvironmentId=api.Environment.Id,ReleaseNo="existing",ReleaseType="publish",Status=status,RequestedBy=api.User.Id});await db.SaveChangesAsync();}
        using var response=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id),"\"0\"");Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
    }
    [Theory] [InlineData(0)] [InlineData(10081)]
    public async Task ExpiryOutsideApprovedRangeIsRejected(int minutes)
    {
        await using var api=new ApiFixture();var source=await Setup(api);using var response=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id,minutes:minutes),"\"0\"");Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
    }
    [Fact] public async Task SavingHasRevisionAuditAndNoReleaseSideEffects()
    {
        await using var api=new ApiFixture();var source=await Setup(api);using var saved=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id),"\"0\"");saved.EnsureSuccessStatusCode();Assert.Equal("\"1\"",saved.Headers.ETag?.Tag);
        using var stale=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id,"Legacy"),"\"0\"");Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        using var changed=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id,"Legacy"),"\"1\"");changed.EnsureSuccessStatusCode();
        await using var db=api.Context();Assert.Equal(2,await db.Set<AuditLog>().CountAsync(x=>x.Action=="delivery_policy.save"));Assert.Empty(await db.Set<ReleaseRecord>().ToArrayAsync());Assert.Empty(await db.Set<OutboxMessage>().ToArrayAsync());
    }
    [Fact] public async Task NewPermissionsOnlyDefaultToPlatformAdminAndExplicitGrantsSurviveReseed()
    {
        await using var api=new ApiFixture();await Setup(api);await using var db=api.Context();await PermissionCatalog.SeedAsync(db);await PermissionCatalog.SeedAsync(db);
        string[] added=["release.test.record","release.test.accept","release.verify"];
        var permissions=await db.Set<Permission>().Where(x=>added.Contains(x.Code)).ToArrayAsync();Assert.Equal(3,permissions.Length);
        var roles=await db.Set<Role>().Where(x=>x.OrganizationId==null).ToArrayAsync();
        foreach(var r in roles){var grants=await db.Set<RolePermission>().Where(x=>x.RoleId==r.Id&&permissions.Select(p=>p.Id).Contains(x.PermissionId)).CountAsync();Assert.Equal(r.Code=="PlatformAdmin"?3:0,grants);}
        var project=roles.Single(x=>x.Code=="ProjectAdmin");var explicitPermission=permissions.Single(x=>x.Code=="release.test.accept");db.Add(new RolePermission{RoleId=project.Id,PermissionId=explicitPermission.Id});await db.SaveChangesAsync();await PermissionCatalog.SeedAsync(db);Assert.True(await db.Set<RolePermission>().AnyAsync(x=>x.RoleId==project.Id&&x.PermissionId==explicitPermission.Id));
    }
    [Fact] public async Task MigrationCreatesScopedDeliveryTablesAndNullableLegacyLinks()
    {
        await using var api=new ApiFixture();await Setup(api);await using var c=await api.Database.OpenAsync();
        foreach(var table in new[]{"project_delivery_policies","release_artifacts","release_verifications","release_test_acceptances","release_promotions","release_promotion_mappings","release_promotion_events","verification_reports"}){await using var cmd=new NpgsqlCommand("select to_regclass(@t)::text",c);cmd.Parameters.AddWithValue("t",table);Assert.Equal(table,await cmd.ExecuteScalarAsync());}
        await using var columns=new NpgsqlCommand("select count(*) from information_schema.columns where table_name='release_records' and column_name in ('artifact_id','promotion_id','source_release_id') and is_nullable='YES'",c);Assert.Equal(3L,await columns.ExecuteScalarAsync());
    }
    [Fact] public async Task OnlyOneFormalReleaseCanLinkPromotionWhileRecoveryKeepsTraceability()
    {
        await using var api=new ApiFixture();var source=await Setup(api);await using var c=await api.Database.OpenAsync();
        await using(var exists=new NpgsqlCommand("select to_regclass('release_promotions')::text",c))Assert.Equal("release_promotions",await exists.ExecuteScalarAsync());
        var sourceRelease=Guid.NewGuid();var artifact=Guid.NewGuid();var promotion=Guid.NewGuid();var formal=Guid.NewGuid();var recovery=Guid.NewGuid();
        await using(var cmd=new NpgsqlCommand($"""
            insert into release_records(id,environment_id,release_no,from_config_version,to_config_version,release_type,status,requested_by,created_at) values('{sourceRelease}','{source.Id}','source',0,1,'publish','Succeeded','{api.User.Id}',now());
            insert into release_artifacts(id,organization_id,project_id,source_environment_id,source_release_id,canonical_content,artifact_hash,source_snapshot_hash,created_by,created_at) values('{artifact}','{api.Organization.Id}','{api.Project.Id}','{source.Id}','{sourceRelease}','{"{}"}','{new string('a',64)}','{new string('b',64)}','{api.User.Id}',now());
            insert into release_promotions(id,organization_id,project_id,artifact_id,source_environment_id,target_environment_id,source_release_id,requested_by,created_at) values('{promotion}','{api.Organization.Id}','{api.Project.Id}','{artifact}','{source.Id}','{api.Environment.Id}','{sourceRelease}','{api.User.Id}',now());
            insert into release_records(id,environment_id,release_no,from_config_version,to_config_version,release_type,status,requested_by,created_at,artifact_id,promotion_id,source_release_id) values('{formal}','{api.Environment.Id}','formal',0,1,'publish','Failed','{api.User.Id}',now(),'{artifact}','{promotion}','{sourceRelease}');
            """,c))await cmd.ExecuteNonQueryAsync();
        await using(var duplicate=new NpgsqlCommand($"insert into release_records(id,environment_id,release_no,from_config_version,to_config_version,release_type,status,requested_by,created_at,artifact_id,promotion_id,source_release_id) values(gen_random_uuid(),'{api.Environment.Id}','duplicate',0,1,'publish','Draft','{api.User.Id}',now(),'{artifact}','{promotion}','{sourceRelease}')",c))Assert.Equal("23505",(await Assert.ThrowsAsync<PostgresException>(()=>duplicate.ExecuteNonQueryAsync())).SqlState);
        var otherPromotion=Guid.NewGuid();await using(var cmd=new NpgsqlCommand($"insert into release_promotions(id,organization_id,project_id,artifact_id,source_environment_id,target_environment_id,source_release_id,requested_by,created_at) values('{otherPromotion}','{api.Organization.Id}','{api.Project.Id}','{artifact}','{source.Id}','{api.Environment.Id}','{sourceRelease}','{api.User.Id}',now())",c))await cmd.ExecuteNonQueryAsync();
        await using(var wrongScope=new NpgsqlCommand($"insert into release_records(id,environment_id,release_no,from_config_version,to_config_version,release_type,status,requested_by,created_at,artifact_id,promotion_id,source_release_id) values(gen_random_uuid(),'{source.Id}','wrong-scope',0,1,'publish','Draft','{api.User.Id}',now(),'{artifact}','{otherPromotion}','{sourceRelease}')",c))Assert.Equal("23503",(await Assert.ThrowsAsync<PostgresException>(()=>wrongScope.ExecuteNonQueryAsync())).SqlState);
        await using(var cmd=new NpgsqlCommand($"insert into release_records(id,environment_id,release_no,from_config_version,to_config_version,release_type,status,requested_by,created_at,recovery_of,artifact_id,source_release_id) values('{recovery}','{api.Environment.Id}','recovery',0,1,'publish','Building','{api.User.Id}',now(),'{formal}','{artifact}','{sourceRelease}'); select promotion_id is null and recovery_of='{formal}' from release_records where id='{recovery}'",c))Assert.Equal(true,await cmd.ExecuteScalarAsync());
    }
    [Fact] public async Task RepeatedPolicyCommandReplaysOnceAndDifferentBodyConflicts()
    {
        await using var api=new ApiFixture();var source=await Setup(api);var key=Guid.NewGuid().ToString("N");
        async Task<HttpResponseMessage> Save(string mode){using var req=new HttpRequestMessage(HttpMethod.Put,Url(api)){Content=JsonContent.Create(Body(source.Id,api.Environment.Id,mode))};req.Headers.Add("X-CSRF-Token",await api.CsrfAsync());req.Headers.Add("Idempotency-Key",key);req.Headers.Add("If-Match","\"0\"");return await api.Client.SendAsync(req);}
        using var first=await Save("PromotionRequired");first.EnsureSuccessStatusCode();var original=await first.Content.ReadAsStringAsync();using var replay=await Save("PromotionRequired");replay.EnsureSuccessStatusCode();Assert.Equal(original,await replay.Content.ReadAsStringAsync());using var conflict=await Save("Legacy");Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);await using var db=api.Context();Assert.Equal(1,await db.Set<AuditLog>().CountAsync(x=>x.Action=="delivery_policy.save"));
    }
    [Fact] public async Task SavingRequiresTargetWriteAndReadingRechecksBothEnvironmentScopes()
    {
        await using var api=new ApiFixture();var source=await Setup(api);using var save=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id),"\"0\"");save.EnsureSuccessStatusCode();
        await using(var db=api.Context()){await db.Set<UserProjectScope>().ExecuteDeleteAsync();db.AddRange(new UserProjectScope{UserId=api.User.Id,OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,EnvironmentId=source.Id,AccessMode="read_write"},new UserProjectScope{UserId=api.User.Id,OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,EnvironmentId=api.Environment.Id,AccessMode="read"});await db.SaveChangesAsync();}
        using var denied=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id,"Legacy"),"\"1\"");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        await using(var db=api.Context()){await db.Set<UserProjectScope>().Where(x=>x.EnvironmentId==source.Id).ExecuteDeleteAsync();db.Add(new UserProjectScope{UserId=api.User.Id,OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,AccessMode="read"});await db.SaveChangesAsync();}
        using var readable=await api.Client.GetAsync(Url(api));readable.EnsureSuccessStatusCode();
        await using(var db=api.Context())await db.Set<UserProjectScope>().Where(x=>x.EnvironmentId==null).ExecuteDeleteAsync();
        using var inaccessible=await api.Client.GetAsync(Url(api));Assert.Equal(HttpStatusCode.NotFound,inaccessible.StatusCode);
    }
    [Fact] public async Task ProjectWriteAloneDoesNotGrantTargetEnvironmentWrite()
    {
        await using var api=new ApiFixture();var source=await Setup(api);await using(var db=api.Context()){var permission=await db.Set<Permission>().SingleAsync(x=>x.Code=="environment.write");await db.Set<RolePermission>().Where(x=>x.PermissionId==permission.Id).ExecuteDeleteAsync();}
        using var denied=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id),"\"0\"");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);using var current=await api.Client.GetAsync(Url(api));current.EnsureSuccessStatusCode();Assert.Equal("Legacy",(await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mode").GetString());
    }
    [Fact] public async Task AuditRecordsConnectionModeAndExpiryWithoutEnvironmentAddresses()
    {
        await using var api=new ApiFixture();var source=await Setup(api);using var save=await api.WriteAsync(HttpMethod.Put,Url(api),Body(source.Id,api.Environment.Id),"\"0\"");save.EnsureSuccessStatusCode();await using var db=api.Context();var audit=await db.Set<AuditLog>().SingleAsync(x=>x.Action=="delivery_policy.save");Assert.Contains("PromotionRequired",audit.AfterJson);Assert.Contains("1440",audit.AfterJson);Assert.Contains(source.Id.ToString(),audit.AfterJson);Assert.Contains(api.Environment.Id.ToString(),audit.AfterJson);Assert.DoesNotContain("prod.example",audit.AfterJson);
    }
}

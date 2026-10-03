using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class GovernanceTests
{
    [Fact] public async Task CrossOrganizationReadIsHidden()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();
        var foreign=new Organization {Code="FOREIGN",Name="不可见组织"};await using(var db=api.Context()) {db.Add(foreign);await db.SaveChangesAsync();}
        using var own=await api.Client.GetAsync($"/api/v1/organizations/{api.Organization.Id}");Assert.Equal(HttpStatusCode.OK,own.StatusCode);
        using var result=await api.Client.GetAsync($"/api/v1/organizations/{foreign.Id}");Assert.Equal(HttpStatusCode.NotFound,result.StatusCode);Assert.DoesNotContain("不可见组织",await result.Content.ReadAsStringAsync());
    }
    [Fact] public async Task ReadonlyScopeCannotWrite()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync("read");using var login=await api.LoginAsync();
        using var result=await api.WriteAsync(HttpMethod.Put,$"/api/v1/organizations/{api.Organization.Id}",new {code="ORG",name="越权",status="Active"},"\"1\"");Assert.Equal(HttpStatusCode.Forbidden,result.StatusCode);
        await using var db=api.Context();Assert.Equal("组织",(await db.Set<Organization>().SingleAsync()).Name);Assert.False(await db.Set<AuditLog>().AnyAsync(x=>x.Action=="organization.update"));
    }
    [Fact] public async Task RevokedScopeBlocksExistingSession()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();
        await using(var db=api.Context()) {await db.Set<UserProjectScope>().ExecuteDeleteAsync();}
        using var result=await api.WriteAsync(HttpMethod.Put,$"/api/v1/organizations/{api.Organization.Id}",new {code="ORG",name="旧会话",status="Active"},"\"1\"");Assert.Equal(HttpStatusCode.Forbidden,result.StatusCode);
        await using var check=api.Context();Assert.Equal("组织",(await check.Set<Organization>().SingleAsync()).Name);Assert.False(await check.Set<AuditLog>().AnyAsync(x=>x.Action=="organization.update"));
    }
    [Fact] public async Task StaleETagReturns412()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();
        using var first=await api.WriteAsync(HttpMethod.Put,$"/api/v1/organizations/{api.Organization.Id}",new {code="ORG",name="新版",status="Active"},"\"1\"");Assert.Equal(HttpStatusCode.OK,first.StatusCode);Assert.Equal("\"2\"",first.Headers.ETag?.ToString());
        using var old=await api.WriteAsync(HttpMethod.Put,$"/api/v1/organizations/{api.Organization.Id}",new {code="ORG",name="旧版",status="Active"},"\"1\"");Assert.Equal(HttpStatusCode.PreconditionFailed,old.StatusCode);
        await using var db=api.Context();Assert.Equal("新版",(await db.Set<Organization>().SingleAsync()).Name);Assert.Equal(1,await db.Set<AuditLog>().CountAsync(x=>x.Action=="organization.update"));
    }
    [Fact] public async Task LastActivePlatformAdminProtected()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);using var login=await api.LoginAsync();
        using var result=await api.WriteAsync(HttpMethod.Put,$"/api/v1/users/{api.User.Id}",new {displayName="管理员",status="Disabled"},"\"1\"");Assert.Equal(HttpStatusCode.Conflict,result.StatusCode);
        using var revoke=await api.WriteAsync(HttpMethod.Put,$"/api/v1/users/{api.User.Id}/roles",new {roleIds=Array.Empty<Guid>()},"\"1\"");Assert.Equal(HttpStatusCode.Conflict,revoke.StatusCode);
        await using var db=api.Context();Assert.Equal("Active",(await db.Set<UserRecord>().SingleAsync()).Status);Assert.Single(await db.Set<UserRole>().ToListAsync());
    }
    [Fact] public async Task AuditFailureRollsBackBusinessWrite()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();
        await using(var c=await api.Database.OpenAsync()) {await using var cmd=new NpgsqlCommand("CREATE FUNCTION fail_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.action='organization.update' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END; $$; CREATE TRIGGER test_audit_failure BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_audit();",c);await cmd.ExecuteNonQueryAsync();}
        using var result=await api.WriteAsync(HttpMethod.Put,$"/api/v1/organizations/{api.Organization.Id}",new {code="ORG",name="不能提交",status="Active"},"\"1\"");Assert.Equal(HttpStatusCode.InternalServerError,result.StatusCode);
        await using var db=api.Context();var org=await db.Set<Organization>().SingleAsync();Assert.Equal("组织",org.Name);Assert.Equal(1,org.Revision);Assert.False(await db.Set<AuditLog>().AnyAsync(x=>x.Action=="organization.update"));
    }
    [Theory] [InlineData(true)] [InlineData(false)] public async Task ActiveReleasePreventsDisablingProjectOrEnvironment(bool project)
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync();using var login=await api.LoginAsync();
        await using(var db=api.Context()) {db.Add(new ReleaseRecord {EnvironmentId=api.Environment.Id,ReleaseNo="IN-FLIGHT",ReleaseType="publish",Status="Publishing",RequestedBy=api.User.Id});await db.SaveChangesAsync();}
        var path=project?$"/api/v1/projects/{api.Project.Id}":$"/api/v1/environments/{api.Environment.Id}";
        object body=project?new {code="PROJECT",name="项目",status="Disabled"}:new {code="TEST",name="测试",status="Disabled",isProduction=false,sortOrder=0,releasePolicyId=(Guid?)null};
        using var result=await api.WriteAsync(HttpMethod.Put,path,body,"\"1\"");Assert.Equal(HttpStatusCode.Conflict,result.StatusCode);
        await using var check=api.Context();Assert.Equal("Active",(await check.Set<Project>().SingleAsync()).Status);Assert.Equal("Active",(await check.Set<EnvironmentRecord>().SingleAsync()).Status);
    }
    [Fact] public async Task ScopeUnionAllowsEnvironmentWriteWithoutBroadeningOrganizationWrite()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync("read");using var login=await api.LoginAsync();
        await using(var db=api.Context()) {db.Add(new UserProjectScope {UserId=api.User.Id,OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,EnvironmentId=api.Environment.Id,AccessMode="read_write"});await db.SaveChangesAsync();}
        using var environment=await api.WriteAsync(HttpMethod.Put,$"/api/v1/environments/{api.Environment.Id}",new {code="TEST",name="新测试",status="Active",isProduction=false,sortOrder=0,releasePolicyId=(Guid?)null},"\"1\"");Assert.Equal(HttpStatusCode.OK,environment.StatusCode);
        using var organization=await api.WriteAsync(HttpMethod.Put,$"/api/v1/organizations/{api.Organization.Id}",new {code="ORG",name="越权",status="Active"},"\"1\"");Assert.Equal(HttpStatusCode.Forbidden,organization.StatusCode);
    }
    [Fact] public async Task AuditEndpointSerializesAddressAsSafeString()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);using var login=await api.LoginAsync();
        using var response=await api.Client.GetAsync("/api/v1/audit-logs");Assert.Equal(HttpStatusCode.OK,response.StatusCode);var json=await response.Content.ReadAsStringAsync();Assert.Contains("127.0.0.1",json);Assert.DoesNotContain(api.Password,json);Assert.DoesNotContain("scopeId",json,StringComparison.OrdinalIgnoreCase);
    }
}

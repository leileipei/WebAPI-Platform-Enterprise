using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ApplicationOverviewTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task App_summary_uses_visible_distinct_active_api_windows(bool organizationApplication)
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();var now=DateTimeOffset.UtcNow;
        await using(var db=api.Context())
        {
            if(organizationApplication)(await db.Set<ApplicationRecord>().SingleAsync()).ProjectId=null;
            var env2=new EnvironmentRecord{ProjectId=api.Project.Id,Code="UAT",Name="验收"};var hidden=new EnvironmentRecord{ProjectId=api.Project.Id,Code="HIDDEN",Name="停用",Status="Disabled"};db.AddRange(env2,hidden);
            var second=new Api{OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,Code="SECOND",Name="第二API",OwnerUserId=api.User.Id};var third=new Api{OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,Code="THIRD",Name="隐藏API",OwnerUserId=api.User.Id};db.AddRange(second,third);
            foreach(var (id,env,from,to) in new[]{(api.Api.Id,api.Environment.Id,now.AddDays(-1),(DateTimeOffset?)now.AddDays(1)),(api.Api.Id,env2.Id,now.AddDays(-1),(DateTimeOffset?)null),(second.Id,api.Environment.Id,now.AddMinutes(-1),(DateTimeOffset?)null),(third.Id,hidden.Id,now.AddDays(-1),(DateTimeOffset?)null),(third.Id,api.Environment.Id,now.AddDays(1),(DateTimeOffset?)now.AddDays(2))})db.Add(new ApplicationApiPermission{ApplicationId=api.Application.Id,ApiId=id,EnvironmentId=env,ValidFrom=from,ExpiresAt=to,CreatedBy=api.User.Id});
            foreach(var (name,status,from,to) in new[]{("active","Active",now.AddDays(-1),now.AddDays(1)),("future","Active",now.AddDays(1),now.AddDays(2)),("expired","Active",now.AddDays(-2),now.AddDays(-1)),("revoked","Revoked",now.AddDays(-1),now.AddDays(1))})db.Add(new ApplicationCredential{ApplicationId=api.Application.Id,AccessKey=name,SecretHash=new string('a',64),SecretLast4="1234",Status=status,ValidFrom=from,ExpiresAt=to});
            await db.SaveChangesAsync();
        }
        foreach(var path in new[]{"/api/v1/applications",$"/api/v1/applications/{api.Application.Id}/overview"})
        {
            using var response=await api.Client.GetAsync(path);response.EnsureSuccessStatusCode();var json=await response.Content.ReadFromJsonAsync<JsonElement>();var summary=path.EndsWith("overview",StringComparison.Ordinal)?json.GetProperty("summary"):json.GetProperty("items")[0].GetProperty("summary");
            Assert.Equal(2,summary.GetProperty("visibleActiveApiCount").GetInt32());Assert.Equal(1,summary.GetProperty("availableCredentials").GetInt32());Assert.Equal(4,summary.GetProperty("totalCredentials").GetInt32());Assert.Equal("WorkingConfiguration",summary.GetProperty("activation").GetString());
        }
    }
    [Fact]
    public async Task App_read_does_not_grant_metrics_audit_logs_or_traces()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();using var login=await api.LoginAsync();await using(var db=api.Context()){var p=await db.Set<Permission>().Where(x=>x.Code=="audit.read").Select(x=>x.Id).SingleAsync();await db.Set<RolePermission>().Where(x=>x.PermissionId==p).ExecuteDeleteAsync();}
        using(var overview=await api.Client.GetAsync($"/api/v1/applications/{api.Application.Id}/overview"))overview.EnsureSuccessStatusCode();
        using(var audit=await api.Client.GetAsync($"/api/v1/applications/{api.Application.Id}/audit"))Assert.Equal(HttpStatusCode.Forbidden,audit.StatusCode);
        var scope=$"organizationId={api.Organization.Id}&projectId={api.Project.Id}&environmentId={api.Environment.Id}&applicationId={api.Application.Id}";
        foreach(var route in new[]{"metrics","logs","traces"}){using var response=await api.Client.GetAsync($"/api/v1/observability/{route}?{scope}");Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);}
    }
}

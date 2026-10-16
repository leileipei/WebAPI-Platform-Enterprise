using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ApplicationAuditScopeTests
{
    [Fact]
    public async Task Audit_resource_filter_cannot_be_supplied_by_browser()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();using var login=await api.LoginAsync();var credential=new ApplicationCredential{ApplicationId=api.Application.Id,AccessKey="audit",SecretHash=new string('a',64),SecretLast4="1234",ValidFrom=DateTimeOffset.UtcNow.AddDays(-1),ExpiresAt=DateTimeOffset.UtcNow.AddDays(1)};
        var grant=new ApplicationApiPermission{ApplicationId=api.Application.Id,ApiId=api.Api.Id,EnvironmentId=api.Environment.Id,ValidFrom=DateTimeOffset.UtcNow.AddDays(-1),CreatedBy=api.User.Id};var foreign=Guid.NewGuid().ToString();
        await using(var db=api.Context())
        {
            db.AddRange(credential,grant);
            foreach(var (kind,id,env) in new[]{("ApplicationRecord",api.Application.Id.ToString(),(Guid?)null),("ApplicationCredential",credential.Id.ToString(),(Guid?)null),("ApplicationApiPermission",grant.Id.ToString(),(Guid?)api.Environment.Id),("ApplicationRecord",foreign,(Guid?)null)})db.Add(new AuditLog{OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,EnvironmentId=env,UserId=api.User.Id,ResourceType=kind,ResourceId=id,Action="fixture.audit",AfterJson="[]"});
            await db.SaveChangesAsync();
        }
        var allowed=new[]{api.Application.Id.ToString(),credential.Id.ToString(),grant.Id.ToString()};
        foreach(var suffix in new[]{"","&resourceId="+foreign,"&traceId=unrelated"})
        {
            using var response=await api.Client.GetAsync($"/api/v1/applications/{api.Application.Id}/audit?page=1&pageSize=50"+suffix);response.EnsureSuccessStatusCode();var json=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(3,json.GetProperty("total").GetInt32());Assert.All(json.GetProperty("items").EnumerateArray(),x=>Assert.Contains(x.GetProperty("resourceId").GetString(),allowed));
        }
        await using(var db=api.Context()){await db.Set<UserProjectScope>().ExecuteUpdateAsync(s=>s.SetProperty(x=>x.ProjectId,api.Project.Id).SetProperty(x=>x.EnvironmentId,api.Environment.Id));}
        using var denied=await api.Client.GetAsync($"/api/v1/applications/{api.Application.Id}/audit");Assert.False(denied.IsSuccessStatusCode);
    }
}

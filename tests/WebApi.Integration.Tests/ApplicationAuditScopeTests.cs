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
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();using var login=await api.LoginAsync();var credential=new ApplicationCredential{ApplicationId=api.Application.Id,OrganizationId=api.Organization.Id,AccessKey="audit",SecretHash=new string('a',64),SecretLast4="1234",ValidFrom=DateTimeOffset.UtcNow.AddDays(-1),ExpiresAt=DateTimeOffset.UtcNow.AddDays(1)};
        var grant=new ApplicationApiPermission{ApplicationId=api.Application.Id,ApiId=api.Api.Id,EnvironmentId=api.Environment.Id,Mode="LegacyApi",ValidFrom=DateTimeOffset.UtcNow.AddDays(-1),CreatedBy=api.User.Id};var foreign=Guid.NewGuid().ToString();
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
    [Fact]
    public async Task Security_operations_are_linked_only_to_the_owning_application_before_paging()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();using var login=await api.LoginAsync();
        var foreignApp=new ApplicationRecord{OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,Code="OTHER",Name="其他",Owner="测试"};
        var hiddenOrg=new Organization{Code="HIDDEN",Name="不可见组织"};
        var hiddenProject=new Project{OrganizationId=hiddenOrg.Id,Code="P",Name="不可见项目"};
        var hiddenEnv=new EnvironmentRecord{ProjectId=hiddenProject.Id,Code="E",Name="不可见环境"};
        var now=DateTimeOffset.UtcNow;
        ApplicationCredential Key(Guid app,string access)=>new(){ApplicationId=app,OrganizationId=api.Organization.Id,AccessKey=access,SecretHash=new string('a',64),SecretLast4="1234",ValidFrom=now.AddDays(-1),ExpiresAt=now.AddDays(1)};
        var old=Key(api.Application.Id,"audit-old");var next=Key(api.Application.Id,"audit-next");var other=Key(foreignApp.Id,"audit-foreign");
        var rotation=new FormalCredentialRotation{Id=Guid.NewGuid(),CredentialId=old.Id,ReplacementCredentialId=next.Id,EnvironmentId=api.Environment.Id,HardDeadline=now.AddDays(1),RecordedAt=now,IntentHash=new string('a',64)};
        AuditLog Audit(string type,Guid resource,string action, bool hidden=false)=>new(){UserId=api.User.Id,OrganizationId=hidden?hiddenOrg.Id:api.Organization.Id,ProjectId=hidden?hiddenProject.Id:api.Project.Id,EnvironmentId=hidden?hiddenEnv.Id:api.Environment.Id,ResourceType=type,ResourceId=resource.ToString(),Action=action,AfterJson="{}"};
        await using(var db=api.Context()){
            db.AddRange(foreignApp,hiddenOrg,hiddenProject,hiddenEnv,old,next,other,rotation);
            db.AddRange(Audit("credential",old.Id,"credential.revoke"),Audit("credential",old.Id,"credential.formal-rotation.confirm"),
                Audit("credential-rotation",rotation.Id,"credential.formal-rotation.complete"),Audit("credential",other.Id,"credential.revoke"),
                Audit("credential",old.Id,"credential.revoke",hidden:true));
            await db.SaveChangesAsync();
        }
        var actions=new HashSet<string>();
        for(var page=1;page<=3;page++){
            var result=await api.Client.GetFromJsonAsync<JsonElement>($"/api/v1/applications/{api.Application.Id}/audit?page={page}&pageSize=1");
            Assert.Equal(3,result.GetProperty("total").GetInt32());var row=Assert.Single(result.GetProperty("items").EnumerateArray());
            Assert.Equal(api.Environment.Id,row.GetProperty("environmentId").GetGuid());Assert.True(actions.Add(row.GetProperty("action").GetString()!));
        }
    }
}

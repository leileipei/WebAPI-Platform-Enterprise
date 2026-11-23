using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceDirectoryWorkflowTests
{
    [Fact]
    public async Task Platform_user_detail_summaries_and_project_handover_use_real_revisions()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);
        using var login=await api.LoginAsync();
        var user=await api.Client.GetFromJsonAsync<JsonElement>($"/api/v1/users/{api.User.Id}");
        Assert.Equal(api.User.Id,user.GetProperty("id").GetGuid());
        var directory=await api.Client.GetFromJsonAsync<JsonElement>("/api/v1/users");
        var listed=Assert.Single(directory.GetProperty("items").EnumerateArray());
        Assert.Equal(1,listed.GetProperty("scopeCount").GetInt32());
        var reader=new UserRecord{Username="project.reader",DisplayName="成员",SecurityStamp=Guid.NewGuid().ToString("N")};
        await using(var db=api.Context()){db.AddRange(reader,new UserProjectScope{UserId=reader.Id,OrganizationId=api.Organization.Id,ProjectId=api.Project.Id,AccessMode="read"});await db.SaveChangesAsync();}
        var owner=$"/api/v1/projects/{api.Project.Id}/owner";
        using var saved=await api.WriteAsync(HttpMethod.Put,owner,new{userId=reader.Id},"\"1\"");saved.EnsureSuccessStatusCode();
        var updated=await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reader.Id,updated.GetProperty("ownerUserId").GetGuid());
        await using(var db=api.Context())Assert.Equal("read",(await db.Set<UserProjectScope>().SingleAsync(g=>g.UserId==reader.Id)).AccessMode);
        using var stale=await api.WriteAsync(HttpMethod.Put,owner,new{userId=reader.Id},"\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        using var unknown=await api.WriteAsync(HttpMethod.Put,owner,new{userId=Guid.NewGuid()},saved.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,unknown.StatusCode);
    }

    [Fact]
    public async Task Optional_application_organization_filter_precedes_count_and_pagination()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedConsumerAsync();
        var other=new Organization{Code="SECOND",Name="另一组织"};
        await using(var db=api.Context()){
            var role=await db.Set<Role>().SingleAsync();role.OrganizationId=null;
            var application=new ApplicationRecord{OrganizationId=other.Id,Code="AAAA",Name="另一应用",Owner="测试"};
            db.AddRange(other,application,new UserProjectScope{UserId=api.User.Id,OrganizationId=other.Id,AccessMode="read_write"});
            await db.SaveChangesAsync();
        }
        using var login=await api.LoginAsync();
        var page=await api.Client.GetFromJsonAsync<JsonElement>($"/api/v1/applications?organizationId={api.Organization.Id}&pageSize=1");
        Assert.Equal(1,page.GetProperty("total").GetInt32());
        Assert.Equal(api.Application.Id,Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var legacy=await api.Client.GetFromJsonAsync<JsonElement>("/api/v1/applications?pageSize=1");
        Assert.Equal(2,legacy.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Role_copy_checks_source_revision_copies_permissions_and_excludes_members()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);
        using var login=await api.LoginAsync();var org=api.Organization.Id;
        using var created=await api.WriteAsync(HttpMethod.Post,"/api/v1/roles",new{code="Custom",name="来源",organizationId=org});
        created.EnsureSuccessStatusCode();var role=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var assigned=await api.WriteAsync(HttpMethod.Put,$"/api/v1/roles/{role}/permissions",new{permissions=new[]{"project.read"}},created.Headers.ETag!.Tag);assigned.EnsureSuccessStatusCode();
        var copy=$"/api/v1/roles/{role}/copy";var input=new{code="Copied",name="新角色",organizationId=org};
        using var stale=await api.WriteAsync(HttpMethod.Post,copy,input,created.Headers.ETag.Tag);Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        using var duplicated=await api.WriteAsync(HttpMethod.Post,copy,input,assigned.Headers.ETag!.Tag);duplicated.EnsureSuccessStatusCode();
        var roles=await api.Client.GetFromJsonAsync<JsonElement[]>($"/api/v1/roles?organizationId={org}");var result=Assert.Single(roles!,r=>r.GetProperty("code").GetString()=="Copied");
        Assert.Equal(0,result.GetProperty("userCount").GetInt32());
        Assert.Equal("project.read",Assert.Single(result.GetProperty("permissions").EnumerateArray()).GetString());
        using var removed=await api.WriteAsync(HttpMethod.Delete,$"/api/v1/roles/{result.GetProperty("id").GetGuid()}",null,duplicated.Headers.ETag!.Tag);removed.EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task Administrator_handover_is_organization_local_and_keeps_the_last_ready_administrator()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedScopeAsync(platformAdmin:true);
        var other=new Organization{Code="EXTERNAL",Name="其他组织"};
        var adminRole=new Role{Code="OrganizationAdmin",Name="组织管理员",IsSystem=true};
        var target=new UserRecord{Username="successor",DisplayName="接任者",PasswordHash=api.User.PasswordHash,SecurityStamp=Guid.NewGuid().ToString("N")};
        await using(var db=api.Context()){
            db.AddRange(other,adminRole,target,new Role{Code="OrganizationAdmin",OrganizationId=api.Organization.Id,Name="已有自定义角色"},new UserRole{UserId=api.User.Id,RoleId=adminRole.Id},
                new UserProjectScope{OrganizationId=other.Id,UserId=target.Id,AccessMode="read_write"});
            var write=await db.Set<Permission>().SingleAsync(p=>p.Code=="project.write");db.Add(new RolePermission{RoleId=adminRole.Id,PermissionId=write.Id});await db.SaveChangesAsync();
        }
        using var login=await api.LoginAsync();var path=$"/api/v1/organizations/{api.Organization.Id}/administrators";
        var current=await api.Client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(api.User.Id,Assert.Single(current.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var last=await api.WriteAsync(HttpMethod.Put,path+"/"+api.User.Id,new{enabled=false},"\"1\"");Assert.Equal(HttpStatusCode.Conflict,last.StatusCode);
        var candidates=await api.Client.GetFromJsonAsync<JsonElement>(path+"?candidates=true&search=successor");
        Assert.Equal(target.Id,Assert.Single(candidates.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var added=await api.WriteAsync(HttpMethod.Put,path+"/"+target.Id,new{enabled=true},"\"1\"");added.EnsureSuccessStatusCode();
        using var stale=await api.WriteAsync(HttpMethod.Put,path+"/"+target.Id,new{enabled=false},"\"1\"");Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
        var both=await api.Client.GetFromJsonAsync<JsonElement>(path);Assert.Equal(2,both.GetProperty("total").GetInt32());
        using var handed=await api.WriteAsync(HttpMethod.Put,path+"/"+api.User.Id,new{enabled=false},"\"1\"");handed.EnsureSuccessStatusCode();
        await using var check=api.Context();
        var role=await (from member in check.Set<UserRole>() join r in check.Set<Role>() on member.RoleId equals r.Id where member.UserId==target.Id select r).SingleAsync();
        Assert.Equal(api.Organization.Id,role.OrganizationId);Assert.True(role.IsSystem);Assert.Equal("_OrganizationAdmin",role.Code);
        Assert.True(await check.Set<Role>().AnyAsync(r=>r.OrganizationId==api.Organization.Id && r.Code=="OrganizationAdmin" && !r.IsSystem));
        Assert.True(await check.Set<RolePermission>().AnyAsync(x=>x.RoleId==role.Id));
        Assert.True(await check.Set<UserProjectScope>().AnyAsync(g=>g.UserId==api.User.Id&&g.OrganizationId==api.Organization.Id&&g.AccessMode=="read"));
        Assert.True(await check.Set<UserProjectScope>().AnyAsync(g=>g.UserId==target.Id&&g.OrganizationId==other.Id&&g.AccessMode=="read_write"));
    }

    [Fact]
    public async Task Rotation_release_choices_return_an_older_success_before_paging_and_identify_the_applicant()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();
        var requester=new UserRecord{Username="other.requester",DisplayName="另一位申请人",SecurityStamp=Guid.NewGuid().ToString("N")};
        var now=DateTimeOffset.UtcNow;
        ReleaseRecord Row(int n,string status)=>new(){EnvironmentId=api.Environment.Id,ReleaseNo=$"ROT-{n}",Status=status,
            ReleaseType="publish",RequestedBy=requester.Id,CreatedAt=now.AddMinutes(n),ToConfigVersion=1};
        await using(var db=api.Context()){
            db.Add(requester);db.Add(Row(0,"Succeeded"));db.AddRange(Enumerable.Range(1,50).Select(n=>Row(n,"Draft")));await db.SaveChangesAsync();
        }
        using var login=await api.LoginAsync();
        var list=await api.Client.GetFromJsonAsync<JsonElement>($"/api/v1/environments/{api.Environment.Id}/releases?successfulOnly=true&page=1&pageSize=50");
        Assert.Equal(1,list.GetProperty("total").GetInt32());var release=Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("ROT-0",release.GetProperty("releaseNo").GetString());Assert.Equal("另一位申请人",release.GetProperty("applicantDisplayName").GetString());
    }
    [Fact]
    public async Task Frozen_organization_admin_step_accepts_only_the_managed_local_role()
    {
        await using var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();using var login=await api.LoginAsync();
        var reviewer=await api.NewReviewerAsync("_OrganizationAdmin");
        await using(var db=api.Context()){
            var step=await db.Set<ApprovalStep>().SingleAsync(s=>s.FlowId==api.ApprovalFlow.Id && s.StepOrder==1);step.RoleCode="OrganizationAdmin";
            await db.SaveChangesAsync();
        }
        var release=await api.CreateSubmittedReleaseAsync();
        async Task<bool> MayApprove(){
            var dto=await reviewer.Client.GetFromJsonAsync<JsonElement>($"/api/v1/releases/{release}");return dto.GetProperty("approvalEligibility").GetProperty("canAct").GetBoolean();
        }
        Assert.False(await MayApprove());
        await using(var db=api.Context()){
            var role=await db.Set<Role>().SingleAsync(r=>r.OrganizationId==api.Organization.Id&&r.Code=="_OrganizationAdmin");role.IsSystem=true;await db.SaveChangesAsync();
        }
        Assert.True(await MayApprove());
    }
}

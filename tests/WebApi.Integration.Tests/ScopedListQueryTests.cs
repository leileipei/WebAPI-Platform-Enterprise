using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Policies;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ScopedListQueryTests
{
    private sealed class DataSqlCapture : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if(command.CommandText.Contains("FROM applications ")||command.CommandText.Contains("FROM policies "))Count++;return ValueTask.FromResult(result); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ApiFixture Api=new(); internal readonly DataSqlCapture Capture=new(); internal readonly RequestSqlProbe Probe=new();
        internal readonly Dictionary<string,ActorContext> Actors=[]; internal readonly List<ApplicationRecord> Applications=[]; internal readonly List<Policy> Policies=[];
        internal readonly Project OtherProject=new(){Code="OTHER",Name="其他项目"}; internal readonly Project DisabledProject=new(){Code="DISABLED",Name="停用项目",Status="Disabled"};
        internal readonly EnvironmentRecord DisabledEnvironment=new(){Code="DISABLED",Name="停用环境",Status="Disabled"};
        internal async Task InitializeAsync()
        {
            await Api.InitializeAsync(b=>b.Services.AddDbContext<WebApiDbContext>(o=>o.AddInterceptors(Capture,Probe)));await Api.SeedConsumerAsync();await using var db=Api.Context();
            var otherOrg=new Organization {Code="OTHER",Name="其他组织"};var otherOrgProject=new Project{OrganizationId=otherOrg.Id,Code="OTHER",Name="其他组织项目"};
            OtherProject.OrganizationId=DisabledProject.OrganizationId=Api.Organization.Id;DisabledEnvironment.ProjectId=Api.Project.Id;db.AddRange(otherOrg,otherOrgProject,OtherProject,DisabledProject,DisabledEnvironment);
            var roleId=await db.Set<UserRole>().Where(r=>r.UserId==Api.User.Id).Select(r=>r.RoleId).SingleAsync();var policyRead=new Permission {Code="policy.read",Module="policy",Name="策略读取"};db.Add(policyRead);db.Add(new RolePermission{RoleId=roleId,PermissionId=policyRead.Id});
            Applications.Add(Api.Application);
            for(var i=0;i<150;i++){var app=new ApplicationRecord{OrganizationId=Api.Organization.Id,ProjectId=i<79?Api.Project.Id:null,Code="A"+i.ToString("D4"),Name="同名应用",Owner="测试"};Applications.Add(app);db.Add(app);}
            for(var i=0;i<151;i++){var policy=new Policy{OrganizationId=Api.Organization.Id,ProjectId=i<75?null:Api.Project.Id,Name="同名策略",Type=i%2==0?"timeout":"authentication",Config=i%2==0?"{\"timeoutMs\":2000}":"{\"mode\":\"ApiKey\"}",Enabled=i%3!=0};Policies.Add(policy);db.Add(policy);}
            foreach(var project in new[]{OtherProject,DisabledProject,otherOrgProject})for(var i=0;i<151;i++)
            {db.Add(new ApplicationRecord{OrganizationId=project.OrganizationId,ProjectId=project.Id,Code=project.Code+project.Id.ToString("N")[..6]+i.ToString("D4"),Name="隐藏应用",Owner="测试"});db.Add(new Policy{OrganizationId=project.OrganizationId,ProjectId=project.Id,Name="隐藏策略",Type="timeout",Config="{\"timeoutMs\":2000}"});}
            var appRead=await db.Set<Permission>().SingleAsync(p=>p.Code=="app.read");
            void AddActor(string name, Guid? project, Guid? environment, bool grant=true, bool function=true, bool inactive=false)
            {
                var user=new UserRecord{Username="scoped-"+name,DisplayName=name,SecurityStamp=Guid.NewGuid().ToString("N"),Status=inactive?"Disabled":"Active"};db.Add(user);Actors[name]=new(user.Id,name);
                if(function){var role=new Role{OrganizationId=Api.Organization.Id,Code="scoped-"+name,Name=name};db.Add(role);db.Add(new UserRole{UserId=user.Id,RoleId=role.Id});db.AddRange(new RolePermission{RoleId=role.Id,PermissionId=policyRead.Id},new RolePermission{RoleId=role.Id,PermissionId=appRead.Id});}
                if(grant)db.Add(new UserProjectScope{UserId=user.Id,OrganizationId=Api.Organization.Id,ProjectId=project,EnvironmentId=environment,AccessMode="read"});
            }
            Actors["organization"]=new(Api.User.Id,"organization");AddActor("project",Api.Project.Id,null);AddActor("environment",Api.Project.Id,Api.Environment.Id);
            AddActor("disabled-project",DisabledProject.Id,null);AddActor("disabled-environment",Api.Project.Id,DisabledEnvironment.Id);AddActor("no-scope",null,null,false);AddActor("no-function",null,null,true,false);AddActor("inactive",null,null,true,true,true);
            // Having an additional data grant must not broaden an organization-limited role.
            db.Add(new UserProjectScope{UserId=Api.User.Id,OrganizationId=otherOrg.Id,AccessMode="read_write"});await db.SaveChangesAsync();
        }
        internal async Task<T> Measure<T>(Func<IServiceProvider,Task<T>> action,int maximumTotal=30,int? maximumRows=null)
        {
            Capture.Count=0;var id=Guid.NewGuid().ToString("N");using var probe=Probe.Begin(id);using var services=Api.Services();var result=await action(services.ServiceProvider);var stats=Probe.Read(id);
            Assert.InRange(Capture.Count,1,2);Assert.True(stats.Total<=maximumTotal,$"total SQL {stats.Total} > {maximumTotal}");if(maximumRows is int rows)Assert.True(stats.Rows<=rows,$"consumed rows {stats.Rows} > {rows}");return result;
        }
        public ValueTask DisposeAsync()=>Api.DisposeAsync();
    }
    [Fact]
    public async Task ApplicationVisibleTotalIsFilteredBeforePaging()
    {
        await using var f=new Fixture();await f.InitializeAsync();
        foreach(var (name,total) in new[]{("organization",302),("project",80),("environment",0),("disabled-project",0),("disabled-environment",0),("no-scope",0),("no-function",0)})
        {
            var expected=f.Applications.Where(a=>name=="organization"||name=="project"&&a.ProjectId==f.Api.Project.Id).OrderBy(a=>a.Code,StringComparer.Ordinal).ThenBy(a=>a.Id.ToString("D"),StringComparer.Ordinal).Select(a=>a.Id).ToArray();
            // Organization visibility includes the additional active project, but never disabled or foreign org rows.
            if(name=="organization"){await using var db=f.Api.Context();expected=await db.Set<ApplicationRecord>().Where(a=>a.OrganizationId==f.Api.Organization.Id&&a.ProjectId!=f.DisabledProject.Id).OrderBy(a=>a.Code).ThenBy(a=>a.Id).Select(a=>a.Id).ToArrayAsync();Assert.Equal(302,expected.Length);}
            foreach(var size in new[]{1,50,100})foreach(var page in new[]{1,2,5})
            {
                var count=expected.Skip((page-1)*size).Take(size).Count();var result=await f.Measure(p=>p.GetRequiredService<ApplicationService>().ListAsync(f.Actors[name],page,size),10,count+5);
                Assert.Equal(total,result.Total);Assert.Equal(expected.Skip((page-1)*size).Take(size),result.Items.Select(a=>a.Id));Assert.All(result.Items,a=>Assert.Equal(f.Api.Organization.Id,a.OrganizationId));
            }
        }
        using var services=f.Api.Services();var error=await Assert.ThrowsAsync<ApiException>(()=>services.ServiceProvider.GetRequiredService<ApplicationService>().ListAsync(f.Actors["inactive"],1,50));Assert.Equal(401,error.Status);
    }
    [Fact]
    public async Task OrganizationPolicySharesOnlyWithAuthorizedActiveDescendants()
    {
        await using var f=new Fixture();await f.InitializeAsync();
        foreach(var(name,total)in new[]{("organization",302),("project",151),("environment",75)})
        {
            foreach(var size in new[]{1,50,100})
            {
                var result=await f.Measure(p=>p.GetRequiredService<PolicyService>().ListAsync(new(f.Api.Organization.Id),f.Actors[name],1,size,null,null,null),30,size+20);
                Assert.Equal(total,result.Total);Assert.All(result.Items,p=>Assert.Equal(f.Api.Organization.Id,p.OrganizationId));if(name=="environment")Assert.All(result.Items,p=>Assert.Null(p.ProjectId));
                if(name!="organization")Assert.Equal(f.Policies.Where(p=>name=="project"||p.ProjectId==null).OrderBy(p=>p.Name,StringComparer.Ordinal).ThenBy(p=>p.Id.ToString("D"),StringComparer.Ordinal).Take(size).Select(p=>p.Id),result.Items.Select(p=>p.Id));
            }
        }
        using var services=f.Api.Services();var service=services.ServiceProvider.GetRequiredService<PolicyService>();
        foreach(var name in new[]{"no-scope","no-function","disabled-project","disabled-environment"})Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id),f.Actors[name],1,50,null,null,null))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id,Guid.NewGuid()),f.Actors["organization"],1,50,null,null,null))).Status);
        Assert.Equal(401,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id),f.Actors["inactive"],1,50,null,null,null))).Status);
    }
    [Fact]
    public async Task ProjectPolicyIsNotVisibleWithEnvironmentOnlyGrant()
    {
        await using var f=new Fixture();await f.InitializeAsync();using var services=f.Api.Services();var service=services.ServiceProvider.GetRequiredService<PolicyService>();var actor=f.Actors["environment"];
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id,f.Api.Project.Id),actor,1,50,null,null,null))).Status);
        Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.GetAsync(f.Policies.First(p=>p.ProjectId!=null).Id,actor))).Status);
        Assert.Equal(75,(await service.ListAsync(new(f.Api.Organization.Id),actor,1,100,null,null,null)).Total);
    }
    [Fact]
    public async Task PolicyFiltersStablePagingAndRevocationRemainUnchanged()
    {
        await using var f=new Fixture();await f.InitializeAsync();var actor=f.Actors["project"];var expected=f.Policies.Where(p=>p.Type=="timeout"&&p.Enabled).OrderBy(p=>p.Id.ToString("D"),StringComparer.Ordinal).Select(p=>p.Id).ToArray();
        foreach(var page in new[]{1,2,3}){var result=await f.Measure(p=>p.GetRequiredService<PolicyService>().ListAsync(new(f.Api.Organization.Id),actor,page,20,"timeout","同名",true));Assert.Equal(expected.Length,result.Total);Assert.Equal(expected.Skip((page-1)*20).Take(20),result.Items.Select(p=>p.Id));}
        using var services=f.Api.Services();var service=services.ServiceProvider.GetRequiredService<PolicyService>();Assert.Equal(422,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id),actor,1,50,"bad",null,null))).Status);
        await using var db=f.Api.Context();await db.Set<UserProjectScope>().Where(s=>s.UserId==actor.UserId).ExecuteDeleteAsync();Assert.Equal(404,(await Assert.ThrowsAsync<ApiException>(()=>service.ListAsync(new(f.Api.Organization.Id),actor,1,50,null,null,null))).Status);
        Assert.Empty((await services.ServiceProvider.GetRequiredService<ApplicationService>().ListAsync(actor,1,50)).Items);
    }
}

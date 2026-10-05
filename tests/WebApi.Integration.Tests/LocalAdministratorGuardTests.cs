using System.Net;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Governance;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class LocalAdministratorGuardTests
{
    [Fact] public async Task LastLocalAdminIsProtectedEvenWithSsoAdmin()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();
        await using(var db=f.Api.Context()){
            var role=await db.Set<Role>().SingleAsync();var sso=new UserRecord{Username="sso_admin",DisplayName="SSO 管理员",AuthSource="sso",SecurityStamp=Guid.NewGuid().ToString("N")};db.Add(sso);db.Add(new UserRole{UserId=sso.Id,RoleId=role.Id});await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict,(await f.Api.WriteAsync(HttpMethod.Put,$"/api/v1/users/{f.Api.User.Id}",new UpdateUserRequest(f.Api.User.DisplayName,"Disabled"),"\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Api.WriteAsync(HttpMethod.Put,$"/api/v1/users/{f.Api.User.Id}/roles",new AssignRolesRequest([]),"\"1\"")).StatusCode);
        await using var context=f.Api.Context();var local=await context.Set<UserRecord>().SingleAsync(x=>x.Id==f.Api.User.Id);Assert.Equal("Active",local.Status);Assert.Equal(f.Api.User.PasswordHash,local.PasswordHash);
    }
    [Fact] public async Task ConcurrentRemovalKeepsOneAvailableLocalAdministrator()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();
        var first=new UserRecord{Username="local_a",DisplayName="本地A",PasswordHash=f.Api.User.PasswordHash,SecurityStamp=Guid.NewGuid().ToString("N")};
        var second=new UserRecord{Username="local_b",DisplayName="本地B",PasswordHash=f.Api.User.PasswordHash,SecurityStamp=Guid.NewGuid().ToString("N")};
        await using(var db=f.Api.Context()){
            var actor=await db.Set<UserRecord>().SingleAsync();actor.AuthSource="sso";actor.PasswordHash=null;
            var role=await db.Set<Role>().SingleAsync();db.AddRange(first,second);db.AddRange(new UserRole{UserId=first.Id,RoleId=role.Id},new UserRole{UserId=second.Id,RoleId=role.Id});await db.SaveChangesAsync();
        }
        var results=await Task.WhenAll(f.Api.WriteAsync(HttpMethod.Put,$"/api/v1/users/{first.Id}",new UpdateUserRequest(first.DisplayName,"Disabled"),"\"1\""),f.Api.WriteAsync(HttpMethod.Put,$"/api/v1/users/{second.Id}",new UpdateUserRequest(second.DisplayName,"Disabled"),"\"1\""));
        Assert.Single(results,response=>response.StatusCode==HttpStatusCode.OK);Assert.Single(results,response=>response.StatusCode==HttpStatusCode.Conflict);foreach(var response in results)response.Dispose();
        await using var context=f.Api.Context();Assert.Equal(1,await context.Set<UserRecord>().CountAsync(x=>x.Status=="Active"&&x.AuthSource=="local"&&x.PasswordHash!=null));
    }
}

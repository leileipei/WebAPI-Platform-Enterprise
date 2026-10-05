using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Sso;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Sso;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoIdentityAdmissionTests
{
    private static async Task<UserDto> User(SsoFixture f,Guid provider)
    {
        using var response=await f.Api.WriteAsync(HttpMethod.Post,"/api/v1/users/sso",new CreateSsoUserRequest("synthetic","原显示名","original@example.test",provider,"SyntheticSubject"));response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserDto>())!;
    }
    [Theory][InlineData("unknown")][InlineData("issuer")][InlineData("noScope")][InlineData("disabledUser")][InlineData("disabledBinding")]
    public async Task UnknownIdentityAndOrganizationMismatchDenyAdmission(string scenario)
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync(SsoFixture.Body(organizationId:f.Api.Organization.Id)));
        var user=await User(f,provider.Id);
        await using var db=f.Api.Context();if(scenario!="noScope"){db.Add(new UserProjectScope{UserId=user.Id,OrganizationId=f.Api.Organization.Id,AccessMode="read"});await db.SaveChangesAsync();}
        if(scenario=="disabledUser"){var row=await db.Set<UserRecord>().SingleAsync(x=>x.Id==user.Id);row.Status="Disabled";await db.SaveChangesAsync();}
        if(scenario=="disabledBinding"){var binding=await db.Set<UserExternalIdentity>().SingleAsync();binding.Enabled=false;await db.SaveChangesAsync();}
        var service=new SsoLoginCoordinator(db,new(db),f.Time);var pending=await service.BeginAsync(provider.Id,null);await service.ClaimAsync(pending.AttemptId,provider.Id,pending.Provider.Revision);
        var error=await Assert.ThrowsAsync<ApiException>(()=>service.CompleteAsync(pending.AttemptId,new(scenario=="issuer"?"https://other.example/":provider.Issuer,scenario=="unknown"?"UnknownSynthetic":"SyntheticSubject",new Dictionary<string,string>()),"test",null));
        Assert.DoesNotContain("Synthetic",error.Message);Assert.Equal(0,await db.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.login"));
    }
    [Fact] public async Task ClaimsOnlySynchronizePresentProfileStringsAndReadActualTtl()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync());var user=await User(f,provider.Id);
        await using var db=f.Api.Context();db.Add(new SystemSetting{Key="system.security",Value="{\"sessionTtlMinutes\":17,\"passwordMinLength\":16,\"passwordComplexity\":\"LengthOnly\",\"allowedOrigins\":[]}",UpdatedBy=f.Api.User.Id});await db.SaveChangesAsync();
        var service=new SsoLoginCoordinator(db,new(db),f.Time);var pending=await service.BeginAsync(provider.Id,"/apis");await service.ClaimAsync(pending.AttemptId,provider.Id,pending.Provider.Revision);
        var grant=await service.CompleteAsync(pending.AttemptId,new(provider.Issuer,"SyntheticSubject",new Dictionary<string,string>{{"roles","PlatformAdmin"},{"groups","superusers"},{"email","synced@example.test"}}),"test",null);
        Assert.Equal(17,grant.TtlMinutes);Assert.Equal(user.Id,grant.UserId);Assert.Equal("/apis",grant.ReturnPath);
        var row=await db.Set<UserRecord>().AsNoTracking().SingleAsync(x=>x.Id==user.Id);Assert.Equal("原显示名",row.DisplayName);Assert.Equal("synced@example.test",row.Email);
        Assert.Empty(await db.Set<UserRole>().Where(x=>x.UserId==user.Id).ToArrayAsync());Assert.Empty(await db.Set<UserProjectScope>().Where(x=>x.UserId==user.Id).ToArrayAsync());
        await Assert.ThrowsAsync<ApiException>(()=>service.CompleteAsync(pending.AttemptId,new(provider.Issuer,"SyntheticSubject",new Dictionary<string,string>()),"replay",null));
        var audits=await db.Set<AuditLog>().Where(x=>x.Action=="auth.sso.login").ToArrayAsync();Assert.Single(audits);Assert.DoesNotContain("synced@example.test",audits[0].AfterJson);Assert.DoesNotContain("SyntheticSubject",audits[0].AfterJson);
    }
    [Fact] public async Task OverlongMappedProfileAndConcurrentProviderChangeNeverCommitSuccess()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync());await User(f,provider.Id);
        await using var db=f.Api.Context();var service=new SsoLoginCoordinator(db,new(db),f.Time);
        var pending=await service.BeginAsync(provider.Id,null);await service.ClaimAsync(pending.AttemptId,provider.Id,pending.Provider.Revision);
        await Assert.ThrowsAsync<ApiException>(()=>service.CompleteAsync(pending.AttemptId,new(provider.Issuer,"SyntheticSubject",new Dictionary<string,string>{{"name",new string('x',129)}}),"test",null));
        (await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/disable",new{},RevisionTag.Format(provider.Revision))).EnsureSuccessStatusCode();
        await Assert.ThrowsAsync<ApiException>(()=>service.CompleteAsync(pending.AttemptId,new(provider.Issuer,"SyntheticSubject",new Dictionary<string,string>()),"test",null));
        Assert.Equal(0,await db.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.login"));
    }
    [Fact] public async Task OrganizationAdmissionKeepsExplicitRolesAndOtherOrganizationScopes()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync(SsoFixture.Body(organizationId:f.Api.Organization.Id)));var user=await User(f,provider.Id);
        await using var db=f.Api.Context();var other=new Organization{Code="OTHER",Name="其他组织"};db.Add(other);var role=await db.Set<Role>().SingleAsync();
        db.AddRange(new UserRole{UserId=user.Id,RoleId=role.Id},new UserProjectScope{UserId=user.Id,OrganizationId=f.Api.Organization.Id,ProjectId=f.Api.Project.Id,EnvironmentId=f.Api.Environment.Id,AccessMode="read"},new UserProjectScope{UserId=user.Id,OrganizationId=other.Id,AccessMode="read_write"});await db.SaveChangesAsync();
        var service=new SsoLoginCoordinator(db,new(db),f.Time);var pending=await service.BeginAsync(provider.Id,null);await service.ClaimAsync(pending.AttemptId,provider.Id,pending.Provider.Revision);
        var grant=await service.CompleteAsync(pending.AttemptId,new(provider.Issuer,"SyntheticSubject",new Dictionary<string,string>{{"roles","Anything"},{"groups","Everything"}}),"test",null);
        Assert.Equal(user.Id,grant.UserId);Assert.Equal(new[]{role.Id},await db.Set<UserRole>().Where(x=>x.UserId==user.Id).Select(x=>x.RoleId).ToArrayAsync());
        var organizations=await db.Set<UserProjectScope>().Where(x=>x.UserId==user.Id).Select(x=>x.OrganizationId).ToArrayAsync();Assert.Equal(2,organizations.Length);Assert.Contains(other.Id,organizations);
    }
    [Fact] public async Task InactiveProjectCannotSatisfyOrganizationAdmission()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync(SsoFixture.Body(organizationId:f.Api.Organization.Id)));var user=await User(f,provider.Id);
        await using var db=f.Api.Context();db.Add(new UserProjectScope{UserId=user.Id,OrganizationId=f.Api.Organization.Id,ProjectId=f.Api.Project.Id,AccessMode="read"});await db.SaveChangesAsync();
        var service=new SsoLoginCoordinator(db,new(db),f.Time);var pending=await service.BeginAsync(provider.Id,null);await service.ClaimAsync(pending.AttemptId,provider.Id,pending.Provider.Revision);
        await db.Set<Project>().Where(x=>x.Id==f.Api.Project.Id).ExecuteUpdateAsync(update=>update.SetProperty(x=>x.Status,"Disabled"));
        await Assert.ThrowsAsync<ApiException>(()=>service.CompleteAsync(pending.AttemptId,new(provider.Issuer,"SyntheticSubject",new Dictionary<string,string>()),"test",null));
        Assert.Equal(0,await db.Set<AuditLog>().CountAsync(x=>x.Action=="auth.sso.login"));
    }
}

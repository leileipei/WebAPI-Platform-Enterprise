using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Sso;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class SsoProviderCommandTests
{
    [Fact] public async Task GovernanceRequiresRealPlatformAuthority()
    {
        await using var f=new SsoFixture();await f.InitializeAsync(login:false,platform:false);
        Assert.Equal(HttpStatusCode.Unauthorized,(await f.Api.Client.GetAsync("/api/v1/settings/sso/providers")).StatusCode);
        (await f.Api.LoginAsync()).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,(await f.Api.Client.GetAsync("/api/v1/settings/sso/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await f.SendAsync(HttpMethod.Post,$"/{Guid.NewGuid()}/test",new{},"\"1\"")).StatusCode);
        Assert.Equal(0,f.IdentityService.Calls);
        await using var db=f.Api.Context();var role=await db.Set<Role>().SingleAsync();role.OrganizationId=null;await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden,(await f.Api.Client.GetAsync("/api/v1/settings/sso/providers")).StatusCode);
    }
    [Fact] public async Task DefaultRaceAndReplayAreAtomic()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync());
        var suffix=$"/{provider.Id}/default";var tag=RevisionTag.Format(provider.Revision);
        var responses=await Task.WhenAll(f.SendAsync(HttpMethod.Post,suffix,new{},tag),f.SendAsync(HttpMethod.Post,suffix,new{},tag));
        Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.PreconditionFailed);
        foreach(var response in responses)response.Dispose();
        var current=(await f.Api.Client.GetFromJsonAsync<SsoProviderDto>($"/api/v1/settings/sso/providers/{provider.Id}"))!;
        var renamed=SsoFixture.Body("改名");var key=Guid.NewGuid().ToString("N");var currentTag=RevisionTag.Format(current.Revision);
        using var first=await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",renamed,currentTag,key);first.EnsureSuccessStatusCode();var original=await first.Content.ReadAsStringAsync();
        await using var db=f.Api.Context();var audit=await db.Set<AuditLog>().CountAsync();
        using var replay=await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",renamed,currentTag,key);replay.EnsureSuccessStatusCode();Assert.Equal(original,await replay.Content.ReadAsStringAsync());Assert.Equal(audit,await db.Set<AuditLog>().CountAsync());
        Assert.Equal(HttpStatusCode.Conflict,(await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",SsoFixture.Body("不同"),currentTag,key)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed,(await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",renamed)).StatusCode);
        Assert.Equal(1,await db.Set<SsoProvider>().CountAsync(x=>x.IsDefault));
        var logs=await db.Set<AuditLog>().Where(x=>x.Action.StartsWith("settings.sso")).ToArrayAsync();Assert.All(logs,log=>Assert.DoesNotContain("file://sso/enterprise",log.AfterJson??""));
        Assert.All(logs,log=>Assert.Equal(nameof(SsoProvider),log.ResourceType));
    }
    [Fact] public async Task NameAndDefaultOnlyChangeRevision()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync());
        using var rename=await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",SsoFixture.Body("新名称"),RevisionTag.Format(provider.Revision));rename.EnsureSuccessStatusCode();
        var renamed=(await rename.Content.ReadFromJsonAsync<SsoProviderDto>())!;Assert.Equal(provider.AuthRevision,renamed.AuthRevision);Assert.Equal(provider.Revision+1,renamed.Revision);
        using var select=await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/default",new{},RevisionTag.Format(renamed.Revision));select.EnsureSuccessStatusCode();
        var selected=(await select.Content.ReadFromJsonAsync<SsoProviderDto>())!;Assert.Equal(renamed.AuthRevision,selected.AuthRevision);
        using var disable=await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/disable",new{},RevisionTag.Format(selected.Revision));disable.EnsureSuccessStatusCode();
        var disabled=(await disable.Content.ReadFromJsonAsync<SsoProviderDto>())!;Assert.False(disabled.IsDefault);Assert.Equal(selected.AuthRevision+1,disabled.AuthRevision);
    }
    [Fact] public async Task BoundIssuerAndClientScopeCannotChange()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();
        await using(var db=f.Api.Context()){db.Add(new UserExternalIdentity{UserId=f.Api.User.Id,ProviderId=provider.Id,Issuer=provider.Issuer,Subject="synthetic"});await db.SaveChangesAsync();}
        Assert.Equal(HttpStatusCode.Conflict,(await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",SsoFixture.Body(issuer:"https://id.example.test/other"),"\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",SsoFixture.Body() with{ClientId="other"},"\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",SsoFixture.Body(organizationId:f.Api.Organization.Id),"\"1\"")).StatusCode);
    }
    [Fact] public async Task DefaultSwitchAuditsBothRowsAndKeepsOrganizationDefaultIndependent()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();
        var first=await f.EnableAsync(await f.CreateAsync(SsoFixture.Body("第一源")));
        var second=await f.EnableAsync(await f.CreateAsync(SsoFixture.Body("第二源")));
        var org=await f.EnableAsync(await f.CreateAsync(SsoFixture.Body("组织源",f.Api.Organization.Id)));
        (await f.SendAsync(HttpMethod.Post,$"/{first.Id}/default",new{},RevisionTag.Format(first.Revision))).EnsureSuccessStatusCode();
        (await f.SendAsync(HttpMethod.Post,$"/{org.Id}/default",new{},RevisionTag.Format(org.Revision))).EnsureSuccessStatusCode();
        (await f.SendAsync(HttpMethod.Post,$"/{second.Id}/default",new{},RevisionTag.Format(second.Revision))).EnsureSuccessStatusCode();
        await using var db=f.Api.Context();Assert.Equal(2,await db.Set<SsoProvider>().CountAsync(x=>x.IsDefault));
        var a=await db.Set<SsoProvider>().SingleAsync(x=>x.Id==first.Id);var b=await db.Set<SsoProvider>().SingleAsync(x=>x.Id==second.Id);
        Assert.False(a.IsDefault);Assert.True(b.IsDefault);Assert.Equal(first.AuthRevision,a.AuthRevision);
        var audits=await db.Set<AuditLog>().Where(x=>x.Action=="settings.sso.provider.default").ToArrayAsync();
        Assert.Contains(audits,log=>(log.AfterJson??"").Contains(first.Id.ToString())&&(log.AfterJson??"").Contains(second.Id.ToString()));
    }
    [Fact] public async Task RevokedActorCannotReplayAndClientChangeBumpsAuthRevision()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.CreateAsync();var key=Guid.NewGuid().ToString("N");
        var body=SsoFixture.Body() with{ClientId="changed-client"};
        using var first=await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",body,"\"1\"",key);first.EnsureSuccessStatusCode();
        Assert.Equal(provider.AuthRevision+1,(await first.Content.ReadFromJsonAsync<SsoProviderDto>())!.AuthRevision);
        await using(var db=f.Api.Context()){var permission=await db.Set<Permission>().SingleAsync(x=>x.Code=="system.sso.manage");db.RemoveRange(await db.Set<RolePermission>().Where(x=>x.PermissionId==permission.Id).ToArrayAsync());await db.SaveChangesAsync();}
        Assert.Equal(HttpStatusCode.Forbidden,(await f.SendAsync(HttpMethod.Put,$"/{provider.Id}",body,"\"1\"",key)).StatusCode);
    }
    [Fact] public async Task ExplicitSecretRotationInvalidatesAuthRevisionWithoutRenamingOrChangingReference()
    {
        await using var f=new SsoFixture();await f.InitializeAsync();var provider=await f.EnableAsync(await f.CreateAsync());
        using var rotate=await f.SendAsync(HttpMethod.Post,$"/{provider.Id}/rotate",new{},RevisionTag.Format(provider.Revision));rotate.EnsureSuccessStatusCode();
        var rotated=(await rotate.Content.ReadFromJsonAsync<SsoProviderDto>())!;
        Assert.Equal(provider.AuthRevision+1,rotated.AuthRevision);Assert.Equal(provider.Revision+1,rotated.Revision);
        Assert.Equal(provider.Name,rotated.Name);Assert.Equal(provider.SecretRef,rotated.SecretRef);
    }
}

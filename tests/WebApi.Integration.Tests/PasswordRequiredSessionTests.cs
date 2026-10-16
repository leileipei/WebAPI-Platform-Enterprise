using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Infrastructure.Settings;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;
public sealed class PasswordRequiredSessionTests
{
    [Fact]
    public async Task Restricted_identity_has_no_permissions_or_scopes()
    {
        await using var api=new ApiFixture(); api.User.MustChangePassword=true;
        await api.InitializeAsync(); await api.SeedScopeAsync(platformAdmin:true);
        using var login=await api.LoginAsync(); login.EnsureSuccessStatusCode();
        using var me=await api.Client.GetAsync("/api/v1/auth/me"); me.EnsureSuccessStatusCode();
        var identity=JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement;
        Assert.Empty(identity.GetProperty("permissions").EnumerateArray()); Assert.Empty(identity.GetProperty("scopes").EnumerateArray());
        Assert.True(identity.GetProperty("mustChangePassword").GetBoolean()); Assert.Equal("local",identity.GetProperty("authSource").GetString());
        await using var db=api.Context(); Assert.NotEmpty(await db.Set<UserRole>().Where(x=>x.UserId==api.User.Id).ToArrayAsync());
        Assert.NotEmpty(await db.Set<UserProjectScope>().Where(x=>x.UserId==api.User.Id).ToArrayAsync());
    }

    [Theory]
    [InlineData("/api/v1/organizations")]
    [InlineData("/api/v1/users")]
    [InlineData("/api/v1/audit-logs/export")]
    public async Task Restricted_cookie_cannot_read_or_export(string path)
    {
        await using var api=new ApiFixture(); api.User.MustChangePassword=true;
        await api.InitializeAsync(); await api.SeedScopeAsync(platformAdmin:true); using var login=await api.LoginAsync(); login.EnsureSuccessStatusCode();
        using var result=await api.Client.GetAsync(path); Assert.Equal(HttpStatusCode.Forbidden,result.StatusCode);
        Assert.Equal("password_change_required",JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Restricted_cookie_cannot_write_or_call_services()
    {
        await using var api=new ApiFixture(); api.User.MustChangePassword=true;
        await api.InitializeAsync(); await api.SeedScopeAsync(platformAdmin:true); using var login=await api.LoginAsync(); login.EnsureSuccessStatusCode();
        using var result=await api.WriteAsync(HttpMethod.Post,"/api/v1/organizations",new {code="DENIED",name="不得创建"});
        Assert.Equal(HttpStatusCode.Forbidden,result.StatusCode);
        using var scope=api.Services(); var actor=new ActorContext(api.User.Id,"required-test");
        var authorization=scope.ServiceProvider.GetRequiredService<AuthorizationService>();
        var error=await Assert.ThrowsAsync<ApiException>(()=>authorization.RequireAsync(actor,"organization.read",new("organization",api.Organization.Id,new(api.Organization.Id))));
        Assert.Equal("password_change_required",error.Code);
        var collection=scope.ServiceProvider.GetRequiredService<CollectionAuthorizationQuery>();
        Assert.Empty(await collection.Scopes(actor,"organization.read").ToArrayAsync());
        var collectionError=await Assert.ThrowsAsync<ApiException>(()=>collection.RequireActiveActorAsync(actor)); Assert.Equal("password_change_required",collectionError.Code);
        var exportError=await Assert.ThrowsAsync<ApiException>(()=>scope.ServiceProvider.GetRequiredService<AuditExportService>().ExportAsync(actor,null,null));
        Assert.Equal("password_change_required",exportError.Code);
    }

    [Theory]
    [InlineData(5,5)] [InlineData(30,10)]
    public async Task Restricted_cookie_expires_without_sliding(int configuredMinutes,int expectedMinutes)
    {
        await using var api=new ApiFixture(); api.User.MustChangePassword=true; await api.InitializeAsync();
        await using(var db=api.Context())
        {
            var security=((SecuritySettings)SettingsValueCodec.Default("security")) with {SessionTtlMinutes=configuredMinutes};
            db.Add(new SystemSetting {Key="system.security",Value=SettingsValueCodec.Json(security).GetRawText(),UpdatedBy=api.User.Id}); await db.SaveChangesAsync();
        }
        using var login=await api.LoginAsync(); login.EnsureSuccessStatusCode();
        var header=login.Headers.GetValues("Set-Cookie").Single(x=>x.StartsWith("WebApi.Session=",StringComparison.Ordinal));
        Assert.DoesNotContain("expires=",header,StringComparison.OrdinalIgnoreCase);
        var protectedTicket=header.Split(';')[0]["WebApi.Session=".Length..];
        using var scope=api.Services(); var options=scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket=options.TicketDataFormat.Unprotect(protectedTicket); Assert.NotNull(ticket);
        Assert.False(ticket.Properties.IsPersistent); Assert.False(ticket.Properties.AllowRefresh);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),ticket.Properties.ExpiresUtc-ticket.Properties.IssuedUtc);
    }

    [Fact]
    public async Task Database_flag_overrides_previously_ordinary_cookie()
    {
        await using var api=new ApiFixture(); await api.InitializeAsync(); await api.SeedScopeAsync(platformAdmin:true);
        using var login=await api.LoginAsync(); login.EnsureSuccessStatusCode();
        await using(var db=api.Context()) await db.Set<UserRecord>().Where(x=>x.Id==api.User.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.MustChangePassword,true));
        using var response=await api.Client.GetAsync("/api/v1/organizations"); Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);
        using var me=await api.Client.GetAsync("/api/v1/auth/me"); me.EnsureSuccessStatusCode();
        Assert.Empty(JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("permissions").EnumerateArray());
    }

    [Fact]
    public async Task Restricted_cookie_can_csrf_me_and_logout_and_old_stamp_is_rejected()
    {
        await using var api=new ApiFixture(); api.User.MustChangePassword=true; await api.InitializeAsync();
        using var login=await api.LoginAsync(); login.EnsureSuccessStatusCode(); Assert.NotEqual("missing-token",await api.CsrfAsync());
        using var me=await api.Client.GetAsync("/api/v1/auth/me"); Assert.Equal(HttpStatusCode.OK,me.StatusCode);
        await using(var db=api.Context()) await db.Set<UserRecord>().Where(x=>x.Id==api.User.Id).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.SecurityStamp,Guid.NewGuid().ToString("N")));
        using var invalid=await api.Client.GetAsync("/api/v1/auth/me"); Assert.Equal(HttpStatusCode.Unauthorized,invalid.StatusCode);
        using var relogin=await api.LoginAsync(); relogin.EnsureSuccessStatusCode();
        using var logout=await api.WriteAsync(HttpMethod.Post,"/api/v1/auth/logout"); Assert.Equal(HttpStatusCode.NoContent,logout.StatusCode);
    }
}

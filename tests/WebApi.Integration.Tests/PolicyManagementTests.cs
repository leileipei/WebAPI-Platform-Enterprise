using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PolicyManagementTests
{
    private static SavePolicyRequest Body(string name = "企业认证") => new(name, "authentication", "{\"mode\":\"ApiKey\"}");
    private static async Task<ApiFixture> PrepareAsync()
    {
        var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedCatalogAsync();
        await using var db = api.Context();
        var roleId = await db.Set<UserRole>().Where(r => r.UserId == api.User.Id).Select(r => r.RoleId).SingleAsync();
        foreach (var code in new[] { "policy.read", "policy.write" })
        {
            var permission = new Permission { Code = code, Name = code, Module = "policy" };
            db.Add(permission); db.Add(new RolePermission { RoleId = roleId, PermissionId = permission.Id });
        }
        await db.SaveChangesAsync(); using var login = await api.LoginAsync(); login.EnsureSuccessStatusCode(); return api;
    }
    private static async Task<PolicyDto> CreateAsync(ApiFixture api, bool organization = false)
    {
        using var result = await api.WriteAsync(HttpMethod.Post, organization ? $"/api/v1/organizations/{api.Organization.Id}/policies" : $"/api/v1/projects/{api.Project.Id}/policies", Body());
        result.EnsureSuccessStatusCode(); return (await result.Content.ReadFromJsonAsync<PolicyDto>())!;
    }

    [Fact]
    public async Task Etag428And412()
    {
        await using var api = await PrepareAsync(); var p = await CreateAsync(api);
        using var missing = await api.WriteAsync(HttpMethod.Put, $"/api/v1/policies/{p.Id}", Body("修改"));
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        using var valid = await api.WriteAsync(HttpMethod.Put, $"/api/v1/policies/{p.Id}", Body("修改"), "\"1\""); valid.EnsureSuccessStatusCode();
        using var stale = await api.WriteAsync(HttpMethod.Put, $"/api/v1/policies/{p.Id}", Body("过期修改"), "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        await using var db = api.Context(); var actual = await db.Set<Policy>().SingleAsync(); Assert.Equal("修改", actual.Name); Assert.Equal(2, actual.VersionNo);
        var audit=await db.Set<AuditLog>().Where(a=>a.Action=="policy.save").OrderByDescending(a=>a.CreatedAt).FirstAsync();
        using var before=System.Text.Json.JsonDocument.Parse(audit.BeforeJson!);using var after=System.Text.Json.JsonDocument.Parse(audit.AfterJson!);
        var oldFields=before.RootElement.EnumerateArray().Single(e=>e.GetProperty("Type").GetString()=="Policy").GetProperty("Fields");
        var newFields=after.RootElement.EnumerateArray().Single(e=>e.GetProperty("Type").GetString()=="Policy").GetProperty("Fields");
        Assert.Equal(1,oldFields.GetProperty("VersionNo").GetInt64());Assert.Equal(2,newFields.GetProperty("VersionNo").GetInt64());
        Assert.Equal("企业认证",oldFields.GetProperty("Name").GetString());Assert.Equal("修改",newFields.GetProperty("Name").GetString());
        using var config=System.Text.Json.JsonDocument.Parse(actual.Config);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(CanonicalJson.Serialize(config.RootElement))),newFields.GetProperty("ConfigHash").GetString());
    }

    [Fact]
    public async Task ProjectCannotEditOrganizationPolicy()
    {
        await using var api = await PrepareAsync(); var p = await CreateAsync(api, true);
        await using (var db = api.Context())
        {
            await db.Set<UserProjectScope>().Where(s => s.UserId == api.User.Id).ExecuteDeleteAsync();
            db.Add(new UserProjectScope { UserId = api.User.Id, OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, AccessMode = "read_write" }); await db.SaveChangesAsync();
        }
        using var list = await api.Client.GetAsync($"/api/v1/projects/{api.Project.Id}/policies"); list.EnsureSuccessStatusCode();
        Assert.Contains((await list.Content.ReadFromJsonAsync<PageResult<PolicyDto>>())!.Items, x => x.Id == p.Id);
        using var write = await api.WriteAsync(HttpMethod.Put, $"/api/v1/policies/{p.Id}", Body("越权"), "\"1\""); Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task ForeignScopeHiddenInDetailsAndTotals()
    {
        await using var api = await PrepareAsync(); await CreateAsync(api); Guid foreignId;
        await using (var db = api.Context())
        {
            var org = new Organization { Code = "FOREIGN", Name = "秘密组织" }; var p = new Policy { OrganizationId = org.Id, Name = "秘密策略", Type = "authentication", Config = "{\"mode\":\"ApiKey\"}" }; foreignId = p.Id; db.AddRange(org, p); await db.SaveChangesAsync();
        }
        using var detail = await api.Client.GetAsync($"/api/v1/policies/{foreignId}"); Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode); Assert.DoesNotContain("秘密", await detail.Content.ReadAsStringAsync());
        var list = await api.Client.GetFromJsonAsync<PageResult<PolicyDto>>($"/api/v1/projects/{api.Project.Id}/policies"); Assert.Equal(1, list!.Total); Assert.DoesNotContain(list.Items, p => p.Id == foreignId);
    }

    [Fact]
    public async Task CopyHasNewIdAndNoBindings()
    {
        await using var api = await PrepareAsync(); var original = await CreateAsync(api);
        using var copy = await api.WriteAsync(HttpMethod.Post, $"/api/v1/policies/{original.Id}/copy", new CopyPolicyRequest("认证副本", api.Project.Id)); copy.EnsureSuccessStatusCode();
        var p = (await copy.Content.ReadFromJsonAsync<PolicyDto>())!; Assert.NotEqual(original.Id, p.Id); Assert.Equal(1, p.VersionNo); Assert.Equal(original.Config, p.Config);
        await using var db = api.Context(); Assert.False(await db.Set<RoutePolicyBinding>().AnyAsync(b => b.PolicyId == p.Id));
    }

    [Fact]
    public async Task IdempotentSaveAuditedOnce()
    {
        await using var api = await PrepareAsync(); var key = Guid.NewGuid().ToString("N");
        using var first = await ApiFixture.CommandAsync(api.Client, $"/api/v1/projects/{api.Project.Id}/policies", Body(), key); first.EnsureSuccessStatusCode();
        using var replay = await ApiFixture.CommandAsync(api.Client, $"/api/v1/projects/{api.Project.Id}/policies", Body(), key); replay.EnsureSuccessStatusCode();
        Assert.Equal((await first.Content.ReadFromJsonAsync<PolicyDto>())!.Id, (await replay.Content.ReadFromJsonAsync<PolicyDto>())!.Id);
        await using var db = api.Context(); Assert.Equal(1, await db.Set<Policy>().CountAsync()); Assert.Equal(1, await db.Set<AuditLog>().CountAsync(a => a.Action == "policy.save"));
    }

    [Fact]
    public async Task ReservedNameAndImmutableTypeScope()
    {
        await using var api = await PrepareAsync();
        using var reserved = await api.WriteAsync(HttpMethod.Post, $"/api/v1/projects/{api.Project.Id}/policies", Body("RouteAuth-forged")); Assert.Equal(HttpStatusCode.UnprocessableEntity, reserved.StatusCode);
        var p = await CreateAsync(api);
        using var type = await api.WriteAsync(HttpMethod.Put, $"/api/v1/policies/{p.Id}", new SavePolicyRequest("更换类型", "timeout", "{\"timeoutMs\":1000}"), "\"1\""); Assert.Equal(HttpStatusCode.UnprocessableEntity, type.StatusCode);
        using var removed = await api.WriteAsync(HttpMethod.Delete, $"/api/v1/policies/{p.Id}", etag: "\"1\""); Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }
}

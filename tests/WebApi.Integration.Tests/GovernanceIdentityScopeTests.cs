using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceIdentityScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_identity_preserves_role_ownership_and_requires_the_builtin_platform_role(bool builtin)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedScopeAsync();
        var other = new Organization { Code = "OTHER", Name = "乙组织" };
        var otherProject = new Project { OrganizationId = other.Id, Code = "P", Name = "乙项目" };
        Guid sourceId;
        await using (var db = api.Context())
        {
            sourceId = await db.Set<Role>().Select(r => r.Id).SingleAsync();
            var platform = new Role { Code = "PlatformAdmin", Name = "同名角色", IsSystem = builtin };
            var scoped = new Role { OrganizationId = other.Id, Code = "PlatformAdmin", Name = "组织角色", IsSystem = true };
            var appWrite = new Permission { Code = "app.write", Name = "写应用", Module = "app" };
            var global = await db.Set<Permission>().Where(p => p.Code == "user.manage" || p.Code == "scope.manage" || p.Code == "project.read")
                .Select(p => p.Id).ToArrayAsync();
            db.AddRange(other, otherProject, platform, scoped, appWrite, new UserRole { UserId = api.User.Id, RoleId = platform.Id },
                new UserRole { UserId = api.User.Id, RoleId = scoped.Id },
                new RolePermission { RoleId = sourceId, PermissionId = appWrite.Id },
                new UserProjectScope { UserId = api.User.Id, OrganizationId = other.Id, AccessMode = "read_write" });
            db.AddRange(global.Select(id => new RolePermission { RoleId = platform.Id, PermissionId = id }));
            await db.SaveChangesAsync();
        }
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var me = await api.Client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal(builtin, me.GetProperty("isPlatformAdministrator").GetBoolean());
        var groups = me.GetProperty("rolePermissions").EnumerateArray().ToArray();
        Assert.Collection(groups.Where(g => g.GetProperty("organizationId").ValueKind == JsonValueKind.Null),
            g => Assert.Equal(new[] { "project.read", "scope.manage", "user.manage" },
                g.GetProperty("permissions").EnumerateArray().Select(p => p.GetString() ?? throw new JsonException("权限编码不能为空。")).ToArray()));
        var scopedGrants = Assert.Single(groups, g => g.GetProperty("organizationId").ValueKind != JsonValueKind.Null);
        Assert.Equal(api.Organization.Id, scopedGrants.GetProperty("organizationId").GetGuid());
        Assert.Contains(scopedGrants.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "project.write");
        Assert.Contains(me.GetProperty("permissions").EnumerateArray(), p => p.GetString() == "project.write");
        Assert.Equal(2, me.GetProperty("scopes").GetArrayLength());

        foreach (var (org, project, permitted) in new[] { (api.Organization.Id, api.Project.Id, true), (other.Id, otherProject.Id, false) })
        {
            using var candidates = await api.Client.GetAsync($"/api/v1/projects/{project}/owner-candidates");
            using var admins = await api.Client.GetAsync($"/api/v1/organizations/{org}/administrators");
            using var app = await api.WriteAsync(HttpMethod.Post, "/api/v1/applications",
                new { OrganizationId = org, Code = "SHARED", Name = "共享", Owner = "接入组", Status = "Active" });
            using var copy = await api.WriteAsync(HttpMethod.Post, $"/api/v1/roles/{sourceId}/copy",
                new { OrganizationId = org, Code = "COPY", Name = "副本" }, "\"1\"");
            Assert.Equal(permitted, candidates.IsSuccessStatusCode);
            Assert.Equal(permitted && builtin, admins.IsSuccessStatusCode);
            Assert.Equal(permitted, app.IsSuccessStatusCode);
            Assert.Equal(permitted, copy.IsSuccessStatusCode);
            if (!permitted)
                Assert.All(new[] { candidates, admins, app, copy }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
            if (!builtin) Assert.Equal(HttpStatusCode.Forbidden, admins.StatusCode);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    public async Task Identity_reads_its_role_permissions_in_three_queries_regardless_of_organization_count(int additionalOrganizations)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedScopeAsync();
        await using (var seed = api.Context())
        {
            var permission = await seed.Set<Permission>().SingleAsync(p => p.Code == "project.write");
            for (var i = 0; i < additionalOrganizations; i++)
            {
                var org = new Organization { Code = "ORG" + i, Name = "组织" + i };
                var role = new Role { OrganizationId = org.Id, Code = "Writer", Name = "项目管理员" };
                seed.AddRange(org, role, new RolePermission { RoleId = role.Id, PermissionId = permission.Id },
                    new UserRole { UserId = api.User.Id, RoleId = role.Id },
                    new UserProjectScope { UserId = api.User.Id, OrganizationId = org.Id, AccessMode = "read_write" });
            }
            await seed.SaveChangesAsync();
        }
        var commands = new List<string>();
        var options = new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(api.Database.ConnectionString)
            .LogTo(commands.Add, [RelationalEventId.CommandExecuted]).Options;
        await using var db = new WebApiDbContext(options);
        var identity = await new AccountService(db, new PasswordHasher<UserRecord>()).IdentityAsync(api.User.Id, default);
        Assert.Equal(3, commands.Count);
        var json = JsonSerializer.SerializeToElement(identity, JsonSerializerOptions.Web);
        Assert.Equal(additionalOrganizations + 1, json.GetProperty("rolePermissions").GetArrayLength());
    }
}

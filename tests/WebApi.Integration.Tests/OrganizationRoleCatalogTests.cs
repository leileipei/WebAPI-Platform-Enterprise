using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Governance;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class OrganizationRoleCatalogTests(MigratedDatabaseTemplateFixture template) : IClassFixture<MigratedDatabaseTemplateFixture>
{
    private const string Roles = "/api/v1/roles";
    private const string Permissions = "/api/v1/permissions";
    private const string Directories = "/api/v1/role-directories";

    private Task<ApiFixture> LoginAsync(bool platformAdmin = false) => ApiFixture.CreateAsync(async api =>
    {
        await api.InitializeAsync();
        await api.SeedScopeAsync(platformAdmin: platformAdmin);
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
    }, template.Template);

    private static string InOrganization(string path, Guid id) => $"{path}?organizationId={id}";
    private static string Tag(RoleDto role) => $"\"{role.Revision}\"";

    [Fact]
    public async Task OrganizationManagerCanReopenAndEditTheCompletePermissionSelection()
    {
        await using var api = await LoginAsync();
        var org = api.Organization.Id;
        using var created = await api.WriteAsync(HttpMethod.Post, Roles, new SaveRoleRequest("Planner", "计划员", org));
        created.EnsureSuccessStatusCode();
        var role = (await created.Content.ReadFromJsonAsync<RoleDto>())!;
        var permissionsPath = $"{Roles}/{role.Id}/permissions";
        using var assigned = await api.WriteAsync(HttpMethod.Put, permissionsPath, new AssignPermissionsRequest(["project.read", "organization.read"]), Tag(role));
        assigned.EnsureSuccessStatusCode();

        var catalogPath = InOrganization(Roles, org);
        var list = (await api.Client.GetFromJsonAsync<RoleDto[]>(catalogPath))!;
        var reopened = Assert.Single(list, item => item.Id == role.Id);
        Assert.Equal(org, reopened.OrganizationId);
        Assert.False(reopened.IsSystem);
        Assert.Equal(assigned.Headers.ETag!.Tag, Tag(reopened));
        Assert.Equal(["organization.read", "project.read"], reopened.Permissions.Order());

        using var rename = await api.WriteAsync(HttpMethod.Put, $"{Roles}/{role.Id}", new SaveRoleRequest("Planner", "排产员", org), Tag(reopened));
        rename.EnsureSuccessStatusCode();
        var renamed = (await rename.Content.ReadFromJsonAsync<RoleDto>())!;
        using var replace = await api.WriteAsync(HttpMethod.Put, permissionsPath, new AssignPermissionsRequest(["project.read", "environment.read"]), Tag(renamed));
        replace.EnsureSuccessStatusCode();
        using var stale = await api.WriteAsync(HttpMethod.Put, permissionsPath, new AssignPermissionsRequest([]), Tag(reopened));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var saved = Assert.Single((await api.Client.GetFromJsonAsync<RoleDto[]>(catalogPath))!, item => item.Id == role.Id);
        Assert.Equal("排产员", saved.Name);
        Assert.Equal(replace.Headers.ETag!.Tag, Tag(saved));
        Assert.Equal(["environment.read", "project.read"], saved.Permissions.Order());
    }

    [Fact]
    public async Task OrganizationDictionaryShowsOnlyGrantableCodesWhileRoleShowsAllAssignedCodes()
    {
        await using var api = await LoginAsync();
        var org = api.Organization.Id;
        var other = new Organization { Code = "OTHER", Name = "其他组织" };
        var foreignRole = new Role { OrganizationId = other.Id, Code = "LocalEditor", Name = "其他组织编辑" };
        var inheritedRole = new Role { Code = "Reader", Name = "全局只读", IsSystem = true };
        var ownRole = new Role { OrganizationId = org, Code = "Reviewer", Name = "审核员" };
        var reservedRole = new Role { OrganizationId = org, Code = "Reserved", Name = "组织内置", IsSystem = true };
        var inherited = new Permission { Code = "api.read", Module = "api", Name = "查看接口", Description = "接口读取" };
        var foreign = new Permission { Code = "api.edit", Module = "api", Name = "编辑接口" };
        await using (var db = api.Context())
        {
            db.AddRange(other, foreignRole, inheritedRole, ownRole, reservedRole, inherited, foreign);
            db.AddRange(new UserRole { UserId = api.User.Id, RoleId = inheritedRole.Id },
                new UserRole { UserId = api.User.Id, RoleId = foreignRole.Id });
            var projectRead = await db.Set<Permission>().SingleAsync(p => p.Code == "project.read");
            db.AddRange(new RolePermission { RoleId = inheritedRole.Id, PermissionId = inherited.Id },
                new RolePermission { RoleId = inheritedRole.Id, PermissionId = projectRead.Id },
                new RolePermission { RoleId = foreignRole.Id, PermissionId = foreign.Id },
                new RolePermission { RoleId = ownRole.Id, PermissionId = foreign.Id });
            await db.SaveChangesAsync();
        }

        var dictionary = (await api.Client.GetFromJsonAsync<JsonElement[]>(InOrganization(Permissions, org)))!;
        var definition = Assert.Single(dictionary, entry => entry.GetProperty("code").GetString() == "api.read");
        Assert.Equal(inherited.Id, definition.GetProperty("id").GetGuid());
        Assert.Equal("api", definition.GetProperty("module").GetString());
        Assert.Equal("查看接口", definition.GetProperty("name").GetString());
        Assert.Equal("接口读取", definition.GetProperty("description").GetString());
        Assert.Single(dictionary, entry => entry.GetProperty("code").GetString() == "project.read");
        Assert.DoesNotContain(dictionary, entry => entry.GetProperty("code").GetString() == "api.edit");
        var roles = (await api.Client.GetFromJsonAsync<RoleDto[]>(InOrganization(Roles, org)))!;
        Assert.All(roles, item => Assert.Equal(org, item.OrganizationId));
        var own = Assert.Single(roles, item => item.Id == ownRole.Id);
        Assert.Equal(["api.edit"], own.Permissions);
        var reserved = Assert.Single(roles, item => item.Id == reservedRole.Id);
        Assert.True(reserved.IsSystem);
        using var immutable = await api.WriteAsync(HttpMethod.Put, $"{Roles}/{reserved.Id}/permissions", new AssignPermissionsRequest([]), Tag(reserved));
        Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);

        using var denied = await api.WriteAsync(HttpMethod.Put, $"{Roles}/{own.Id}/permissions",
            new AssignPermissionsRequest(["api.edit", "api.read"]), Tag(own));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var check = api.Context();
        Assert.Equal(own.Revision, (await check.Set<Role>().SingleAsync(r => r.Id == own.Id)).Revision);
    }

    [Theory]
    [InlineData("read", HttpStatusCode.Forbidden)]
    [InlineData("project", HttpStatusCode.Forbidden)]
    [InlineData("revoked", HttpStatusCode.Forbidden)]
    [InlineData("disabled", HttpStatusCode.Conflict)]
    public async Task OrganizationCatalogsEnforceCurrentRoleAndDataScope(string change, HttpStatusCode status)
    {
        await using var api = await LoginAsync();
        var org = api.Organization.Id;
        await using (var db = api.Context())
        {
            var grant = await db.Set<UserProjectScope>().SingleAsync();
            if (change == "read") grant.AccessMode = "read";
            if (change == "project") grant.ProjectId = api.Project.Id;
            if (change == "disabled") (await db.Set<Organization>().SingleAsync()).Status = "Disabled";
            if (change == "revoked")
            {
                var roleManage = await db.Set<Permission>().SingleAsync(p => p.Code == "role.manage");
                await db.Set<RolePermission>().Where(p => p.PermissionId == roleManage.Id).ExecuteDeleteAsync();
            }
            await db.SaveChangesAsync();
        }
        foreach (var path in new[] { Roles, Permissions })
        {
            using var denied = await api.Client.GetAsync(InOrganization(path, org));
            Assert.Equal(status, denied.StatusCode);
        }
        using var write = await api.WriteAsync(HttpMethod.Post, Roles, new SaveRoleRequest("CannotCreate", "受限角色", org));
        Assert.Equal(status, write.StatusCode);
        var directories = await api.Client.GetFromJsonAsync<JsonElement>(Directories);
        Assert.False(directories.GetProperty("canManagePlatformRoles").GetBoolean());
        Assert.Empty(directories.GetProperty("organizations").EnumerateArray());
    }

    [Fact]
    public async Task OrganizationManagerCannotUsePlatformOrForeignCatalogsEvenWithForeignDataGrant()
    {
        await using var api = await LoginAsync();
        var other = new Organization { Code = "OTHER", Name = "其他组织" };
        var foreign = new Role { OrganizationId = other.Id, Code = "OtherRole", Name = "其他组织自定义角色" };
        var system = new Role { Code = "PlatformAdmin", Name = "平台管理员", IsSystem = true };
        await using (var db = api.Context())
        {
            db.AddRange(other, foreign, system,
                new UserProjectScope { UserId = api.User.Id, OrganizationId = other.Id, AccessMode = "read_write" });
            await db.SaveChangesAsync();
        }
        foreach (var path in new[] { Roles, Permissions })
        {
            using var platform = await api.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, platform.StatusCode);
            using var denied = await api.Client.GetAsync(InOrganization(path, other.Id));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        foreach (var role in new[] { system, foreign })
        {
            using var denied = await api.WriteAsync(HttpMethod.Put, $"{Roles}/{role.Id}/permissions", new AssignPermissionsRequest([]), "\"1\"");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var assign = await api.WriteAsync(HttpMethod.Put, $"/api/v1/users/{api.User.Id}/roles", new AssignRolesRequest([system.Id]), "\"1\"");
        Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
        var directories = await api.Client.GetFromJsonAsync<JsonElement>(Directories);
        Assert.False(directories.GetProperty("canManagePlatformRoles").GetBoolean());
        var own = Assert.Single(directories.GetProperty("organizations").EnumerateArray());
        Assert.Equal(api.Organization.Id, own.GetProperty("id").GetGuid());
        Assert.Equal(api.Organization.Name, own.GetProperty("name").GetString());
    }

    [Fact]
    public async Task PlatformCatalogIncludesAllRolesForUserAssignmentAndExplicitOrganizationStillFilters()
    {
        await using var api = await LoginAsync(platformAdmin: true);
        var other = new Organization { Code = "OTHER", Name = "其他组织" };
        var own = new Role { OrganizationId = api.Organization.Id, Code = "Auditor", Name = "本组织审计" };
        var foreign = new Role { OrganizationId = other.Id, Code = "Auditor", Name = "其他组织审计" };
        var unassigned = new Permission { Code = "api.edit", Module = "api", Name = "编辑接口" };
        await using (var db = api.Context())
        {
            db.AddRange(other, own, foreign, unassigned);
            await db.SaveChangesAsync();
        }
        var all = (await api.Client.GetFromJsonAsync<RoleDto[]>(Roles))!;
        var platform = Assert.Single(all, role => role.IsSystem && role.OrganizationId is null);
        Assert.Contains(all, role => role.Id == own.Id);
        Assert.Contains(all, role => role.Id == foreign.Id);
        var dictionary = (await api.Client.GetFromJsonAsync<JsonElement[]>(Permissions))!;
        Assert.Contains(dictionary, entry => entry.GetProperty("code").GetString() == "api.edit");
        var directories = await api.Client.GetFromJsonAsync<JsonElement>(Directories);
        Assert.True(directories.GetProperty("canManagePlatformRoles").GetBoolean());
        var managed = Assert.Single(directories.GetProperty("organizations").EnumerateArray());
        Assert.Equal(api.Organization.Id, managed.GetProperty("id").GetGuid());
        using var assign = await api.WriteAsync(HttpMethod.Put, $"/api/v1/users/{api.User.Id}/roles",
            new AssignRolesRequest([platform.Id, own.Id, foreign.Id]), "\"1\"");
        assign.EnsureSuccessStatusCode();
        var assigned = (await assign.Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Equal(new[] { platform.Id, own.Id, foreign.Id }.Order(), assigned.RoleIds.Order());
        using var immutable = await api.WriteAsync(HttpMethod.Put, $"{Roles}/{platform.Id}/permissions", new AssignPermissionsRequest([]), Tag(platform));
        Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);

        var scoped = (await api.Client.GetFromJsonAsync<RoleDto[]>(InOrganization(Roles, api.Organization.Id)))!;
        Assert.Equal(own.Id, Assert.Single(scoped).Id);
        var available = (await api.Client.GetFromJsonAsync<JsonElement[]>(InOrganization(Permissions, api.Organization.Id)))!;
        Assert.DoesNotContain(available, entry => entry.GetProperty("code").GetString() == "api.edit");
        foreach (var path in new[] { Roles, Permissions })
        {
            using var absentScope = await api.Client.GetAsync(InOrganization(path, other.Id));
            Assert.Equal(HttpStatusCode.Forbidden, absentScope.StatusCode);
            using var emptyScope = await api.Client.GetAsync(InOrganization(path, Guid.Empty));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, emptyScope.StatusCode);
        }
    }

    [Fact]
    public async Task DirectoriesReflectCurrentPlatformRoleAuthorizationAndRestrictedFirstLogin()
    {
        await using var api = await LoginAsync(platformAdmin: true);
        var current = await api.Client.GetFromJsonAsync<JsonElement>(Directories);
        Assert.True(current.GetProperty("canManagePlatformRoles").GetBoolean());
        await using (var db = api.Context())
        {
            var roleManage = await db.Set<Permission>().SingleAsync(p => p.Code == "role.manage");
            await db.Set<RolePermission>().Where(p => p.PermissionId == roleManage.Id).ExecuteDeleteAsync();
        }
        var revoked = await api.Client.GetFromJsonAsync<JsonElement>(Directories);
        Assert.False(revoked.GetProperty("canManagePlatformRoles").GetBoolean());
        Assert.Empty(revoked.GetProperty("organizations").EnumerateArray());
        using var denied = await api.Client.GetAsync(Roles);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using (var db = api.Context())
            await db.Set<UserRecord>().Where(u => u.Id == api.User.Id).ExecuteUpdateAsync(set => set.SetProperty(u => u.MustChangePassword, true));
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        using var restricted = await api.Client.GetAsync(Directories);
        Assert.Equal(HttpStatusCode.Forbidden, restricted.StatusCode);
    }

    [Fact]
    public async Task RoleManagementAloneDiscoversItsOrganizationWithoutExpandingTheWorkspaceTree()
    {
        await using var api = await LoginAsync();
        await using (var db = api.Context())
        {
            var manage = await db.Set<Permission>().SingleAsync(p => p.Code == "role.manage");
            await db.Set<RolePermission>().Where(p => p.PermissionId != manage.Id).ExecuteDeleteAsync();
        }
        var tree = await api.Client.GetFromJsonAsync<JsonElement>("/api/v1/scope-tree");
        Assert.Empty(tree.GetProperty("organizations").EnumerateArray());
        var directories = await api.Client.GetFromJsonAsync<JsonElement>(Directories);
        Assert.False(directories.GetProperty("canManagePlatformRoles").GetBoolean());
        var organization = Assert.Single(directories.GetProperty("organizations").EnumerateArray());
        Assert.Equal(api.Organization.Id, organization.GetProperty("id").GetGuid());
        Assert.Equal(api.Organization.Name, organization.GetProperty("name").GetString());
        using var roles = await api.Client.GetAsync(InOrganization(Roles, api.Organization.Id));
        roles.EnsureSuccessStatusCode();
        var allowed = (await api.Client.GetFromJsonAsync<JsonElement[]>(InOrganization(Permissions, api.Organization.Id)))!;
        Assert.Equal("role.manage", Assert.Single(allowed).GetProperty("code").GetString());

        // Built-in OrganizationAdmin is a global role with role.manage, but platform administration requires PlatformAdmin.
        await using (var db = api.Context())
        {
            var inherited = new Role { Code = "OrganizationAdmin", Name = "组织管理员", IsSystem = true };
            var manage = await db.Set<Permission>().SingleAsync(p => p.Code == "role.manage");
            db.AddRange(inherited, new UserRole { UserId = api.User.Id, RoleId = inherited.Id },
                new RolePermission { RoleId = inherited.Id, PermissionId = manage.Id });
            await db.SaveChangesAsync();
        }
        var inheritedDirectories = await api.Client.GetFromJsonAsync<JsonElement>(Directories);
        Assert.False(inheritedDirectories.GetProperty("canManagePlatformRoles").GetBoolean());
        using var platform = await api.Client.GetAsync(Roles);
        Assert.Equal(HttpStatusCode.Forbidden, platform.StatusCode);
    }
}

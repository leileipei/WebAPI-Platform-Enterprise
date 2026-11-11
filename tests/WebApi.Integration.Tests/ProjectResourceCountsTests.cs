using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ProjectResourceCountsTests
{
    private static async Task<JsonElement> GetAsync(ApiFixture api, string path)
    {
        using var response = await api.Client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string ListPath(ApiFixture api) => $"/api/v1/organizations/{api.Organization.Id}/projects";

    [Fact]
    public async Task CountsResourcesOnceAndExcludesOrganizationSharedApplications()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();

        await using (var db = api.Context())
        {
            db.Add(new EnvironmentRecord { ProjectId = api.Project.Id, Code = "DEV", Name = "开发", Status = "Disabled" });
            db.Add(new Api { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, Code = "RETIRED", Name = "已退役", LifecycleStatus = "Retired", OwnerUserId = api.User.Id });
            db.Add(new ApiVersion { ApiId = api.Api.Id, Version = "2.0", Status = "Draft", CreatedBy = api.User.Id });
            db.Add(new ApplicationRecord { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, Code = "DISABLED", Name = "停用应用", Status = "Disabled" });
            db.Add(new ApplicationRecord { OrganizationId = api.Organization.Id, Code = "SHARED", Name = "组织共享应用" });
            db.Add(new UserProjectScope { UserId = api.User.Id, OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, AccessMode = "read" });
            db.Add(new UserProjectScope { UserId = api.User.Id, OrganizationId = api.Organization.Id, ProjectId = api.Project.Id, EnvironmentId = api.Environment.Id, AccessMode = "read" });
            await db.SaveChangesAsync();
        }

        var list = await GetAsync(api, ListPath(api));
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(api.Project.Id, row.GetProperty("id").GetGuid());
        Assert.Equal(api.Organization.Id, row.GetProperty("organizationId").GetGuid());
        Assert.Equal(api.Project.Code, row.GetProperty("code").GetString());
        Assert.Equal(api.Project.Name, row.GetProperty("name").GetString());
        Assert.Equal(2, row.GetProperty("environmentCount").GetInt32());
        Assert.Equal(2, row.GetProperty("apiCount").GetInt32());
        Assert.Equal(2, row.GetProperty("applicationCount").GetInt32());
        Assert.Equal(1, row.GetProperty("revision").GetInt64());

        using var detail = await api.Client.GetAsync($"/api/v1/projects/{api.Project.Id}");
        detail.EnsureSuccessStatusCode();
        Assert.Equal("\"1\"", detail.Headers.ETag?.ToString());
        var original = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(original.TryGetProperty("apiCount", out _));
        var tree = await GetAsync(api, "/api/v1/scope-tree");
        Assert.False(tree.GetProperty("projects")[0].TryGetProperty("apiCount", out _));
    }

    [Fact]
    public async Task EmptyProjectAndOutOfRangePageKeepPaginationAndReadableZeros()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var empty = new Project { OrganizationId = api.Organization.Id, Code = "A_EMPTY", Name = "空项目" };
        await using (var db = api.Context())
        {
            db.Add(empty);
            await db.SaveChangesAsync();
        }

        var first = await GetAsync(api, ListPath(api) + "?page=1&pageSize=1");
        var row = Assert.Single(first.GetProperty("items").EnumerateArray());
        Assert.Equal(empty.Id, row.GetProperty("id").GetGuid());
        foreach (var field in new[] { "environmentCount", "apiCount", "applicationCount" })
            Assert.Equal(0, row.GetProperty(field).GetInt32());
        Assert.Equal(2, first.GetProperty("total").GetInt32());
        Assert.Equal(1, first.GetProperty("page").GetInt32());
        Assert.Equal(1, first.GetProperty("pageSize").GetInt32());

        var second = await GetAsync(api, ListPath(api) + "?page=2&pageSize=1");
        row = Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal(api.Project.Id, row.GetProperty("id").GetGuid());
        foreach (var field in new[] { "environmentCount", "apiCount", "applicationCount" })
            Assert.Equal(1, row.GetProperty(field).GetInt32());
        var beyond = await GetAsync(api, ListPath(api) + "?page=3&pageSize=1");
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        Assert.Equal(2, beyond.GetProperty("total").GetInt32());
        Assert.Equal(3, beyond.GetProperty("page").GetInt32());

        var clamped = await GetAsync(api, ListPath(api) + "?page=-1&pageSize=500");
        Assert.Equal(1, clamped.GetProperty("page").GetInt32());
        Assert.Equal(100, clamped.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("environment.read", "environmentCount")]
    [InlineData("api.read", "apiCount")]
    [InlineData("app.read", "applicationCount")]
    public async Task RevokedPermissionReturnsNullInExistingSession(string permission, string field)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var before = await GetAsync(api, ListPath(api));
        Assert.Equal(1, before.GetProperty("items")[0].GetProperty(field).GetInt32());

        await using (var db = api.Context())
        {
            var permissionId = await db.Set<Permission>().Where(p => p.Code == permission).Select(p => p.Id).SingleAsync();
            await db.Set<RolePermission>().Where(p => p.PermissionId == permissionId).ExecuteDeleteAsync();
        }

        var after = await GetAsync(api, ListPath(api));
        var row = Assert.Single(after.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty(field).ValueKind);
        foreach (var other in new[] { "environmentCount", "apiCount", "applicationCount" }.Where(key => key != field))
            Assert.Equal(1, row.GetProperty(other).GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NarrowScopesHideOtherProjectsAndProjectResourceCounts(bool environmentOnly)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        await using (var db = api.Context())
        {
            var grant = await db.Set<UserProjectScope>().SingleAsync();
            grant.ProjectId = api.Project.Id;
            grant.EnvironmentId = environmentOnly ? api.Environment.Id : null;
            var hidden = new Project { OrganizationId = api.Organization.Id, Code = "HIDDEN", Name = "其他项目" };
            db.Add(hidden);
            db.Add(new EnvironmentRecord { ProjectId = api.Project.Id, Code = "PROD", Name = "生产" });
            db.Add(new EnvironmentRecord { ProjectId = hidden.Id, Code = "TEST", Name = "其他环境" });
            db.Add(new Api { OrganizationId = api.Organization.Id, ProjectId = hidden.Id, Code = "HIDDEN", Name = "其他API", LifecycleStatus = "Active", OwnerUserId = api.User.Id });
            db.Add(new ApplicationRecord { OrganizationId = api.Organization.Id, ProjectId = hidden.Id, Code = "HIDDEN", Name = "其他应用" });
            db.Add(new ApplicationRecord { OrganizationId = api.Organization.Id, Code = "SHARED", Name = "组织共享应用" });
            await db.SaveChangesAsync();
        }

        var result = await GetAsync(api, ListPath(api));
        var row = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal(1, result.GetProperty("total").GetInt32());
        Assert.Equal(api.Project.Id, row.GetProperty("id").GetGuid());
        Assert.Equal(environmentOnly ? 1 : 2, row.GetProperty("environmentCount").GetInt32());
        foreach (var field in new[] { "apiCount", "applicationCount" })
        {
            if (environmentOnly) Assert.Equal(JsonValueKind.Null, row.GetProperty(field).ValueKind);
            else Assert.Equal(1, row.GetProperty(field).GetInt32());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledBusinessAncestorKeepsGovernanceCountAndHidesBusinessCounts(bool organization)
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        await using (var db = api.Context())
        {
            if (organization) (await db.Set<Organization>().SingleAsync()).Status = "Disabled";
            else (await db.Set<Project>().SingleAsync()).Status = "Disabled";
            await db.SaveChangesAsync();
        }

        var result = await GetAsync(api, ListPath(api));
        var row = Assert.Single(result.GetProperty("items").EnumerateArray());
        Assert.Equal(1, row.GetProperty("environmentCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("apiCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("applicationCount").ValueKind);
    }

    [Fact]
    public async Task OrganizationRoleDoesNotExposeForeignCountsDespiteExtraDataGrant()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedConsumerAsync();
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
        var foreign = new Organization { Code = "FOREIGN", Name = "其他组织" };
        await using (var db = api.Context())
        {
            var project = new Project { OrganizationId = foreign.Id, Code = "FOREIGN", Name = "其他项目" };
            db.AddRange(foreign, project);
            db.Add(new UserProjectScope { UserId = api.User.Id, OrganizationId = foreign.Id, AccessMode = "read" });
            db.Add(new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "其他环境" });
            db.Add(new Api { OrganizationId = foreign.Id, ProjectId = project.Id, Code = "FOREIGN", Name = "其他API", LifecycleStatus = "Active", OwnerUserId = api.User.Id });
            db.Add(new ApplicationRecord { OrganizationId = foreign.Id, ProjectId = project.Id, Code = "FOREIGN", Name = "其他应用" });
            await db.SaveChangesAsync();
        }

        var own = await GetAsync(api, ListPath(api));
        var row = Assert.Single(own.GetProperty("items").EnumerateArray());
        foreach (var field in new[] { "environmentCount", "apiCount", "applicationCount" })
            Assert.Equal(1, row.GetProperty(field).GetInt32());
        var hidden = await GetAsync(api, $"/api/v1/organizations/{foreign.Id}/projects");
        Assert.Empty(hidden.GetProperty("items").EnumerateArray());
        Assert.Equal(0, hidden.GetProperty("total").GetInt32());
    }
}

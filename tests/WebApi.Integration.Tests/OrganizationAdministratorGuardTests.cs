using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class OrganizationAdministratorGuardTests
{
    [Fact]
    public async Task SoleAdministratorKeepsAccountRoleAndOrganizationWriteAccess()
    {
        await using var api = await OpenAsync();
        var org = api.Organization.Id;
        await using var setup = api.Context();
        var admin = await setup.Set<Role>().SingleAsync(r => r.Code == "OrganizationAdmin");
        var platform = await setup.Set<Role>().SingleAsync(r => r.Code == "PlatformAdmin");
        var custom = new Role { Code = "OrganizationAdmin", Name = "Custom", OrganizationId = org };
        var other = new Organization { Code = "OTHER", Name = "Other" };
        setup.AddRange(custom, other);
        var target = AddMember(setup, api, admin, Grant(org));
        setup.Add(new UserRole { UserId = target.Id, RoleId = platform.Id });
        AddMember(setup, api, custom, Grant(org));
        AddMember(setup, api, admin, Grant(other.Id));
        AddMember(setup, api, admin, Grant(org, "read"), Grant(org, project: api.Project.Id),
            Grant(org, project: api.Project.Id, environment: api.Environment.Id));
        AddMember(setup, api, admin, Grant(org)).Status = "Disabled";
        AddMember(setup, api, admin, Grant(org)).MustChangePassword = true;
        await setup.SaveChangesAsync();

        await AssertConflictAsync(WriteAsync(api, target, "", new UpdateUserRequest("Changed", "Disabled")));
        await AssertConflictAsync(WriteAsync(api, target, "/roles", new AssignRolesRequest([])));
        await AssertConflictAsync(WriteAsync(api, target, "/roles", new AssignRolesRequest([platform.Id, custom.Id])));
        await AssertConflictAsync(WriteAsync(api, target, "/scopes", new SaveScopesRequest([])));
        await AssertConflictAsync(WriteAsync(api, target, "/scopes", new SaveScopesRequest(
            [Grant(org, "read"), Grant(org, project: api.Project.Id)])));

        await using var check = api.Context();
        var saved = await check.Set<UserRecord>().SingleAsync(u => u.Id == target.Id);
        Assert.Equal("Active", saved.Status);
        Assert.Equal(target.DisplayName, saved.DisplayName);
        Assert.Equal(target.SecurityStamp, saved.SecurityStamp);
        Assert.Equal(1L, saved.Revision);
        Assert.Equal(2, await check.Set<UserRole>().CountAsync(r => r.UserId == target.Id));
        var scope = await check.Set<UserProjectScope>().SingleAsync(s => s.UserId == target.Id);
        Assert.Equal(org, scope.OrganizationId);
        Assert.Null(scope.ProjectId);
        Assert.Null(scope.EnvironmentId);
        Assert.Equal("read_write", scope.AccessMode);
        Assert.False(await check.Set<AuditLog>().AnyAsync(a =>
            a.Action == "user.update" || a.Action == "user.roles" || a.Action == "user.scopes"));

        using var retained = await WriteAsync(api, target, "/roles", new AssignRolesRequest([admin.Id]));
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        var remainingRole = await check.Set<UserRole>().SingleAsync(r => r.UserId == target.Id);
        Assert.Equal(admin.Id, remainingRole.RoleId);
    }

    [Fact]
    public async Task ReplacementProtectsOnlyLostOrganizationsAndSupportsHandover()
    {
        await using var api = await OpenAsync();
        var org = api.Organization.Id;
        await using var setup = api.Context();
        var admin = await setup.Set<Role>().SingleAsync(r => r.Code == "OrganizationAdmin");
        var archived = new Organization { Code = "ARCHIVED", Name = "Archived", Status = "Disabled" };
        var historical = new Organization { Code = "UNASSIGNED", Name = "Unassigned" };
        setup.AddRange(archived, historical);
        var target = AddMember(setup, api, admin, Grant(org), Grant(archived.Id), Grant(historical.Id, "read"));
        var successor = AddMember(setup, api, admin, Grant(org));
        await setup.SaveChangesAsync();

        await AssertConflictAsync(WriteAsync(api, target, "", new UpdateUserRequest(target.DisplayName, "Disabled")));
        await AssertConflictAsync(WriteAsync(api, target, "/scopes", new SaveScopesRequest([Grant(org)])));
        using var partial = await WriteAsync(api, target, "/scopes", new SaveScopesRequest([Grant(archived.Id)]));
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);

        using var assign = await WriteAsync(api, successor, "/scopes", new SaveScopesRequest([Grant(org), Grant(archived.Id)]));
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        using var handover = await WriteAsync(api, target, "/roles", new AssignRolesRequest([]), revision: 2);
        Assert.Equal(HttpStatusCode.OK, handover.StatusCode);

        await using var check = api.Context();
        Assert.Empty(await check.Set<UserRole>().Where(r => r.UserId == target.Id).ToArrayAsync());
        Assert.Equal(3L, (await check.Set<UserRecord>().SingleAsync(u => u.Id == target.Id)).Revision);
        var successorScopes = await check.Set<UserProjectScope>().Where(s => s.UserId == successor.Id)
            .Select(s => s.OrganizationId).ToArrayAsync();
        Assert.Equal(new[] { org, archived.Id }.Order(), successorScopes.Order());
        Assert.False(await check.Set<UserProjectScope>().AnyAsync(s => s.OrganizationId == historical.Id));
    }

    [Fact]
    public async Task RecoverableAdministratorsCanHandOverButCannotRemoveTheirLastAssignment()
    {
        await using var api = await OpenAsync();
        await using var setup = api.Context();
        var admin = await setup.Set<Role>().SingleAsync(r => r.Code == "OrganizationAdmin");
        var first = AddMember(setup, api, admin, Grant(api.Organization.Id));
        var second = AddMember(setup, api, admin, Grant(api.Organization.Id));
        first.MustChangePassword = second.MustChangePassword = true;
        await setup.SaveChangesAsync();

        using var handover = await WriteAsync(api, first, "", new UpdateUserRequest(first.DisplayName, "Disabled"));
        Assert.Equal(HttpStatusCode.OK, handover.StatusCode);
        await AssertConflictAsync(WriteAsync(api, second, "/roles", new AssignRolesRequest([])));
        await using var check = api.Context();
        Assert.Equal("Disabled", (await check.Set<UserRecord>().SingleAsync(u => u.Id == first.Id)).Status);
        Assert.True(await check.Set<UserRole>().AnyAsync(r => r.UserId == second.Id && r.RoleId == admin.Id));
    }

    [Fact]
    public async Task ConcurrentRoleAndScopeRemovalsCommitOnlyOneHandover()
    {
        await using var api = await OpenAsync();
        var org = api.Organization.Id;
        await using var setup = api.Context();
        var admin = await setup.Set<Role>().SingleAsync(r => r.Code == "OrganizationAdmin");
        var first = AddMember(setup, api, admin, Grant(org));
        var second = AddMember(setup, api, admin, Grant(org));
        await setup.SaveChangesAsync();

        var results = await Task.WhenAll(
            WriteAsync(api, first, "/roles", new AssignRolesRequest([])),
            WriteAsync(api, second, "/scopes", new SaveScopesRequest([])));
        using var roleResult = results[0];
        using var scopeResult = results[1];
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        var conflict = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("last_organization_admin", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        await using var check = api.Context();
        var survivor = await (from role in check.Set<UserRole>()
                              join scope in check.Set<UserProjectScope>() on role.UserId equals scope.UserId
                              where role.RoleId == admin.Id && scope.OrganizationId == org && scope.ProjectId == null
                                  && scope.EnvironmentId == null && scope.AccessMode == "read_write"
                              select role.UserId).SingleAsync();
        Assert.Equal(roleResult.IsSuccessStatusCode ? second.Id : first.Id, survivor);
        Assert.Equal(1L, (await check.Set<UserRecord>().SingleAsync(u => u.Id == survivor)).Revision);
        Assert.Equal(1, await check.Set<AuditLog>().CountAsync(a => a.Action == "user.roles" || a.Action == "user.scopes"));
    }

    private static Task<ApiFixture> OpenAsync() => ApiFixture.CreateAsync(async api =>
    {
        await api.InitializeAsync();
        await api.SeedScopeAsync(platformAdmin: true);
        await using var db = api.Context();
        await PermissionCatalog.SeedAsync(db);
        using var login = await api.LoginAsync();
        login.EnsureSuccessStatusCode();
    });

    private static ScopeGrantDto Grant(Guid organization, string mode = "read_write", Guid? project = null, Guid? environment = null)
        => new(new(organization, project, environment), mode);

    private static UserRecord AddMember(WebApiDbContext db, ApiFixture api, Role role, params ScopeGrantDto[] scopes)
    {
        var user = new UserRecord
        {
            Username = "member_" + Guid.NewGuid().ToString("N"),
            DisplayName = "Organization member",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            PasswordHash = api.User.PasswordHash
        };
        db.Add(user);
        db.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        foreach (var grant in scopes)
            db.Add(new UserProjectScope
            {
                UserId = user.Id, OrganizationId = grant.Scope.OrganizationId, ProjectId = grant.Scope.ProjectId,
                EnvironmentId = grant.Scope.EnvironmentId, AccessMode = grant.AccessMode
            });
        return user;
    }

    private static Task<HttpResponseMessage> WriteAsync(ApiFixture api, UserRecord target, string suffix, object body, int revision = 1)
        => api.WriteAsync(HttpMethod.Put, $"/api/v1/users/{target.Id}{suffix}", body, $"\"{revision}\"");

    private static async Task AssertConflictAsync(Task<HttpResponseMessage> write)
    {
        using var response = await write;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("last_organization_admin", problem.GetProperty("code").GetString());
    }
}

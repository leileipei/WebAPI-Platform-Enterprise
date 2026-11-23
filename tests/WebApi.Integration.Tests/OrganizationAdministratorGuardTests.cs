using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Governance;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class OrganizationAdministratorGuardTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Appointment_and_revocation_restore_the_members_own_project_or_organization_access(bool organizationRead, bool hasGlobalRole)
    {
        await using var api = await OpenAsync();
        var org = api.Organization.Id;
        var sibling = new Project { OrganizationId = org, Code = "SIBLING", Name = "同组织另一项目" };
        await using var setup = api.Context();
        var writer = new Role { OrganizationId = org, Code = "ProjectWriter", Name = "项目编辑" };
        setup.AddRange(sibling, writer);
        var permissions = await setup.Set<Permission>().Where(p => p.Code == "project.write" || p.Code == "project.read").ToArrayAsync();
        setup.AddRange(permissions.Select(p => new RolePermission { RoleId = writer.Id, PermissionId = p.Id }));
        var permanent = organizationRead ? Grant(org, "read") : Grant(org, project: api.Project.Id);
        var target = AddMember(setup, api, writer, permanent);
        var administrator = await setup.Set<Role>().SingleAsync(r => r.IsSystem && r.Code == "OrganizationAdmin");
        AddMember(setup, api, administrator, Grant(org));
        if (hasGlobalRole) setup.Add(new UserRole { UserId = target.Id, RoleId = administrator.Id });
        await setup.SaveChangesAsync();

        async Task<bool> Allowed(string permission, Guid project)
        {
            await using var db = api.Context();
            return await new AuthorizationService(db).CanAsync(new(target.Id, "admin-handover"),
                permission, new("project", project, new(org, project)));
        }
        var endpoint = $"/api/v1/organizations/{org}/administrators/{target.Id}";
        Assert.Equal(organizationRead, await Allowed("project.read", sibling.Id));
        Assert.False(await Allowed("project.write", sibling.Id));
        using var appointed = await api.WriteAsync(HttpMethod.Put, endpoint, new { enabled = true }, "\"1\"");
        appointed.EnsureSuccessStatusCode();
        Assert.True(await Allowed("project.write", sibling.Id));
        var editable = await api.Client.GetFromJsonAsync<ScopeGrantDto[]>($"/api/v1/users/{target.Id}/scopes");
        Assert.Equal(permanent, Assert.Single(editable!));
        await using (var db = api.Context())
        {
            Assert.True(await new CollectionAuthorizationQuery(db).Scopes(new(target.Id, "admin-list"), "project.write")
                .AnyAsync(s => s.ProjectId == sibling.Id));
            var identity=await new AccountService(db,new PasswordHasher<UserRecord>()).IdentityAsync(target.Id,default);
            Assert.Contains(Grant(org),identity.Scopes);
            Assert.All(identity.Scopes,g=>Assert.Contains(g.AccessMode,new[]{"read","read_write"}));
        }
        using var revoked = await api.WriteAsync(HttpMethod.Put, endpoint, new { enabled = false }, appointed.Headers.ETag!.Tag);
        revoked.EnsureSuccessStatusCode();
        Assert.False(await Allowed("project.write", sibling.Id));
        Assert.Equal(organizationRead, await Allowed("project.read", sibling.Id));
        Assert.Equal(!organizationRead, await Allowed("project.write", api.Project.Id));
        var saved = Assert.Single(await setup.Set<UserProjectScope>().AsNoTracking().Where(g => g.UserId == target.Id).ToArrayAsync());
        Assert.Equal(permanent.Scope.ProjectId, saved.ProjectId);
        Assert.Equal(permanent.AccessMode, saved.AccessMode);
        Assert.Equal(hasGlobalRole, await setup.Set<UserRole>().AnyAsync(r => r.RoleId == administrator.Id && r.UserId == target.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_scope_edit_and_role_revocation_preserve_the_explicit_change(bool entireOrganization)
    {
        await using var api = await OpenAsync();
        var org = api.Organization.Id;
        await using var db = api.Context();
        var permanentRole = await db.Set<Role>().SingleAsync(r => r.Code == "ApiDeveloper" && r.OrganizationId == null);
        var adminRole = await db.Set<Role>().SingleAsync(r => r.Code == "OrganizationAdmin" && r.OrganizationId == null);
        AddMember(db, api, adminRole, Grant(org));
        var target = AddMember(db, api, permanentRole, Grant(org, "read"));
        await db.SaveChangesAsync();
        using var appointment = await api.WriteAsync(HttpMethod.Put, $"/api/v1/organizations/{org}/administrators/{target.Id}", new { enabled=true }, "\"1\"");
        appointment.EnsureSuccessStatusCode();
        var explicitProject = Grant(org, project: entireOrganization ? null : api.Project.Id);
        async Task<HttpResponseMessage> Edit(string tag) => await api.WriteAsync(HttpMethod.Put, $"/api/v1/users/{target.Id}/scopes",
            new SaveScopesRequest([explicitProject]), tag);
        async Task<HttpResponseMessage> Revoke(string tag) => await api.WriteAsync(HttpMethod.Put, $"/api/v1/users/{target.Id}/roles",
            new AssignRolesRequest([permanentRole.Id]), tag);
        var tag = appointment.Headers.ETag!.Tag;
        var responses = await Task.WhenAll(Edit(tag), Revoke(tag));
        using var edit = responses[0];
        using var revoke = responses[1];
        var successful = Assert.Single(responses, r => r.IsSuccessStatusCode);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
        using var retried = await (edit.IsSuccessStatusCode ? Revoke(successful.Headers.ETag!.Tag) : Edit(successful.Headers.ETag!.Tag));
        retried.EnsureSuccessStatusCode();
        var actor = new ActorContext(target.Id, "concurrent-handover");
        var auth = new AuthorizationService(db);
        Assert.True(await auth.CanAsync(actor, "api.edit", new("project", api.Project.Id, new(org,api.Project.Id))));
        Assert.Equal(entireOrganization, await auth.CanAsync(actor, "api.edit", new("organization", org, new(org))));
        Assert.Equal(explicitProject, Assert.Single((await api.Client.GetFromJsonAsync<ScopeGrantDto[]>($"/api/v1/users/{target.Id}/scopes"))!));
    }

    [Fact]
    public async Task Local_handover_preserves_a_later_explicit_write_grant_for_a_legacy_global_administrator()
    {
        await using var api = await OpenAsync();
        var org = api.Organization.Id;
        await using var db = api.Context();
        var legacy = await db.Set<Role>().SingleAsync(r => r.IsSystem && r.Code == "OrganizationAdmin");
        var member = await db.Set<Role>().SingleAsync(r => r.IsSystem && r.Code == "ApiDeveloper");
        AddMember(db, api, legacy, Grant(org));
        var target = AddMember(db, api, legacy, Grant(org, "read"));
        db.Add(new UserRole { UserId = target.Id, RoleId = member.Id });
        await db.SaveChangesAsync();
        var appointment = $"/api/v1/organizations/{org}/administrators/{target.Id}";
        using var added = await api.WriteAsync(HttpMethod.Put, appointment, new { enabled = true }, "\"1\"");
        added.EnsureSuccessStatusCode();
        using var manual = await api.WriteAsync(HttpMethod.Put, $"/api/v1/users/{target.Id}/scopes", new SaveScopesRequest([Grant(org)]), added.Headers.ETag!.Tag);
        manual.EnsureSuccessStatusCode();
        using var conflicting = await api.WriteAsync(HttpMethod.Put, appointment, new { enabled = false }, manual.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Equal("independent_organization_admin", (await conflicting.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        using var removeRoles = await api.WriteAsync(HttpMethod.Put, $"/api/v1/users/{target.Id}/roles", new AssignRolesRequest([member.Id]), manual.Headers.ETag.Tag);
        removeRoles.EnsureSuccessStatusCode();
        Assert.Equal(Grant(org), Assert.Single((await api.Client.GetFromJsonAsync<ScopeGrantDto[]>($"/api/v1/users/{target.Id}/scopes"))!));
        Assert.True(await new AuthorizationService(db).CanAsync(new(target.Id,"legacy-overlap"),"api.edit",new("organization",org,new(org))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Withdrawal_of_an_older_appointment_recovers_the_audited_scope_change(bool previouslyRead)
    {
        await using var api = await OpenAsync();
        await using var db = api.Context();
        var org = api.Organization.Id;
        var template = await db.Set<Role>().SingleAsync(r => r.IsSystem && r.Code == "OrganizationAdmin");
        var oldRole = new Role { OrganizationId = org, IsSystem = true, Code = "_OrganizationAdmin", Name = "组织管理员" };
        db.Add(oldRole);
        AddMember(db, api, template, Grant(org));
        var target = AddMember(db, api, oldRole, previouslyRead ? Grant(org, "read") : Grant(org, project: api.Project.Id));
        await db.SaveChangesAsync();
        await new AuditedCommandExecutor(db).ExecuteAsync(new(api.User.Id,"older-appointment"), new(org),"organization.administrator", async (_, ct) => {
            if (previouslyRead)
            {
                var original = await db.Set<UserProjectScope>().SingleAsync(g => g.UserId == target.Id, ct);
                original.AccessMode = "read_write";
                original.Revision++;
            }
            else db.Add(new UserProjectScope { UserId=target.Id, OrganizationId=org, AccessMode="read_write" });
            target.Revision++;
            return true;
        });
        using var revoked = await api.WriteAsync(HttpMethod.Put, $"/api/v1/organizations/{org}/administrators/{target.Id}", new {enabled=false}, "\"2\"");
        revoked.EnsureSuccessStatusCode();
        var remaining = Assert.Single(await db.Set<UserProjectScope>().AsNoTracking().Where(g => g.UserId == target.Id).ToArrayAsync());
        Assert.Equal(previouslyRead ? null : api.Project.Id, remaining.ProjectId);
        Assert.Equal(previouslyRead ? "read" : "read_write", remaining.AccessMode);
    }

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
    public async Task DisabledSsoProviderCannotReplaceTheLastUsableAdministrator()
    {
        await using var api = await OpenAsync();
        await using var db = api.Context();
        var organization = api.Organization.Id;
        var role = await db.Set<Role>().SingleAsync(row => row.Code == "OrganizationAdmin");
        var local = AddMember(db, api, role, Grant(organization));
        var external = AddMember(db, api, role, Grant(organization));
        external.AuthSource = "sso";
        external.PasswordHash = null;
        var provider = new SsoProvider
        {
            OrganizationId = organization, Issuer = "https://administrator-id.example.test", Name = "Administrator SSO",
            ClientId = "synthetic", SecretRef = "file://sso/synthetic"
        };
        db.AddRange(provider, new UserExternalIdentity
        {
            UserId = external.Id, ProviderId = provider.Id, Issuer = provider.Issuer, Subject = "replacement"
        });
        await db.SaveChangesAsync();

        await AssertConflictAsync(WriteAsync(api, local, "/roles", new AssignRolesRequest([])));

        provider.Enabled = true;
        await db.SaveChangesAsync();
        using var handover = await WriteAsync(api, local, "/roles", new AssignRolesRequest([]));
        Assert.Equal(HttpStatusCode.OK, handover.StatusCode);
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

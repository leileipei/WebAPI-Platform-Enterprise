using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CollectionAuthorizationQueryTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ApiFixture Api = new();
        internal readonly Organization[] Organizations = Enumerable.Range(0, 10).Select(i => new Organization { Code = "QO" + i, Name = "Query organization " + i, Status = i == 9 ? "Disabled" : "Active" }).ToArray();
        internal readonly List<Project> Projects = [];
        internal readonly List<EnvironmentRecord> Environments = [];
        internal readonly Dictionary<string, ActorContext> Actors = [];
        internal readonly List<AuthorizedScopeRow> AllScopes = [];
        internal Project Project(int org, int index) => Projects.Single(p => p.OrganizationId == Organizations[org].Id && p.Code == "QP" + index);
        internal EnvironmentRecord Environment(int org, int project, int index) => Environments.Single(e => e.ProjectId == Project(org, project).Id && e.Code == "QE" + index);
        internal AuthorizedScopeRow EnvironmentScope(int org, int project, int env) => new(Organizations[org].Id, Project(org, project).Id, Environment(org, project, env).Id);
        internal IEnumerable<AuthorizedScopeRow> OrganizationScopes(int org, bool governance) => AllScopes.Where(s => s.OrganizationId == Organizations[org].Id)
            .Where(s => governance || org != 9 && s.ProjectId != Project(0, 4).Id && s.EnvironmentId != Environment(0, 0, 2).Id);
        internal async Task InitializeAsync()
        {
            await Api.InitializeAsync(); await using var db = Api.Context(); db.AddRange(Organizations);
            for (var org = 0; org < 10; org++)
            {
                AllScopes.Add(new(Organizations[org].Id, null, null));
                for (var project = 0; project < 5; project++)
                {
                    var p = new Project { OrganizationId = Organizations[org].Id, Code = "QP" + project, Name = "Query project " + project, Status = org == 0 && project == 4 ? "Disabled" : "Active" }; Projects.Add(p); db.Add(p); AllScopes.Add(new(Organizations[org].Id, p.Id, null));
                    for (var env = 0; env < 3; env++)
                    {
                        var e = new EnvironmentRecord { ProjectId = p.Id, Code = "QE" + env, Name = "Query environment " + env, Status = org == 0 && project == 0 && env == 2 ? "Disabled" : "Active" }; Environments.Add(e); db.Add(e); AllScopes.Add(new(Organizations[org].Id, p.Id, e.Id));
                    }
                }
            }
            var permissions = new[] { "app.read", "app.write", "project.read" }.Select(code => new Permission { Code = code, Module = code.Split('.')[0], Name = code }).ToArray(); db.AddRange(permissions);
            void AddActor(string name, Guid? roleOrganization, bool role, bool system = false, bool disabled = false, params (int Org, int? Project, int? Env, string Mode)[] grants)
            {
                var user = new UserRecord { Username = "query-" + name, DisplayName = name, SecurityStamp = Guid.NewGuid().ToString("N"), Status = disabled ? "Disabled" : "Active" }; db.Add(user); Actors.Add(name, new(user.Id, "query-authorization-test"));
                if (role)
                {
                    var r = new Role { OrganizationId = roleOrganization, Code = system ? "PlatformAdmin" : "query-" + name, Name = name, IsSystem = system }; db.Add(r); db.Add(new UserRole { UserId = user.Id, RoleId = r.Id });
                    foreach (var p in permissions) db.Add(new RolePermission { RoleId = r.Id, PermissionId = p.Id });
                }
                foreach (var grant in grants) db.Add(new UserProjectScope { UserId = user.Id, OrganizationId = Organizations[grant.Org].Id, ProjectId = grant.Project is int p ? Project(grant.Org, p).Id : null, EnvironmentId = grant.Env is int e ? Environment(grant.Org, grant.Project!.Value, e).Id : null, AccessMode = grant.Mode });
            }
            AddActor("organization", null, true, grants: [(0, null, null, "read_write")]);
            AddActor("organization-role", Organizations[0].Id, true, grants: [(0, null, null, "read_write"), (1, null, null, "read_write")]);
            AddActor("project", null, true, grants: [(0, 0, null, "read")]);
            AddActor("environment", null, true, grants: [(0, 0, 0, "read")]);
            AddActor("no-function", null, false, grants: [(0, null, null, "read_write")]);
            AddActor("no-scope-admin", null, true, system: true);
            AddActor("cross-organization", Organizations[0].Id, true, grants: [(1, null, null, "read_write")]);
            AddActor("union", null, true, grants: [(0, null, null, "read"), (0, 0, 0, "read_write")]);
            AddActor("inactive-ancestor", null, true, grants: [(9, null, null, "read_write")]);
            AddActor("inactive-user", null, true, disabled: true, grants: [(0, null, null, "read_write")]);
            await db.SaveChangesAsync();
        }
        internal IEnumerable<AuthorizedScopeRow> Expected(string actor, string permission)
        {
            var governance = permission == "project.read"; var write = permission == "app.write";
            return actor switch
            {
                "organization" or "organization-role" => OrganizationScopes(0, governance),
                "project" => write ? [] : OrganizationScopes(0, governance).Where(s => s.ProjectId == Project(0, 0).Id),
                "environment" => write ? [] : [EnvironmentScope(0, 0, 0)],
                "union" => write ? [EnvironmentScope(0, 0, 0)] : OrganizationScopes(0, governance),
                "inactive-ancestor" => OrganizationScopes(9, governance),
                _ => []
            };
        }
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }
    private static string Key(AuthorizedScopeRow scope) => $"{scope.OrganizationId:D}:{scope.ProjectId}:{scope.EnvironmentId}";
    private static ResourceRef Resource(AuthorizedScopeRow scope) => new(scope.EnvironmentId is not null ? "environment" : scope.ProjectId is not null ? "project" : "organization", scope.EnvironmentId ?? scope.ProjectId ?? scope.OrganizationId, new(scope.OrganizationId, scope.ProjectId, scope.EnvironmentId));

    [Fact]
    public async Task ScopesMatchAuthorizationOracleAndExplicitExpectations()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await using var db = f.Api.Context(); var query = new CollectionAuthorizationQuery(db); var oracle = new AuthorizationService(db);
        Assert.Equal(210, f.AllScopes.Count);
        foreach (var name in f.Actors.Keys.Where(n => n != "inactive-user")) foreach (var permission in new[] { "app.read", "app.write", "project.read" })
        {
            var actor = f.Actors[name]; await query.RequireActiveActorAsync(actor); var actual = await query.Scopes(actor, permission).ToArrayAsync(); var expected = f.Expected(name, permission).ToArray();
            Assert.Equal(expected.Select(Key).Order(), actual.Select(Key).Order()); Assert.Equal(actual.Length, actual.Select(Key).Distinct().Count());
            var authorized = new List<AuthorizedScopeRow>(); foreach (var scope in f.AllScopes) if (await oracle.CanAsync(actor, permission, Resource(scope))) authorized.Add(scope);
            Assert.Equal(expected.Select(Key).Order(), authorized.Select(Key).Order());
        }
        using var services = f.Api.Services(); Assert.NotNull(services.ServiceProvider.GetRequiredService<CollectionAuthorizationQuery>());
    }

    [Fact]
    public async Task EnvironmentGrantDoesNotReadParent()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await using var db = f.Api.Context(); var actor = f.Actors["environment"];
        var visible = await new CollectionAuthorizationQuery(db).Scopes(actor, "app.read").ToArrayAsync(); Assert.Equal(f.EnvironmentScope(0, 0, 0), Assert.Single(visible));
        Assert.DoesNotContain(visible, s => s.ProjectId == f.Project(0, 0).Id && s.EnvironmentId == null); Assert.DoesNotContain(visible, s => s.ProjectId == null);
    }

    [Fact]
    public async Task RevocationIsVisibleOnNextRequest()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await using var db = f.Api.Context(); var actor = f.Actors["organization"]; var query = new CollectionAuthorizationQuery(db).Scopes(actor, "app.read");
        Assert.NotEmpty(await query.ToArrayAsync()); await db.Set<UserProjectScope>().Where(s => s.UserId == actor.UserId).ExecuteDeleteAsync(); Assert.Empty(await query.ToArrayAsync());
        var other = f.Actors["organization-role"]; var rolesQuery = new CollectionAuthorizationQuery(db).Scopes(other, "app.read"); Assert.NotEmpty(await rolesQuery.ToArrayAsync());
        await db.Set<UserRole>().Where(r => r.UserId == other.UserId).ExecuteDeleteAsync(); Assert.Empty(await rolesQuery.ToArrayAsync());
    }

    [Fact]
    public async Task OrganizationRoleCannotLeakAcrossOrganization()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await using var db = f.Api.Context(); var actor = f.Actors["organization-role"]; var query = new CollectionAuthorizationQuery(db);
        var visible = await query.Scopes(actor, "app.read").ToArrayAsync(); Assert.NotEmpty(visible); Assert.All(visible, s => Assert.Equal(f.Organizations[0].Id, s.OrganizationId));
        var roleId = await db.Set<UserRole>().Where(r => r.UserId == actor.UserId).Select(r => r.RoleId).SingleAsync(); await db.Set<Role>().Where(r => r.Id == roleId).ExecuteUpdateAsync(s => s.SetProperty(r => r.OrganizationId, f.Organizations[1].Id));
        var changed = await query.Scopes(actor, "app.read").ToArrayAsync(); Assert.Equal(f.OrganizationScopes(1, false).Select(Key).Order(), changed.Select(Key).Order());
    }

    [Fact]
    public async Task InactiveActorStill401()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await using var db = f.Api.Context(); var query = new CollectionAuthorizationQuery(db); var actor = f.Actors["inactive-user"];
        var error = await Assert.ThrowsAsync<ApiException>(() => query.RequireActiveActorAsync(actor)); Assert.Equal(401, error.Status); Assert.Equal("inactive_session", error.Code); Assert.Empty(await query.Scopes(actor, "app.read").ToArrayAsync());
    }

    [Fact]
    public async Task SystemPlatformRoleDoesNotBypassRealDataScope()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await using var db = f.Api.Context(); var actor = f.Actors["no-scope-admin"];
        Assert.True(await new AuthorizationService(db).CanAsync(actor, "app.read", new("platform", Guid.Empty, new(Guid.Empty))));
        Assert.Empty(await new CollectionAuthorizationQuery(db).Scopes(actor, "app.read").ToArrayAsync());
    }

    [Fact]
    public async Task ScopesComposeWithServerCountAndPaging()
    {
        await using var f = new Fixture(); await f.InitializeAsync(); await using var db = f.Api.Context(); var query = new CollectionAuthorizationQuery(db).Scopes(f.Actors["organization"], "app.read");
        Assert.Contains("EXISTS", query.ToQueryString(), StringComparison.OrdinalIgnoreCase);
        var projects = query.Where(s => s.ProjectId != null && s.EnvironmentId == null); Assert.Equal(4, await projects.CountAsync());
        var page = await projects.OrderBy(s => s.ProjectId).Skip(1).Take(2).ToArrayAsync();
        Assert.Equal(f.Projects.Where(p => p.OrganizationId == f.Organizations[0].Id && p.Status == "Active").Select(p => p.Id).OrderBy(id => id.ToString("D"), StringComparer.Ordinal).Skip(1).Take(2), page.Select(s => s.ProjectId!.Value));
    }
}

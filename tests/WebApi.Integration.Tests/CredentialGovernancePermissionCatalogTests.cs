using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CredentialGovernancePermissionCatalogTests
{
    [Fact]
    public async Task OnlyPlatformAdminReceivesNewRightsByDefaultAndReseedingPreservesScopedDelegation()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        await PermissionCatalog.SeedAsync(db);

        var revoke = await db.Set<Permission>().SingleAsync(x => x.Code == PermissionCatalog.CredentialRevoke);
        var recover = await db.Set<Permission>().SingleAsync(x => x.Code == PermissionCatalog.CredentialGovernanceRecover);
        var roles = await db.Set<Role>().Where(x => x.IsSystem).ToArrayAsync();
        var defaultGrants = await db.Set<RolePermission>()
            .Where(x => x.PermissionId == revoke.Id || x.PermissionId == recover.Id).ToArrayAsync();
        var platform = roles.Single(x => x.Code == "PlatformAdmin" && x.OrganizationId == null);
        Assert.Equal(2, defaultGrants.Length);
        Assert.All(defaultGrants, grant => Assert.Equal(platform.Id, grant.RoleId));

        var organization = new Organization { Code = "GOV", Name = "Governance" };
        var project = new Project { OrganizationId = organization.Id, Code = "KEY", Name = "Keys" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "A", Name = "Authorized" };
        var other = new EnvironmentRecord { ProjectId = project.Id, Code = "B", Name = "Outside grant" };
        var user = new UserRecord { Username = "credential-operator", DisplayName = "Credential operator" };
        var scoped = new Role { OrganizationId = organization.Id, Code = "CredentialOperator", Name = "Credential operator" };
        var platformCustom = new Role { Code = "RecoveryOperator", Name = "Custom recovery operator" };
        db.AddRange(organization, project, environment, other, user, scoped, platformCustom,
            new UserRole { UserId = user.Id, RoleId = scoped.Id },
            new UserRole { UserId = user.Id, RoleId = platformCustom.Id },
            new RolePermission { RoleId = scoped.Id, PermissionId = revoke.Id },
            new RolePermission { RoleId = platformCustom.Id, PermissionId = recover.Id },
            new UserProjectScope
            {
                UserId = user.Id, OrganizationId = organization.Id, ProjectId = project.Id,
                EnvironmentId = environment.Id, AccessMode = "read_write"
            });
        await db.SaveChangesAsync();
        await PermissionCatalog.SeedAsync(db);

        var actor = new ActorContext(user.Id, "credential-permissions");
        var authorization = new AuthorizationService(db);
        var credential = new ResourceRef("credential", Guid.NewGuid(), new(organization.Id, project.Id, environment.Id));
        await authorization.RequireAsync(actor, PermissionCatalog.CredentialRevoke, credential);

        var denied = await Assert.ThrowsAsync<ApiException>(() => authorization.RequireAsync(actor,
            PermissionCatalog.CredentialRevoke, credential with { Scope = new(organization.Id, project.Id, other.Id) }));
        Assert.Equal("scope_denied", denied.Code);
        var recoveryDenied = await Assert.ThrowsAsync<ApiException>(() => authorization.RequireAsync(actor,
            PermissionCatalog.CredentialGovernanceRecover, new("credential_governance", Guid.Empty, new(Guid.Empty))));
        Assert.Equal("platform_permission_required", recoveryDenied.Code);
    }
}

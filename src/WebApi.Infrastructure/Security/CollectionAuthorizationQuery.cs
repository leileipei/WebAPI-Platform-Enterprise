using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Security;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Security;

public sealed record AuthorizedScopeRow(Guid OrganizationId, Guid? ProjectId, Guid? EnvironmentId)
{
    internal AuthorizedScopeRow() : this(Guid.Empty, null, null) { }
}

public sealed class CollectionAuthorizationQuery(WebApiDbContext db)
{
    public async Task RequireActiveActorAsync(ActorContext actor, CancellationToken ct = default)
    {
        await new PasswordLifecycleGuard(db).RequireBusinessAccessAsync(actor,ct);
    }

    public IQueryable<AuthorizedScopeRow> Scopes(ActorContext actor, string permission)
    {
        var read = permission.EndsWith(".read", StringComparison.Ordinal);
        var governance = permission.StartsWith("organization.", StringComparison.Ordinal)
            || permission.StartsWith("project.", StringComparison.Ordinal)
            || permission.StartsWith("environment.", StringComparison.Ordinal);

        var organizations = db.Set<Organization>().AsNoTracking()
            .Where(organization => governance || organization.Status == "Active")
            .Select(organization => new AuthorizedScopeRow { OrganizationId = organization.Id, ProjectId = null, EnvironmentId = null });
        var projects = from project in db.Set<Project>().AsNoTracking()
                       join organization in db.Set<Organization>() on project.OrganizationId equals organization.Id
                       where governance || project.Status == "Active" && organization.Status == "Active"
                       select new AuthorizedScopeRow { OrganizationId = organization.Id, ProjectId = project.Id, EnvironmentId = null };
        var environments = from environment in db.Set<EnvironmentRecord>().AsNoTracking()
                           join project in db.Set<Project>() on environment.ProjectId equals project.Id
                           join organization in db.Set<Organization>() on project.OrganizationId equals organization.Id
                           where governance || environment.Status == "Active" && project.Status == "Active" && organization.Status == "Active"
                           select new AuthorizedScopeRow { OrganizationId = organization.Id, ProjectId = project.Id, EnvironmentId = environment.Id };

        var functionalOrganizations = from membership in db.Set<UserRole>()
                                      join role in db.Set<Role>() on membership.RoleId equals role.Id
                                      join rolePermission in db.Set<RolePermission>() on role.Id equals rolePermission.RoleId
                                      join grantedPermission in db.Set<Permission>() on rolePermission.PermissionId equals grantedPermission.Id
                                      where membership.UserId == actor.UserId && grantedPermission.Code == permission
                                      select role.OrganizationId;

        // Each row is a real scope. A grant limited to an environment never creates
        // a parent scope, and EXISTS avoids duplicates from overlapping grants.
        return organizations.Concat(projects).Concat(environments).Where(scope =>
            db.Set<UserRecord>().Any(user => user.Id == actor.UserId && user.Status == "Active" && !user.MustChangePassword)
            && functionalOrganizations.Any(organization => organization == null || organization == scope.OrganizationId)
            && db.Set<UserProjectScope>().Any(grant => grant.UserId == actor.UserId && grant.OrganizationId == scope.OrganizationId
                && (grant.ProjectId == null || grant.ProjectId == scope.ProjectId)
                && (grant.EnvironmentId == null || grant.EnvironmentId == scope.EnvironmentId)
                && (read || grant.AccessMode == "read_write")));
    }
}

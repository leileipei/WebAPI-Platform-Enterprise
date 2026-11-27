using Microsoft.EntityFrameworkCore;
using WebApi.Domain.Security;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Sso;
public static class SsoAdmission
{
    public static async Task<bool> HasOrganizationAccessAsync(WebApiDbContext db,Guid? organizationId,Guid userId,CancellationToken ct=default)
        => organizationId is null || await ActiveOrganizationGrants(db)
            .AnyAsync(scope => scope.UserId == userId && scope.OrganizationId == organizationId, ct);

    internal static IQueryable<UserProjectScope> ActiveOrganizationGrants(WebApiDbContext db)
        => db.Set<UserProjectScope>().Where(scope =>
            db.Set<Organization>().Any(org => org.Id == scope.OrganizationId && org.Status == "Active")
            && (scope.AccessMode == "read" || scope.AccessMode == "read_write" || scope.AccessMode == ScopeMatcher.OrganizationAdministratorWrite)
            && (scope.ProjectId == null || db.Set<Project>().Any(project =>
                project.Id == scope.ProjectId && project.OrganizationId == scope.OrganizationId && project.Status == "Active"))
            && (scope.EnvironmentId == null || scope.ProjectId != null && db.Set<EnvironmentRecord>().Any(env =>
                env.Id == scope.EnvironmentId && env.ProjectId == scope.ProjectId && env.Status == "Active")));
}

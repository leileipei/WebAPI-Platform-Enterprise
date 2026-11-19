using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Runtime;

internal static class RuntimeEnvironmentAccess
{
    internal const string ArchiveRetiredNodeEvent = "environment_archive_retired";

    internal static IQueryable<Guid> RetiredNodeIds(WebApiDbContext db) =>
        db.Set<GatewayNodeEvent>().AsNoTracking().Where(item => item.EventType == ArchiveRetiredNodeEvent)
            .Select(item => item.GatewayNodeId);

    internal static IQueryable<EnvironmentRecord> ActiveEnvironments(WebApiDbContext db) =>
        from project in db.Set<Project>().AsNoTracking()
        join environment in db.Set<EnvironmentRecord>().AsNoTracking() on project.Id equals environment.ProjectId
        join organization in db.Set<Organization>().AsNoTracking() on project.OrganizationId equals organization.Id
        where environment.Status == "Active" && project.Status == "Active" && organization.Status == "Active"
        select environment;
}

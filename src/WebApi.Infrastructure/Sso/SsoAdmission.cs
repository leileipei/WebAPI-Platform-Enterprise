using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Sso;
public static class SsoAdmission
{
    public static async Task<bool> HasOrganizationAccessAsync(WebApiDbContext db,Guid? organizationId,Guid userId,CancellationToken ct=default)
    {
        if(organizationId is null)return true;
        if(!await db.Set<Organization>().AnyAsync(x=>x.Id==organizationId&&x.Status=="Active",ct))return false;
        return await db.Set<UserProjectScope>().AnyAsync(scope=>scope.UserId==userId&&scope.OrganizationId==organizationId&&
            (scope.AccessMode=="read"||scope.AccessMode=="read_write")&&
            (scope.ProjectId==null||db.Set<Project>().Any(project=>project.Id==scope.ProjectId&&project.OrganizationId==organizationId&&project.Status=="Active"))&&
            (scope.EnvironmentId==null||scope.ProjectId!=null&&db.Set<EnvironmentRecord>().Any(env=>env.Id==scope.EnvironmentId&&env.ProjectId==scope.ProjectId&&env.Status=="Active")),ct);
    }
}

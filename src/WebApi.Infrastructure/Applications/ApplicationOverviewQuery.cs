using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Applications;
using WebApi.Contracts.CredentialUsage;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.CredentialUsage;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Applications;

public sealed class ApplicationOverviewQuery(WebApiDbContext db,CollectionAuthorizationQuery visibility,CredentialUsageQuery usage,TimeProvider clock)
{
    private sealed record AuthorizedApplication(Guid Id,Guid OrganizationId,Guid? ProjectId);
    public async Task<ApplicationOverviewDto> ReadAsync(Guid id,Guid? environmentId,UsageWindow window,ActorContext actor,CancellationToken ct=default)
    {
        var facts=await usage.ReadAsync(id,environmentId,window,1,50,actor,ct);
        var app=await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync(x=>x.Id==id,ct);var summaries=await SummariesAsync([app],actor,ct);
        return new(ApplicationService.Dto(app,summaries[id]),summaries[id],facts);
    }
    internal async Task<IReadOnlyDictionary<Guid,ApplicationSummaryDto>> SummariesAsync(IReadOnlyList<ApplicationRecord> applications,ActorContext actor,CancellationToken ct=default)
    {
        if(applications.Count>100)throw new ArgumentOutOfRangeException(nameof(applications));if(applications.Count==0)return new Dictionary<Guid,ApplicationSummaryDto>();
        var ids=applications.Select(x=>x.Id).Distinct().ToArray();var now=clock.GetUtcNow();var authorized=visibility.Scopes(actor,"app.read");
        // Only callers that have already authorized these exact application rows can enter this helper.
        // Carry their trusted scope metadata in one bounded relational value, without rereading the list.
        var json=JsonSerializer.Serialize(applications.Select(x=>new{id=x.Id,organization_id=x.OrganizationId,project_id=x.ProjectId}));
        var apps=db.Database.SqlQuery<AuthorizedApplication>($"SELECT id AS \"Id\",organization_id AS \"OrganizationId\",project_id AS \"ProjectId\" FROM jsonb_to_recordset({json}::jsonb) AS v(id uuid,organization_id uuid,project_id uuid)");
        var credentials=await(from key in db.Set<ApplicationCredential>().AsNoTracking() where ids.Contains(key.ApplicationId) group key by key.ApplicationId into g select new{Id=g.Key,Total=g.Count(),Available=g.Count(x=>x.Status=="Active"&&x.ValidFrom<=now&&x.ExpiresAt>now)}).ToArrayAsync(ct);
        var permissions=await(from grant in db.Set<ApplicationApiPermission>().AsNoTracking() join app in apps on grant.ApplicationId equals app.Id join environment in db.Set<EnvironmentRecord>() on grant.EnvironmentId equals environment.Id join project in db.Set<Project>() on environment.ProjectId equals project.Id join api in db.Set<Api>() on grant.ApiId equals api.Id where api.OrganizationId==app.OrganizationId&&api.ProjectId==project.Id&&grant.ValidFrom<=now&&(grant.ExpiresAt==null||grant.ExpiresAt>now)&&project.OrganizationId==app.OrganizationId&&(app.ProjectId==null||app.ProjectId==project.Id)&&authorized.Any(s=>s.OrganizationId==app.OrganizationId&&s.ProjectId==project.Id&&s.EnvironmentId==environment.Id) group grant by grant.ApplicationId into g select new{Id=g.Key,Count=g.Select(x=>x.ApiId).Distinct().Count()}).ToArrayAsync(ct);
        return ids.ToDictionary(id=>id,id=>new ApplicationSummaryDto(permissions.SingleOrDefault(x=>x.Id==id)?.Count??0,credentials.SingleOrDefault(x=>x.Id==id)?.Available??0,credentials.SingleOrDefault(x=>x.Id==id)?.Total??0));
    }
}

using System.Globalization;
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.CredentialUsage;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.CredentialUsage;

public sealed class CredentialUsageQuery(WebApiDbContext db,AuthorizationService auth,ScopeResolver scopes,CollectionAuthorizationQuery visibility,UsageCoverageReader coverage,CredentialUsageRetentionOptions options,TimeProvider clock)
{
    private sealed record Totals(Guid CredentialId,string Count,DateTimeOffset LastObservedAt);
    public async Task<CredentialUsagePage> ReadAsync(Guid applicationId,Guid? environmentId,UsageWindow window,int page,int pageSize,ActorContext actor,CancellationToken ct=default)
    {
        var environments=await VisibleEnvironmentsAsync(applicationId,actor,ct);
        if(environmentId is Guid selected&&!environments.Contains(selected))throw new ApiException(403,"usage_environment_denied","环境范围不允许此查询。");
        if(environmentId is Guid only)environments=[only];
        window=new(window.From.ToUniversalTime(),window.To.ToUniversalTime());var now=clock.GetUtcNow();
        if(page<1||page>1000000||pageSize<1||pageSize>100||window.From>=window.To||window.From!=UsageCoverageReader.Floor(window.From)||window.To!=UsageCoverageReader.Floor(window.To)||window.To-window.From>TimeSpan.FromDays(30)||window.From<UsageCoverageReader.Floor(now.AddDays(-options.RetentionDays))||window.To>UsageCoverageReader.Floor(now).AddHours(1))throw new ApiException(422,"invalid_usage_window","统计窗口必须为UTC整点、在保留期内且不超过30天；分页范围不合法。");
        var credentials=db.Set<ApplicationCredential>().AsNoTracking().Where(x=>x.ApplicationId==applicationId);var total=await credentials.CountAsync(ct);
        var rows=await credentials.OrderBy(x=>x.AccessKey).ThenBy(x=>x.Id).Skip((page-1)*pageSize).Take(pageSize).Select(x=>new{x.Id,x.AccessKey}).ToArrayAsync(ct);var ids=rows.Select(x=>x.Id).ToArray();
        var facts=ids.Length==0||environments.Length==0?[]:await db.Database.SqlQuery<Totals>($"SELECT credential_id AS \"CredentialId\",SUM(count::numeric)::text AS \"Count\",MAX(last_used_at) AS \"LastObservedAt\" FROM credential_usage_hourly WHERE application_id={applicationId} AND credential_id=ANY({ids}) AND environment_id=ANY({environments}) AND hour_start>={window.From} AND hour_start<{window.To} GROUP BY credential_id").ToArrayAsync(ct);
        var visibleLast=await LastUsedCoreAsync(applicationId,ids,environments,ct);var source=await coverage.ReadAsync(environments,window,ct);
        var values=rows.Select(row=>
        {
            var fact=facts.SingleOrDefault(x=>x.CredentialId==row.Id);var known=source.State is "Available" or "NoData" or "Partial";string? count=null;
            if(known){var number=BigInteger.Parse(fact?.Count??"0",NumberStyles.None,CultureInfo.InvariantCulture);if(number<0)throw new InvalidDataException("Invalid negative usage aggregate.");count=number.ToString(CultureInfo.InvariantCulture);}
            var status=source.State=="Available"&&count=="0"?source with{State="NoData"}:source;
            return new CredentialUsageRow(row.Id,row.AccessKey.Length>4?row.AccessKey[^4..]:row.AccessKey,count,fact?.LastObservedAt,visibleLast.GetValueOrDefault(row.Id),status);
        }).ToArray();
        return new(window,environments,new(values,total,page,pageSize),source);
    }
    public async Task<IReadOnlyDictionary<Guid,DateTimeOffset?>> VisibleLastUsedAsync(Guid applicationId,IReadOnlyList<Guid> credentialIds,ActorContext actor,CancellationToken ct=default)
    {
        Guid[] environments;
        try{environments=await VisibleEnvironmentsAsync(applicationId,actor,ct);}
        catch(ApiException error) when(error.Status==403&&error.Code is "permission_denied" or "scope_denied")
        {return new Dictionary<Guid,DateTimeOffset?>();} // A manager-only write remains permitted, without a read projection.
        return await LastUsedCoreAsync(applicationId,credentialIds.Distinct().ToArray(),environments,ct);
    }
    private async Task<Dictionary<Guid,DateTimeOffset?>> LastUsedCoreAsync(Guid applicationId,Guid[] credentialIds,Guid[] environments,CancellationToken ct)
    {
        if(credentialIds.Length==0||environments.Length==0)return [];
        var oldest=UsageCoverageReader.Floor(clock.GetUtcNow().AddDays(-options.RetentionDays));var end=UsageCoverageReader.Floor(clock.GetUtcNow()).AddHours(1);
        var facts=await db.Set<CredentialUsageHourly>().AsNoTracking().Where(x=>x.ApplicationId==applicationId&&credentialIds.Contains(x.CredentialId)&&environments.Contains(x.EnvironmentId)&&x.HourStart>=oldest&&x.HourStart<end).GroupBy(x=>x.CredentialId).Select(g=>new{Id=g.Key,Last=(DateTimeOffset?)g.Max(x=>x.LastUsedAt)}).ToArrayAsync(ct);
        return facts.ToDictionary(x=>x.Id,x=>x.Last);
    }
    private async Task<Guid[]> VisibleEnvironmentsAsync(Guid applicationId,ActorContext actor,CancellationToken ct)
    {
        var scope=await scopes.ApplicationAsync(applicationId,ct);await auth.RequireAsync(actor,"app.read",new("application",applicationId,scope),ct);
        var authorized=visibility.Scopes(actor,"app.read");
        return await (from environment in db.Set<EnvironmentRecord>().AsNoTracking() join project in db.Set<Project>() on environment.ProjectId equals project.Id where project.OrganizationId==scope.OrganizationId&&(scope.ProjectId==null||project.Id==scope.ProjectId)&&authorized.Any(x=>x.EnvironmentId==environment.Id&&x.ProjectId==project.Id&&x.OrganizationId==project.OrganizationId) orderby environment.Id select environment.Id).ToArrayAsync(ct);
    }
}

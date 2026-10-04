using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Alerts;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Observability;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Security;
namespace WebApi.Infrastructure.Alerts;
public sealed class AlertRuleScopeResolver(WebApiDbContext db,AuthorizationService authorization)
{
    public static ScopeRef Scope(SaveAlertRuleRequest r)=>new(r.OrganizationId,r.ProjectId,r.EnvironmentId);
    public async Task<IReadOnlyList<Guid>> ResolveForActorAsync(ActorContext actor,SaveAlertRuleRequest r,CancellationToken ct)
    {
        await authorization.RequireAsync(actor,"alert.rule.manage",new("alert_rule",r.EnvironmentId??r.ProjectId??r.OrganizationId,Scope(r)),ct);
        await ValidateReferencesAsync(r,ct);
        return await ActiveEnvironmentsAsync(r,ct);
    }
    internal async Task AuthorizeExistingAsync(ActorContext actor,AlertRule rule,CancellationToken ct)
    {
        var scope=new ScopeRef(rule.OrganizationId,rule.ProjectId,rule.EnvironmentId);
        await authorization.RequireAsync(actor,"alert.rule.manage",new("alert_rule",rule.Id,scope),ct);
        // Frozen governance ownership remains manageable after a polymorphic target is retired.
        // Newly submitted target references are validated separately before any write.
    }
    public async Task<IReadOnlyList<Guid>> ResolveForSystemAsync(AlertRule rule,CancellationToken ct)
    {
        // A persisted rule is a system fact. Never synthesize a browser actor or borrow its creator's grants.
        if(!await db.Set<Organization>().AnyAsync(x=>x.Id==rule.OrganizationId&&x.Status=="Active",ct))return [];
        return await ActiveEnvironmentsAsync(AlertRuleService.Definition(rule),ct);
    }
    private async Task<Guid[]> ActiveEnvironmentsAsync(SaveAlertRuleRequest r,CancellationToken ct)=>await (
        from e in db.Set<EnvironmentRecord>().AsNoTracking() join p in db.Set<Project>().AsNoTracking() on e.ProjectId equals p.Id
        where p.OrganizationId==r.OrganizationId&&p.Status=="Active"&&e.Status=="Active"&&(r.ProjectId==null||p.Id==r.ProjectId)&&(r.EnvironmentId==null||e.Id==r.EnvironmentId)
        select e.Id).OrderBy(x=>x).ToArrayAsync(ct);
    private async Task ValidateReferencesAsync(SaveAlertRuleRequest r,CancellationToken ct)
    {
        if(r.EnvironmentId is not null&&r.ProjectId is null)throw Invalid("环境必须指定项目。");
        if(r.ProjectId is Guid project&&!await db.Set<Project>().AnyAsync(x=>x.Id==project&&x.OrganizationId==r.OrganizationId,ct))throw ScopeResolver.Missing();
        if(r.EnvironmentId is Guid environment&&!await db.Set<EnvironmentRecord>().AnyAsync(x=>x.Id==environment&&x.ProjectId==r.ProjectId,ct))throw ScopeResolver.Missing();
        switch(r.TargetType)
        {
            case "Environment":if(r.TargetId is not null)throw Invalid("Environment目标不能指定TargetId。");break;
            case "Api":
                if(r.ProjectId is null||r.TargetId is null)throw Invalid("API目标必须指定项目和API。");
                if(!await db.Set<Api>().AnyAsync(x=>x.Id==r.TargetId&&x.ProjectId==r.ProjectId&&x.OrganizationId==r.OrganizationId,ct))throw ScopeResolver.Missing();break;
            case "Destination":
                if(r.ProjectId is null||r.EnvironmentId is null||r.TargetId is null)throw Invalid("后端目标必须限定单个环境。");
                if(!await(from d in db.Set<UpstreamDestination>() join c in db.Set<UpstreamCluster>() on d.ClusterId equals c.Id where d.Id==r.TargetId&&c.ProjectId==r.ProjectId&&c.EnvironmentId==r.EnvironmentId select d.Id).AnyAsync(ct))throw ScopeResolver.Missing();break;
            default:throw Invalid("不支持的规则目标。");
        }
    }
    internal async Task<TrustedObservationScope> ObservationAsync(Guid organizationId,Guid environmentId,CancellationToken ct)
    {
        var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(x=>x.Id==environmentId,ct);
        var apis=await db.Set<Api>().AsNoTracking().Where(x=>x.OrganizationId==organizationId&&x.ProjectId==env.ProjectId).ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);
        var apps=await db.Set<ApplicationRecord>().AsNoTracking().Where(x=>x.OrganizationId==organizationId&&x.ProjectId==env.ProjectId).ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);
        var destinations=await(from d in db.Set<UpstreamDestination>().AsNoTracking() join c in db.Set<UpstreamCluster>().AsNoTracking() on d.ClusterId equals c.Id where c.ProjectId==env.ProjectId&&c.EnvironmentId==env.Id select new ObservationDestination(d.Id,c.Id,c.EnvironmentId,d.Name,d.Enabled)).ToDictionaryAsync(x=>x.Id,ct);
        var nodes=await db.Set<GatewayNode>().AsNoTracking().Where(x=>x.EnvironmentId==env.Id&&x.Enabled).Select(x=>new ExpectedObservationNode(x.EnvironmentId,x.NodeName)).ToArrayAsync(ct);
        return new(organizationId,env.ProjectId,[env.Id],nodes,apis,apps,destinations);
    }
    private static ApiException Invalid(string message)=>new(422,"invalid_alert_rule",message);
}

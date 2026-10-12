using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Alerts;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;

namespace WebApi.Infrastructure.Alerts;

public sealed class AlertListReader(WebApiDbContext db,AlertEvaluationSettings settings)
{
    public async Task<IReadOnlyList<AlertRuleDto>> ReadRulesAsync(IReadOnlyList<AlertRule> rules,CancellationToken ct)
    {
        if(rules.Count==0)return [];
        var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);var result=new List<AlertRuleDto>();foreach(var item in rules){
        var organizations=new[]{item.OrganizationId};var ids=new[]{item.Id};
        // System coverage is derived from rule ownership, never from the requesting actor's environment list.
        var active=await (from e in db.Set<EnvironmentRecord>().AsNoTracking() join p in db.Set<Project>().AsNoTracking() on e.ProjectId equals p.Id
            join o in db.Set<Organization>().AsNoTracking() on p.OrganizationId equals o.Id
            where organizations.Contains(o.Id)&&o.Status=="Active"&&p.Status=="Active"&&e.Status=="Active"
            select new {OrganizationId=o.Id,ProjectId=p.Id,EnvironmentId=e.Id}).ToArrayAsync(ct);
        var states=await (from state in db.Set<AlertEvaluationState>().AsNoTracking() join rule in db.Set<AlertRule>().AsNoTracking() on state.RuleId equals rule.Id
            join env in db.Set<EnvironmentRecord>().AsNoTracking() on state.EnvironmentId equals env.Id
            join project in db.Set<Project>().AsNoTracking() on env.ProjectId equals project.Id
            join organization in db.Set<Organization>().AsNoTracking() on project.OrganizationId equals organization.Id
            where ids.Contains(rule.Id)&&state.LogicRevision==rule.LogicRevision
                &&state.ResourceKey==(rule.TargetType=="Environment"?"Environment":rule.TargetType+":"+rule.TargetId!.Value.ToString())
                &&organization.Id==rule.OrganizationId&&organization.Status=="Active"&&project.Status=="Active"&&env.Status=="Active"
                &&(rule.ProjectId==null||rule.ProjectId==project.Id)&&(rule.EnvironmentId==null||rule.EnvironmentId==env.Id)
            select new {state.RuleId,state.EnvironmentId,state.EvaluationState,state.LastSuccessAt,state.LastEvaluatedSlot}).ToArrayAsync(ct);
        var byRule=states.ToLookup(s=>s.RuleId);var environments=active.ToLookup(e=>e.OrganizationId);
        result.AddRange(new[]{item}.Select(rule=>{
            var expected=environments[rule.OrganizationId].Where(e=>(rule.ProjectId==null||rule.ProjectId==e.ProjectId)&&(rule.EnvironmentId==null||rule.EnvironmentId==e.EnvironmentId)).Select(e=>e.EnvironmentId).ToHashSet();
            var actual=byRule[rule.Id].Where(s=>expected.Contains(s.EnvironmentId)).ToArray();
            var successful=expected.Count>0&&actual.Length==expected.Count&&actual.All(s=>s.LastSuccessAt is not null);
            var complete=successful&&actual.All(s=>s.EvaluationState=="Known"&&s.LastEvaluatedSlot is not null&&s.LastEvaluatedSlot<=now&&now-s.LastEvaluatedSlot<=TimeSpan.FromSeconds(settings.IntervalSeconds*2));
            return new AlertRuleDto(rule.Id,AlertRuleService.Definition(rule),rule.Revision,rule.LogicRevision,!rule.Enabled?"Disabled":expected.Count==0?"ScopeInactive":complete?"Known":"Unknown",successful?actual.Min(s=>s.LastSuccessAt):null);
        }).ToArray());}
        return result;
    }
    public async Task<IReadOnlyList<AlertEventDto>> ReadEventsAsync(IReadOnlyList<AlertEvent> events,CancellationToken ct)
    {
        if(events.Count==0)return [];
        var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);var result=new List<AlertEventDto>();foreach(var item in events){var ids=new[]{item.Id};
        var transitions=(await db.Set<AlertEventTransition>().AsNoTracking().Where(t=>ids.Contains(t.EventId)).OrderBy(t=>t.OccurredAt).ThenBy(t=>t.Id).ToArrayAsync(ct)).ToLookup(t=>t.EventId);
        var slots=await (from e in db.Set<AlertEvent>().AsNoTracking() join state in db.Set<AlertEvaluationState>().AsNoTracking()
            on new {e.RuleId,e.LogicRevision,e.EnvironmentId,e.ResourceKey} equals new {state.RuleId,state.LogicRevision,state.EnvironmentId,state.ResourceKey}
            where ids.Contains(e.Id)&&e.Status!="Resolved"&&e.EvaluationState=="Known"
            select new {e.Id,state.LastEvaluatedSlot}).ToDictionaryAsync(e=>e.Id,e=>e.LastEvaluatedSlot,ct);
        result.AddRange(new[]{item}.Select(e=>{
            var evaluation=e.EvaluationState;
            if(e.Status!="Resolved"&&evaluation=="Known") {slots.TryGetValue(e.Id,out var slot);if(slot is null||slot>now||now-slot>TimeSpan.FromSeconds(settings.IntervalSeconds*2))evaluation="Unknown";}
            var history=transitions[e.Id].Select(t=>new AlertTransitionDto(t.Id,t.FromStatus,t.ToStatus,t.ActorId,t.Reason,t.OccurredAt,t.CorrelationId)).ToArray();
            return new AlertEventDto(e.Id,e.RuleId,e.RuleRevision,e.LogicRevision,AlertEventService.Scope(e),e.ResourceKey,e.ResourceType,e.ResourceId,e.OccurrenceNo,e.Status,e.Severity,e.Message,e.RuleSummary,e.StartedAt,e.ConditionStartedAt,e.ResolvedAt,e.AckedBy,e.AckedAt,e.SilencedBy,e.SilencedUntil,e.SilenceReason,e.ResolvedBy,e.ResolveReason,e.LastObservedAt,evaluation=="Known"?e.LastValue:null,evaluation=="Known"?e.LastCondition:null,evaluation,e.Revision,history);
        }).ToArray());}
        return result;
    }
}

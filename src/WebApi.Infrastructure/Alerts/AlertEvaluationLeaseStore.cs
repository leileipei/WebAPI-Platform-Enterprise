using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Alerts;
public sealed record AlertEvaluationSettings(int IntervalSeconds=15,int QueryDelaySeconds=30,int LeaseSeconds=30,int MaxConcurrentEvaluations=4)
{
    public static AlertEvaluationSettings Read(IConfiguration config)
    {
        int Get(string key,int fallback)=>int.TryParse(config["Alerts:"+key],out var value)?value:fallback;
        var settings=new AlertEvaluationSettings(Get("IntervalSeconds",15),Get("QueryDelaySeconds",30),Get("LeaseSeconds",30),Get("MaxConcurrentEvaluations",4));
        if(settings.IntervalSeconds is <1 or >60||settings.QueryDelaySeconds is <0 or >120||settings.LeaseSeconds is <15 or >120||settings.MaxConcurrentEvaluations is <1 or >16)throw new InvalidOperationException("Invalid alert evaluation settings.");return settings;
    }
}
public sealed record EvaluationLease(Guid RuleId,long RuleRevision,long LogicRevision,Guid EnvironmentId,string ResourceKey,DateTimeOffset Slot,long Token,DateTimeOffset LeaseUntil);
public sealed class AlertEvaluationLeaseStore(WebApiDbContext db,AlertRuleScopeResolver scopes,AlertEvaluationSettings settings)
{
    internal static Task<DateTimeOffset> DatabaseTimeAsync(WebApiDbContext db,CancellationToken ct)=>db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
    private DateTimeOffset Floor(DateTimeOffset time)=>DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds()/settings.IntervalSeconds*settings.IntervalSeconds);
    public async Task<DateTimeOffset> CurrentSlotAsync(CancellationToken ct)=>Floor(await DatabaseTimeAsync(db,ct));
    public async Task<IReadOnlyList<EvaluationLease>> ClaimAsync(DateTimeOffset slot,string owner,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(owner)||owner.Length>128)throw new ArgumentException("Invalid lease owner.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);
        var now=await DatabaseTimeAsync(db,ct);if(slot!=Floor(now)){await tx.CommitAsync(ct);return [];}
        // Seed missing current-logic keys from persisted rules, not from existing occurrences.
        var rules=await db.Set<AlertRule>().FromSqlRaw("SELECT * FROM alert_rules WHERE enabled ORDER BY id FOR UPDATE").ToArrayAsync(ct);
        foreach(var rule in rules)
        {
            var definition=AlertRuleService.Definition(rule);var key=AlertRuleService.ResourceKey(definition);var active=await scopes.ResolveForSystemAsync(rule,ct);
            var existing=await db.Set<AlertEvaluationState>().Where(x=>x.RuleId==rule.Id&&x.LogicRevision==rule.LogicRevision&&x.ResourceKey==key).Select(x=>x.EnvironmentId).ToArrayAsync(ct);
            foreach(var environment in active.Except(existing))
            {
                var project=await db.Set<EnvironmentRecord>().Where(x=>x.Id==environment).Select(x=>x.ProjectId).SingleAsync(ct);
                db.Add(new AlertEvaluationState{RuleId=rule.Id,LogicRevision=rule.LogicRevision,OrganizationId=rule.OrganizationId,ProjectId=project,EnvironmentId=environment,ResourceKey=key,ResourceType=rule.TargetType,ResourceId=rule.TargetId});
            }
        }
        await db.SaveChangesAsync(ct);
        var ids=rules.Select(x=>x.Id).ToArray();
        var candidates=await db.Set<AlertEvaluationState>().Where(x=>ids.Contains(x.RuleId)&&(x.LastEvaluatedSlot==null||x.LastEvaluatedSlot<slot)&&(x.LeaseUntil==null||x.LeaseUntil<=now)).OrderBy(x=>x.LastEvaluatedSlot).ThenBy(x=>x.Id).ToArrayAsync(ct);
        var leases=new List<EvaluationLease>();
        foreach(var state in candidates)
        {
            var rule=rules.Single(x=>x.Id==state.RuleId);if(state.LogicRevision!=rule.LogicRevision||state.ResourceKey!=AlertRuleService.ResourceKey(AlertRuleService.Definition(rule)))continue;
            // Governance lock serializes claims; row lock is still required for every writer's lock order.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_evaluation_states WHERE id={state.Id} FOR UPDATE",ct);
            state.LeaseToken++;state.LeaseOwner=owner;state.LeaseUntil=now.AddSeconds(settings.LeaseSeconds);state.Revision++;
            leases.Add(new(rule.Id,rule.Revision,rule.LogicRevision,state.EnvironmentId,state.ResourceKey,slot,state.LeaseToken,state.LeaseUntil.Value));
            if(leases.Count>=settings.MaxConcurrentEvaluations)break;
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return leases;
    }
}

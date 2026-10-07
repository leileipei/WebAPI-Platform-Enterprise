using Microsoft.EntityFrameworkCore;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Notifications;
namespace WebApi.Infrastructure.Alerts;
public sealed class AlertSilenceExpiryService(WebApiDbContext db,NotificationPlanner notifications)
{
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8901202)",ct);var now=await AlertEvaluationLeaseStore.DatabaseTimeAsync(db,ct);
        var candidates=await db.Set<AlertEvent>().AsNoTracking().Where(x=>x.Status=="Silenced"&&x.SilencedUntil<=now).OrderBy(x=>x.RuleId).ThenBy(x=>x.Id).Take(100).ToArrayAsync(ct);var count=0;
        foreach(var candidate in candidates)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_rules WHERE id={candidate.RuleId} FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM alert_evaluation_states WHERE rule_id={candidate.RuleId} AND logic_revision={candidate.LogicRevision} AND environment_id={candidate.EnvironmentId} AND resource_key={candidate.ResourceKey} FOR UPDATE",ct);
            var e=await db.Set<AlertEvent>().FromSqlInterpolated($"SELECT * FROM alert_events WHERE id={candidate.Id} FOR UPDATE").SingleAsync(ct);
            if(e.Status!="Silenced"||e.SilencedUntil is null||e.SilencedUntil>now)continue;
            e.Status=e.AckedBy is null?"Open":"Ack";e.SilencedUntil=null;e.Revision++;var correlation="expiry:"+e.Id.ToString("N");
            var transition=new AlertEventTransition{EventId=e.Id,FromStatus="Silenced",ToStatus=e.Status,Reason="SilenceExpired",OccurredAt=now,CorrelationId=correlation};db.Add(transition);await notifications.OnTransitionAsync(e,transition,ct);AlertSystemAudit.Add(db,e,"alert.silence_expired","SilenceExpired",now,correlation);count++;
        }
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return count;
    }
}

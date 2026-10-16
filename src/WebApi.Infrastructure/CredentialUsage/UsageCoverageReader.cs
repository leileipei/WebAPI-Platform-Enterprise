using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.CredentialUsage;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.CredentialUsage;

public sealed class UsageCoverageReader(WebApiDbContext db,TimeProvider clock)
{
    public async Task<UsageCoverageDto> ReadAsync(IReadOnlyList<Guid> environmentIds,UsageWindow window,CancellationToken ct=default)
    {
        if(environmentIds.Count==0)return Unknown("NoVisibleEnvironment");
        var ids=environmentIds.Distinct().ToArray();
        var cohort=db.Set<GatewayNode>().AsNoTracking().Where(x=>ids.Contains(x.EnvironmentId));
        var nodes=await cohort.Select(x=>new{x.Id,x.EnvironmentId}).ToArrayAsync(ct);var nodeIds=nodes.Select(x=>x.Id).ToArray();
        var eventSource=db.Set<GatewayNodeEvent>().AsNoTracking().Where(x=>nodeIds.Contains(x.GatewayNodeId)&&x.CreatedAt<window.To&&(x.EventType=="registered"||x.EventType=="usage.node.enabled"||x.EventType=="usage.node.disabled")).Select(x=>new{x.Id,x.GatewayNodeId,x.EventType,x.CreatedAt});
        // Two anchors per node preserve first-cohort and pre-window enable/retire semantics.
        // All other events and source instances must overlap the requested window.
        var registrations=cohort.SelectMany(node=>eventSource.Where(x=>x.GatewayNodeId==node.Id&&x.EventType=="registered").OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).Take(1));
        var priorStates=cohort.SelectMany(node=>eventSource.Where(x=>x.GatewayNodeId==node.Id&&x.CreatedAt<window.From).OrderByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Id).Take(1));
        var events=await eventSource.Where(x=>x.CreatedAt>=window.From).Union(registrations).Union(priorStates).OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).ToArrayAsync(ct);
        var instances=await (from instance in db.Set<CredentialUsageCollectorInstance>().AsNoTracking() join collector in db.Set<CredentialUsageCollector>() on new{instance.NodeId,instance.CollectorId} equals new{collector.NodeId,collector.CollectorId} where ids.Contains(collector.EnvironmentId)&&collector.Protocol=="1.0"&&instance.RegisteredAt<window.To&&(instance.EndedAt==null||instance.EndedAt>window.From) select new{instance.NodeId,instance.RegisteredAt,instance.EndedAt}).ToArrayAsync(ct);
        var rows=await db.Set<CredentialUsageCoverage>().AsNoTracking().Where(x=>ids.Contains(x.EnvironmentId)&&x.HourStart>=window.From&&x.HourStart<window.To).ToArrayAsync(ct);
        var missing=new HashSet<Guid>();var gaps=new List<UsageGap>();var unknown=false;var unavailable=false;DateTimeOffset? coverageStart=null;DateTimeOffset? freshness=null;
        foreach(var environment in ids)
        {
            var relevant=nodes.Where(x=>x.EnvironmentId==environment).ToArray();
            // No current online-node shortcut: a pre-window registration proves the cohort existed.
            var registered=events.Where(e=>e.EventType=="registered"&&relevant.Any(n=>n.Id==e.GatewayNodeId)).Select(e=>(DateTimeOffset?)e.CreatedAt).Min();
            if(registered is null)unknown=true;
            else if(registered>window.From)gaps.Add(new(window.From,registered.Value,"CoverageStartedLate"));
            foreach(var node in relevant)
            {
                var history=events.Where(e=>e.GatewayNodeId==node.Id).ToArray();var first=history.FirstOrDefault(e=>e.EventType=="registered");
                if(first is null){if(rows.Any(x=>x.NodeId==node.Id))unknown=true;continue;}
                var active=false;DateTimeOffset? start=null;var required=new List<UsageInterval>();
                foreach(var entry in history)
                {
                    if(entry.EventType=="usage.node.disabled")
                    {if(active&&start is {} begin&&begin<entry.CreatedAt)required.Add(new(begin,entry.CreatedAt));active=false;start=null;}
                    else if(!active){active=true;start=entry.CreatedAt;}
                }
                if(active&&start is {} final&&final<window.To)required.Add(new(final,window.To));
                foreach(var interval in required)
                {
                    var begin=Max(interval.From,window.From);var end=Min(interval.To,window.To);if(begin>=end)continue;
                    // An old Gateway with no usage protocol cannot prove idle zero.
                    if(!instances.Any(x=>x.NodeId==node.Id&&x.RegisteredAt<=begin&&(x.EndedAt is null||x.EndedAt>begin))){missing.Add(node.Id);continue;}
                    for(var hour=Floor(begin);hour<end;hour=hour.AddHours(1))
                    {
                        var neededFrom=Max(begin,hour);var neededTo=Min(end,hour.AddHours(1));var row=rows.SingleOrDefault(x=>x.NodeId==node.Id&&x.HourStart==hour);
                        if(row is null){missing.Add(node.Id);continue;}
                        freshness=freshness is null?row.UpdatedAt:Min(freshness.Value,row.UpdatedAt);
                        if(row.State=="Unavailable"){unavailable=true;continue;}if(row.State=="Unknown")unknown=true;
                        try
                        {
                            var intervals=JsonSerializer.Deserialize<UsageInterval[]>(row.Intervals,UsageProtocolJson.Options)??[];var losses=JsonSerializer.Deserialize<UsageGap[]>(row.Gaps,UsageProtocolJson.Options)??[];
                            if(intervals.Any(x=>x.From>=x.To||x.From<hour||x.To>hour.AddHours(1))||losses.Any(x=>x.From>=x.To))throw new JsonException();
                            foreach(var loss in losses.Where(x=>x.From<neededTo&&x.To>neededFrom))gaps.Add(loss with{From=Max(neededFrom,loss.From),To=Min(neededTo,loss.To)});
                            var cursor=neededFrom;foreach(var covered in intervals.OrderBy(x=>x.From)){coverageStart=coverageStart is null?covered.From:Min(coverageStart.Value,covered.From);if(covered.From<=cursor&&covered.To>cursor)cursor=covered.To;}
                            if(cursor<neededTo||row.AcceptedThrough is null||row.AcceptedThrough<neededTo||row.DurableThrough is null||row.DurableThrough<neededTo)missing.Add(node.Id);
                        }
                        catch(JsonException){unavailable=true;}
                    }
                }
            }
        }
        var current=window.To>Floor(clock.GetUtcNow());var state=unavailable?"Unavailable":unknown?"Unknown":missing.Count>0||gaps.Count>0||current?"Partial":"Available";
        return new(state,coverageStart,state=="Available"?window.To:null,freshness,missing.Order().ToArray(),gaps.Distinct().OrderBy(x=>x.From).ThenBy(x=>x.Reason,StringComparer.Ordinal).ToArray(),unavailable?"InvalidOrUnavailableSource":unknown?"HistoricalCohortUnproven":current?"CurrentHourInProgress":missing.Count>0?"MissingNodeCoverage":gaps.Count>0?"CollectionGap":null);
    }
    private static UsageCoverageDto Unknown(string reason)=>new("Unknown",null,null,null,[],[],reason);
    internal static DateTimeOffset Floor(DateTimeOffset value){var utc=value.ToUniversalTime();return new(utc.Year,utc.Month,utc.Day,utc.Hour,0,0,TimeSpan.Zero);}
    private static DateTimeOffset Min(DateTimeOffset a,DateTimeOffset b)=>a<b?a:b;
    private static DateTimeOffset Max(DateTimeOffset a,DateTimeOffset b)=>a>b?a:b;
}

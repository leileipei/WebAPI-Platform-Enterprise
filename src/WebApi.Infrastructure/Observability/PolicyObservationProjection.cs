using System.Globalization;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Infrastructure.Governance;
namespace WebApi.Infrastructure.Observability;
public sealed record PolicyObservationRead(IReadOnlyList<PolicyDecisionDto> Decisions,bool Rejected);
public static class PolicyObservationProjection
{
    public static bool Visible(TrustedObservationScope scope,Guid env,Guid id)=>scope.PolicyIdsByEnvironment is not null&&scope.PolicyIdsByEnvironment.TryGetValue(env,out var ids)&&ids.Contains(id);
    public static PolicyObservationRead Read(IReadOnlyDictionary<string,string> attributes,TrustedObservationScope scope,Guid environment)
    {
        var decisions=new List<PolicyDecisionDto>();var rejected=false;
        foreach(var type in PolicyDecisionValues.Types) {
            var prefix="webapi.policy."+type+".";string? Text(string field)=>attributes.GetValueOrDefault(prefix+field);
            if(!attributes.Keys.Any(k=>k.StartsWith(prefix,StringComparison.Ordinal))) continue;
            if(!Guid.TryParse(Text("id"),out var id)||!Visible(scope,environment,id)||!long.TryParse(Text("revision"),NumberStyles.None,CultureInfo.InvariantCulture,out var revision)||Text("type")!=type) {rejected=true;continue;}
            var rawCount=Text("attempt_count");int? count=null;
            if(rawCount is not null){if(!int.TryParse(rawCount,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)){rejected=true;continue;}count=parsed;}
            var decision=new PolicyDecisionDto(id,type,revision,Text("decision")??"",Text("rejection_reason"),Text("cache_read"),Text("cache_write"),count);if(!PolicyDecisionValues.Valid(decision)) {rejected=true;continue;}decisions.Add(decision);
        }
        return new(decisions,rejected);
    }
    public sealed record AdvancedRead(int? AttemptCount,string? CacheDisposition,IReadOnlyList<ForwardAttemptObservation>? Attempts,bool Rejected);
    public static AdvancedRead ReadAdvanced(IReadOnlyDictionary<string,string> attributes,TrustedObservationScope scope,Guid environment)
    {
        var rejected=false;int? count=null;string? cache=null;IReadOnlyList<ForwardAttemptObservation>? attempts=null;
        if(attributes.TryGetValue("webapi.attempt.count",out var text)){if(int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var n)&&n is >=0 and <=3)count=n;else rejected=true;}
        if(attributes.TryGetValue("webapi.cache.disposition",out var disposition)){if(disposition is "Hit" or "Miss" or "Bypass" or "Stored")cache=disposition;else rejected=true;}
        if(attributes.TryGetValue("webapi.forward.attempts",out var raw))
        {
            try
            {
                if(raw.Length>4096||count is null)throw new FormatException();
                using var document=JsonDocument.Parse(raw,new JsonDocumentOptions{MaxDepth=4});var array=document.RootElement;
                if(array.ValueKind!=JsonValueKind.Array||array.GetArrayLength()>3||array.GetArrayLength()!=count)throw new FormatException();
                var result=new List<ForwardAttemptObservation>();
                foreach(var item in array.EnumerateArray())
                {
                    if(item.ValueKind!=JsonValueKind.Object)throw new FormatException();var names=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var field in item.EnumerateObject())if(!names.Add(field.Name)||field.Name is not("number" or "destinationId" or "status" or "outcome" or "durationSeconds"))throw new FormatException();
                    if(names.Count!=5)throw new FormatException();
                    var destination=item.GetProperty("destinationId");var status=item.GetProperty("status");
                    var attempt=new ForwardAttemptObservation(item.GetProperty("number").GetInt32(),destination.ValueKind==JsonValueKind.Null?null:destination.GetGuid(),status.ValueKind==JsonValueKind.Null?null:status.GetInt32(),item.GetProperty("outcome").GetString()??"",item.GetProperty("durationSeconds").GetDouble());
                    if(!ForwardAttemptObservation.Valid(attempt)||attempt.Number!=result.Count+1||attempt.DestinationId is Guid id&&(!scope.Destinations.TryGetValue(id,out var visible)||visible.EnvironmentId!=environment))throw new FormatException();result.Add(attempt);
                }
                attempts=result;
            }
            catch(Exception e)when(e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException){rejected=true;}
        }
        if(cache=="Hit"&&count is not 0){cache=null;rejected=true;}
        return new(count,cache,attempts,rejected);
    }
    public static void RequireFilter(TrustedObservationScope scope,Guid? id,string? decision)
    {
        if(decision is not null&&!PolicyDecisionValues.Types.Any(t=>PolicyDecisionValues.Allowed(t,decision))) throw new ApiException(422,"invalid_policy_decision_filter","策略决策筛选值不合法。");
        if(id is Guid policy&&!scope.EnvironmentIds.Any(env=>Visible(scope,env,policy))) throw ScopeResolver.Missing();
    }
}

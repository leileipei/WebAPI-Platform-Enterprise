using System.Globalization;
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
            var decision=new PolicyDecisionDto(id,type,revision,Text("decision")??"",Text("rejection_reason"));if(!PolicyDecisionValues.Valid(decision)) {rejected=true;continue;}decisions.Add(decision);
        }
        return new(decisions,rejected);
    }
    public static void RequireFilter(TrustedObservationScope scope,Guid? id,string? decision)
    {
        if(decision is not null&&!PolicyDecisionValues.Types.Any(t=>PolicyDecisionValues.Allowed(t,decision))) throw new ApiException(422,"invalid_policy_decision_filter","策略决策筛选值不合法。");
        if(id is Guid policy&&!scope.EnvironmentIds.Any(env=>Visible(scope,env,policy))) throw ScopeResolver.Missing();
    }
}

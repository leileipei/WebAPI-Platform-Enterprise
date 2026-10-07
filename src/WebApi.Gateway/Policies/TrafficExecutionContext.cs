using WebApi.Contracts.Runtime;
using WebApi.Gateway.Security;
using WebApi.Gateway.Configuration;
namespace WebApi.Gateway.Policies;
public sealed record TrafficPolicyDecision(Guid PolicyId,string PolicyType,long PolicyRevision,string Decision,string? RejectionReason=null);
public sealed class TrafficExecutionContext(RuntimeGeneration generation,RuntimeRoute route)
{
    private static readonly object Item=new();private readonly List<TrafficPolicyDecision> decisions=[];
    public RuntimeGeneration Generation {get;}=generation;public RuntimeRoute Route {get;}=route;public Guid? ApplicationId {get;set;}
    public VerifiedTrafficIdentity? VerifiedIdentity {get;internal set;}
    public IReadOnlyList<TrafficPolicyDecision> Decisions=>decisions.AsReadOnly();
    public void Record(RuntimePolicy policy,string decision,string? reason=null)
    {if(decisions.Count>=2||decisions.Any(d=>d.PolicyType==policy.Type)) throw new InvalidOperationException("Traffic decisions are bounded to one per policy type.");decisions.Add(new(policy.SourcePolicyId!.Value,policy.Type,policy.SourceRevision!.Value,decision,reason));}
    public static TrafficExecutionContext? From(HttpContext context)=>context.Items.TryGetValue(Item,out var value)?value as TrafficExecutionContext:null;
    public static TrafficExecutionContext Attach(HttpContext context,RuntimeGeneration generation,RuntimeRoute route)
    {var value=new TrafficExecutionContext(generation,route);context.Items[Item]=value;return value;}
}

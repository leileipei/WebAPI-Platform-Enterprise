using WebApi.Contracts.Runtime;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Gateway.Security;
using WebApi.Gateway.Configuration;
namespace WebApi.Gateway.Policies;
public sealed record TrafficPolicyDecision(Guid PolicyId,string PolicyType,long PolicyRevision,string Decision,string? RejectionReason=null,string? CacheRead=null,string? CacheWrite=null,int? AttemptCount=null)
{public PolicyDecisionDto ToDto()=>new(PolicyId,PolicyType,PolicyRevision,Decision,RejectionReason,CacheRead,CacheWrite,AttemptCount);}
public sealed class TrafficExecutionContext(RuntimeGeneration generation,RuntimeRoute route)
{
    private static readonly object Item=new();private readonly List<TrafficPolicyDecision> decisions=[];private readonly List<ForwardAttemptObservation> attempts=[];
    public RuntimeGeneration Generation {get;}=generation;public RuntimeRoute Route {get;}=route;public Guid? ApplicationId {get;set;}
    public CircuitAdmission? CircuitAdmission {get;internal set;}
    public CircuitBreakerState? CircuitState {get;internal set;}
    internal string? CacheDisposition {get;set;}
    public VerifiedTrafficIdentity? VerifiedIdentity {get;internal set;}
    public IReadOnlyList<ForwardAttemptObservation> Attempts=>attempts.AsReadOnly();
    public void AddAttempt(ForwardAttemptObservation attempt){if(attempts.Count>=3||attempt.Number!=attempts.Count+1||!ForwardAttemptObservation.Valid(attempt))throw new InvalidOperationException("Forward observations are bounded to three physical attempts.");attempts.Add(attempt);}
    public IReadOnlyList<TrafficPolicyDecision> Decisions=>decisions.AsReadOnly();
    public void Record(RuntimePolicy policy,string decision,string? reason=null,string? cacheRead=null,string? cacheWrite=null,int? attemptCount=null)
    {if(decisions.Count>=5||decisions.Any(d=>d.PolicyType==policy.Type)) throw new InvalidOperationException("Traffic decisions are bounded to one per policy type.");var item=new TrafficPolicyDecision(policy.SourcePolicyId!.Value,policy.Type,policy.SourceRevision!.Value,decision,reason,cacheRead,cacheWrite,attemptCount);if(!PolicyDecisionValues.Valid(item.ToDto()))throw new InvalidOperationException("Uncontrolled policy observation.");decisions.Add(item);}
    public static TrafficExecutionContext? From(HttpContext context)=>context.Items.TryGetValue(Item,out var value)?value as TrafficExecutionContext:null;
    public static TrafficExecutionContext Attach(HttpContext context,RuntimeGeneration generation,RuntimeRoute route)
    {var value=new TrafficExecutionContext(generation,route);context.Items[Item]=value;return value;}
}

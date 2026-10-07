using System.Text.Json.Serialization;
namespace WebApi.Contracts.Policies;
public sealed record PolicyDecisionDto(Guid PolicyId,string PolicyType,long PolicyRevision,string Decision,string? RejectionReason,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? CacheRead=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? CacheWrite=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]int? AttemptCount=null);
public sealed record ForwardAttemptObservation(int Number,Guid? DestinationId,int? Status,string Outcome,double DurationSeconds)
{
    public static bool Valid(ForwardAttemptObservation value)=>value.Number is >=1 and <=3&&value.DestinationId!=Guid.Empty
        &&(value.Status is null or >=100 and <=599)&&value.Outcome is "Completed" or "ProxyError" or "Timeout" or "ClientAborted"
        &&double.IsFinite(value.DurationSeconds)&&value.DurationSeconds is >=0 and <=3600;
}
public static class PolicyDecisionValues
{
    public static readonly string[] Types=["authentication","rate_limit","circuit_breaker","retry","cache"];
    public static readonly string[] Fields=["id","type","revision","decision","rejection_reason","cache_read","cache_write","attempt_count"];
    public static bool Allowed(string type,string decision)=>type switch {
        "authentication"=>decision is "Allowed" or "Rejected",
        "rate_limit"=>decision is "Allowed" or "Exceeded" or "StoreRejected" or "Bypass",
        "circuit_breaker"=>decision is "Allowed" or "HalfOpenProbe" or "OpenRejected" or "CacheSkipped",
        "retry"=>decision is "Retried" or "Exhausted" or "Bypass",
        "cache"=>decision is "Hit" or "Miss" or "Bypass" or "Stored",_=>false};
    public static bool Valid(PolicyDecisionDto value)
    {
        if(value.PolicyId==Guid.Empty||value.PolicyRevision<1||!Allowed(value.PolicyType,value.Decision))return false;
        if(value.PolicyType!="cache"&&(value.CacheRead is not null||value.CacheWrite is not null)||value.PolicyType!="retry"&&value.AttemptCount is not null)return false;
        return value.PolicyType switch{
            "authentication"=>value.Decision=="Allowed"?value.RejectionReason is null:value.RejectionReason is "invalid_jwt" or "api_not_granted" or "invalid_api_key",
            "rate_limit"=>value.RejectionReason==(value.Decision switch{"Exceeded"=>"rate_limit_exceeded","StoreRejected" or "Bypass"=>"rate_limit_store_unavailable",_=>null}),
            "circuit_breaker"=>value.RejectionReason==(value.Decision switch{"OpenRejected"=>"circuit_open","CacheSkipped"=>"cache_hit",_=>null}),
            "retry"=>(value.AttemptCount is null or >=0 and <=3)&&(value.Decision=="Bypass"?value.RejectionReason is "cache_hit" or "unsafe_request" or "single_attempt" or "circuit_probe" or "no_destination" or "client_cancelled" or "not_replayable" or "no_retry_needed":value.RejectionReason is null),
            "cache"=>(value.CacheRead is null or "Hit" or "Miss" or "Bypass")&&(value.CacheWrite is null or "Stored" or "Bypass")
                &&(value.Decision=="Bypass"?value.RejectionReason is "cache_store_unavailable" or "unsafe_request" or "cache_key_budget":value.RejectionReason is null)
                &&(value.Decision!="Hit"||value.CacheRead=="Hit"&&value.CacheWrite is null)&&(value.Decision!="Stored"||value.CacheRead=="Miss"&&value.CacheWrite=="Stored"),_=>false};
    }
}

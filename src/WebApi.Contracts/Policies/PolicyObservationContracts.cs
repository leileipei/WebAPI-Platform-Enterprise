namespace WebApi.Contracts.Policies;
public sealed record PolicyDecisionDto(Guid PolicyId,string PolicyType,long PolicyRevision,string Decision,string? RejectionReason);
public static class PolicyDecisionValues
{
    public static readonly string[] Types=["rate_limit","circuit_breaker"];
    public static bool Allowed(string type,string decision)=>type switch {"rate_limit"=>decision is "Allowed" or "Exceeded" or "StoreRejected" or "Bypass","circuit_breaker"=>decision is "Allowed" or "HalfOpenProbe" or "OpenRejected",_=>false};
    public static bool Valid(PolicyDecisionDto value)
    {if(value.PolicyId==Guid.Empty||value.PolicyRevision<1||!Allowed(value.PolicyType,value.Decision)) return false;var reason=value.Decision switch {"Exceeded"=>"rate_limit_exceeded","StoreRejected" or "Bypass"=>"rate_limit_store_unavailable","OpenRejected"=>"circuit_open",_=>null};return value.RejectionReason==reason;}
}

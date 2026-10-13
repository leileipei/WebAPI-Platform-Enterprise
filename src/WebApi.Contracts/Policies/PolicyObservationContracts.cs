using System.Text.Json.Serialization;
namespace WebApi.Contracts.Policies;
public sealed record PolicyDecisionDto(Guid PolicyId,string PolicyType,long PolicyRevision,string Decision,string? RejectionReason,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? CacheRead=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]string? CacheWrite=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]int? AttemptCount=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]int? OperationIndex=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]int? BodyOperationCount=null);
public sealed record ForwardAttemptObservation(int Number,Guid? DestinationId,int? Status,string Outcome,double DurationSeconds)
{
    public static bool Valid(ForwardAttemptObservation value)=>value.Number is >=1 and <=3&&value.DestinationId!=Guid.Empty
        &&(value.Status is null or >=100 and <=599)&&value.Outcome is "Completed" or "ProxyError" or "Timeout" or "ClientAborted"
        &&double.IsFinite(value.DurationSeconds)&&value.DurationSeconds is >=0 and <=3600;
}
public static class PolicyDecisionValues
{
    public const int MaximumPerRequest=8;
    public static readonly string[] Types=["authentication","rate_limit","circuit_breaker","retry","cache","cors","ip_filter","transform"];
    public static readonly string[] Fields=["id","type","revision","decision","rejection_reason","cache_read","cache_write","attempt_count","operation_index","body_operation_count"];
    public static bool Allowed(string type,string decision)=>type switch {
        "authentication"=>decision is "Allowed" or "Rejected",
        "rate_limit"=>decision is "Allowed" or "Exceeded" or "StoreRejected" or "Bypass",
        "circuit_breaker"=>decision is "Allowed" or "HalfOpenProbe" or "OpenRejected" or "CacheSkipped",
        "retry"=>decision is "Retried" or "Exhausted" or "Bypass",
        "cache"=>decision is "Hit" or "Miss" or "Bypass" or "Stored",
        "cors"=>decision is "Allowed" or "Preflight" or "Bypass" or "Rejected",
        "ip_filter"=>decision is "Allowed" or "Rejected",
        "transform"=>decision is "Applied" or "Rejected" or "Bypass",_=>false};
    public static bool Valid(PolicyDecisionDto value)
    {
        if(value.PolicyId==Guid.Empty||value.PolicyRevision<1||!Allowed(value.PolicyType,value.Decision))return false;
        if(value.PolicyType!="cache"&&(value.CacheRead is not null||value.CacheWrite is not null)||value.PolicyType!="retry"&&value.AttemptCount is not null)return false;
        if(value.PolicyType!="transform"&&(value.OperationIndex is not null||value.BodyOperationCount is not null)
            ||value.OperationIndex is <1 or >32||value.BodyOperationCount is <0 or >64)return false;
        return value.PolicyType switch{
            "authentication"=>value.Decision=="Allowed"?value.RejectionReason is null:value.RejectionReason is "invalid_jwt" or "api_not_granted" or "invalid_api_key",
            "rate_limit"=>value.RejectionReason==(value.Decision switch{"Exceeded"=>"rate_limit_exceeded","StoreRejected" or "Bypass"=>"rate_limit_store_unavailable",_=>null}),
            "circuit_breaker"=>value.RejectionReason==(value.Decision switch{"OpenRejected"=>"circuit_open","CacheSkipped"=>"cache_hit",_=>null}),
            "retry"=>(value.AttemptCount is null or >=0 and <=3)&&(value.Decision=="Bypass"?value.RejectionReason is "cache_hit" or "unsafe_request" or "single_attempt" or "circuit_probe" or "no_destination" or "client_cancelled" or "not_replayable" or "no_retry_needed" or "response_transform_rejected":value.RejectionReason is null),
            "cors"=>value.Decision switch {"Allowed" or "Preflight"=>value.RejectionReason is null,"Bypass"=>value.RejectionReason=="no_origin",_=>value.RejectionReason is "cors_origin_rejected" or "cors_method_rejected" or "cors_headers_rejected" or "cors_request_invalid"},
            "ip_filter"=>value.Decision=="Allowed"?value.RejectionReason is null:value.RejectionReason is "ip_not_allowed" or "ip_denied" or "client_ip_unavailable",
            "transform"=>value.Decision switch {
                "Applied"=>value.RejectionReason is null,
                "Bypass"=>value.RejectionReason is "cache_hit" or "no_upstream_response" or "hop_by_hop_protected" or "body_method_not_selected" or "body_status_not_selected" or "head_response",
                _=>value.RejectionReason is "transform_path_mismatch" or "transform_path_invalid" or "transform_budget_exceeded" or "transform_json_invalid" or "transform_json_operation_failed" or "transform_body_too_large" or "transform_body_media_unsupported" or "transform_body_encoding_unsupported" or "transform_body_condition_unsupported" or "transform_body_stream_unsupported" or "transform_body_trailer_unsupported" or "transform_body_signature_unsupported" or "transform_body_busy"},
            "cache"=>(value.CacheRead is null or "Hit" or "Miss" or "Bypass")&&(value.CacheWrite is null or "Stored" or "Bypass")
                &&(value.Decision=="Bypass"?value.RejectionReason is "cache_store_unavailable" or "unsafe_request" or "cache_key_budget":value.RejectionReason is null)
                &&(value.Decision!="Hit"||value.CacheRead=="Hit"&&value.CacheWrite is null)&&(value.Decision!="Stored"||value.CacheRead=="Miss"&&value.CacheWrite=="Stored"),_=>false};
    }
}

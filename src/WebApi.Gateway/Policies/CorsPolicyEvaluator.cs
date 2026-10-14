using WebApi.Contracts.Policies;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;

namespace WebApi.Gateway.Policies;

public sealed record CorsEvaluation(bool Allowed,bool Preflight,string? Origin,string? Reason);

public static class CorsPolicyEvaluator
{
    internal static bool HasSingleRequestedMethod(HttpRequest request)
    {
        var requested=request.Headers["Access-Control-Request-Method"];
        return requested.Count==1&&!string.IsNullOrEmpty(requested[0])&&requested[0]!.All(CorsPolicyConfiguration.IsTokenChar);
    }
    public static CorsEvaluation Evaluate(HttpRequest request,RuntimeRoute route,CorsConfiguration configuration)
    {
        var preflight=HttpMethods.IsOptions(request.Method)&&request.Headers.ContainsKey("Origin")
            &&request.Headers.ContainsKey("Access-Control-Request-Method");
        CorsEvaluation Reject(string reason)=>new(false,preflight,null,reason);
        if(!request.Headers.TryGetValue("Origin",out var origins))return new(true,false,null,"no_origin");
        if(origins.Count!=1)return Reject("cors_request_invalid");
        string origin;
        try{origin=CorsPolicyConfiguration.NormalizeOrigin(origins[0]!);}
        catch(ApiException){return Reject("cors_request_invalid");}
        if(!configuration.AllowedOrigins.Contains("*")&&!configuration.AllowedOrigins.Contains(origin,StringComparer.Ordinal))return Reject("cors_origin_rejected");
        var method=request.Method;
        if(preflight)
        {
            var requested=request.Headers["Access-Control-Request-Method"];
            if(!HasSingleRequestedMethod(request))return Reject("cors_request_invalid");
            method=requested[0]!;
        }
        if(!route.Methods.Contains(method,StringComparer.Ordinal)||!configuration.AllowedMethods.Contains(method,StringComparer.Ordinal))return Reject("cors_method_rejected");
        if(preflight&&request.Headers.TryGetValue("Access-Control-Request-Headers",out var requestedHeaders))
        {
            var headers=requestedHeaders.ToString().Split(',').Select(h=>h.Trim()).ToArray();
            if(headers.Length>64||headers.Any(h=>h.Length==0||h.Length>128||!h.All(CorsPolicyConfiguration.IsTokenChar))
                ||headers.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=headers.Length)return Reject("cors_request_invalid");
            if(headers.Any(h=>!configuration.AllowedHeaders.Contains(h,StringComparer.OrdinalIgnoreCase)))return Reject("cors_headers_rejected");
        }
        return new(true,preflight,origin,null);
    }
}

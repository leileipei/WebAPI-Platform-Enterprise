using Microsoft.AspNetCore.Routing.Matching;
using WebApi.Gateway.Policies;
using Yarp.ReverseProxy.Model;

namespace WebApi.Gateway.Configuration;

// YARP ExactHeader accepts individual comma-separated values. A malformed CORS method
// can therefore match several derived routes before the policy gets to reject it.
public sealed class CorsPreflightMatcherPolicy : MatcherPolicy,IEndpointSelectorPolicy
{
    internal const string MetadataKey="webapi.cors-preflight";
    // Run after YARP's header matcher (-50), before the final endpoint selector.
    public override int Order=>0;
    private static bool Derived(Endpoint endpoint)=>endpoint.Metadata.GetMetadata<RouteModel>()?.Config.Metadata
        ?.TryGetValue(MetadataKey,out var value)==true&&value=="true";
    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)=>endpoints.Any(Derived);
    public Task ApplyAsync(HttpContext context,CandidateSet candidates)
    {
        var request=context.Request;
        if(!HttpMethods.IsOptions(request.Method)||!request.Headers.ContainsKey("Origin")
            ||!request.Headers.ContainsKey("Access-Control-Request-Method")||CorsPolicyEvaluator.HasSingleRequestedMethod(request))return Task.CompletedTask;
        var first=-1;var score=-1;
        for(var i=0;i<candidates.Count;i++)
        {
            if(!candidates.IsValidCandidate(i))continue;
            if(first<0){first=i;score=candidates[i].Score;}
            if(candidates[i].Score!=score)break;
            // Never resolve an ambiguity involving a business endpoint by choosing a CORS route.
            if(!Derived(candidates[i].Endpoint))return Task.CompletedTask;
        }
        if(first<0)return Task.CompletedTask;
        for(var i=first+1;i<candidates.Count;i++)
            if(candidates.IsValidCandidate(i)&&candidates[i].Score==score)candidates.SetValidity(i,false);
        // The selected original generation still runs IP first, then rejects the raw CORS
        // method with the existing controlled response. No auth, cache or backend is reached.
        return Task.CompletedTask;
    }
}

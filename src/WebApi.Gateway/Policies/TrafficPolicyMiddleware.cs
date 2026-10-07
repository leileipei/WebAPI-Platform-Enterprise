using System.Globalization;
using WebApi.Domain.Policies;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Policies;
public sealed class TrafficPolicyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx,IRateLimitStore limiter,CircuitStateRegistry circuits)
    {
        var execution=TrafficExecutionContext.From(ctx)??throw new InvalidOperationException("Authorized traffic context is unavailable.");var generation=execution.Generation;var route=execution.Route;
        var bound=(route.PolicyBindings??[]).Select(b=>generation.PoliciesById[b.PolicyId]).ToArray();
        var rate=bound.SingleOrDefault(p=>p.Type=="rate_limit");
        if(rate is not null) {
            var config=generation.RateLimits[rate.Id];var decision=await limiter.TakeAsync(new(generation.Snapshot.EnvironmentId,rate.Id,route.Id,execution.ApplicationId,config),ctx.RequestAborted);
            if(decision.Kind==RateLimitDecisionKind.Exceeded) {execution.Record(rate,"Exceeded","rate_limit_exceeded");await Reject(ctx,429,"rate_limit_exceeded",decision.RetryAfterSeconds);return;}
            if(decision.Kind==RateLimitDecisionKind.StoreUnavailable) {
                if(config.RedisFailureMode=="Reject") {execution.Record(rate,"StoreRejected","rate_limit_store_unavailable");await Reject(ctx,503,"rate_limit_store_unavailable");return;}
                execution.Record(rate,"Bypass","rate_limit_store_unavailable");
            }else execution.Record(rate,"Allowed");
        }
        var circuit=bound.SingleOrDefault(p=>p.Type=="circuit_breaker");
        if(circuit is null) {await next(ctx);return;}
        var state=generation.CircuitLeases[route.Id].State;var admission=state.TryEnter();
        if(!admission.Allowed) {execution.Record(circuit,"OpenRejected","circuit_open");await Reject(ctx,503,"circuit_open",admission.RetryAfterSeconds);return;}
        execution.CircuitState=state;execution.CircuitAdmission=admission;
        execution.Record(circuit,admission.Probe?"HalfOpenProbe":"Allowed");
        try {await next(ctx);}
        finally {var feature=ctx.GetReverseProxyFeature();var forwarded=feature.ProxiedDestination is not null;state.Complete(admission,CircuitOutcomeClassifier.Classify(ctx,generation.CircuitConfigurations[circuit.Id],forwarded));}
    }
    private static async Task Reject(HttpContext ctx,int status,string code,int retry=0)
    {ctx.Response.StatusCode=status;if(retry>0) ctx.Response.Headers.RetryAfter=retry.ToString(CultureInfo.InvariantCulture);await ctx.Response.WriteAsJsonAsync(new {code,traceId=ctx.TraceIdentifier});}
}

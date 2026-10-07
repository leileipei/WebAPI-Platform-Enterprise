using System.Globalization;
namespace WebApi.Gateway.Policies;
public sealed class TrafficPolicyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx,IRateLimitStore limiter)
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
        await next(ctx);
    }
    private static async Task Reject(HttpContext ctx,int status,string code,int retry=0)
    {ctx.Response.StatusCode=status;if(retry>0) ctx.Response.Headers.RetryAfter=retry.ToString(CultureInfo.InvariantCulture);await ctx.Response.WriteAsJsonAsync(new {code,traceId=ctx.TraceIdentifier});}
}

using System.Globalization;
using WebApi.Contracts.Policies;

namespace WebApi.Gateway.Policies;

public sealed class CorsPolicyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var execution=TrafficExecutionContext.From(context)??throw new InvalidOperationException("Routing policy context unavailable.");
        foreach(var binding in execution.Route.PolicyBindings??[])
        {
            if(!execution.Generation.Cors.TryGetValue(binding.PolicyId,out var configuration))continue;
            var result=CorsPolicyEvaluator.Evaluate(context.Request,execution.Route,configuration);
            execution.Cors=result;
            execution.Record(execution.Generation.PoliciesById[binding.PolicyId],!result.Allowed?"Rejected":result.Preflight?"Preflight":result.Origin is null?"Bypass":"Allowed",result.Reason);
            context.Response.OnStarting(()=>{WriteHeaders(context,configuration,result);return Task.CompletedTask;});
            if(!result.Allowed)
            {
                context.Response.StatusCode=403;
                await context.Response.WriteAsJsonAsync(new{code="cors_rejected",reason=result.Reason,traceId=context.TraceIdentifier});
                return;
            }
            if(result.Preflight)
            {
                context.Response.StatusCode=204;
                // Run response callbacks within the original routing generation lease.
                await context.Response.StartAsync(context.RequestAborted);
                return;
            }
            break;
        }
        await next(context);
    }

    private static void WriteHeaders(HttpContext context,CorsConfiguration configuration,CorsEvaluation evaluation)
    {
        var headers=context.Response.Headers;
        foreach(var name in headers.Keys.Where(k=>k.StartsWith("Access-Control-",StringComparison.OrdinalIgnoreCase)).ToArray())headers.Remove(name);
        MergeVary(headers,"Origin");
        if(evaluation.Preflight){MergeVary(headers,"Access-Control-Request-Method");MergeVary(headers,"Access-Control-Request-Headers");}
        if(!evaluation.Allowed||evaluation.Origin is null)return;
        headers["Access-Control-Allow-Origin"]=configuration.AllowedOrigins.Contains("*")?"*":context.Request.Headers.Origin.ToString();
        if(configuration.AllowCredentials)headers["Access-Control-Allow-Credentials"]="true";
        if(evaluation.Preflight)
        {
            headers["Access-Control-Allow-Methods"]=string.Join(", ",configuration.AllowedMethods);
            if(configuration.AllowedHeaders.Count>0)headers["Access-Control-Allow-Headers"]=string.Join(", ",configuration.AllowedHeaders);
            if(configuration.MaxAgeSeconds>0)headers["Access-Control-Max-Age"]=configuration.MaxAgeSeconds.ToString(CultureInfo.InvariantCulture);
        }
        else if(configuration.ExposedHeaders.Count>0)headers["Access-Control-Expose-Headers"]=string.Join(", ",configuration.ExposedHeaders);
    }
    private static void MergeVary(IHeaderDictionary headers,string name)
    {
        var names=headers.Vary.ToString().Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList();
        if(names.Contains("*",StringComparer.Ordinal)||names.Contains(name,StringComparer.OrdinalIgnoreCase))return;
        names.Add(name);headers.Vary=string.Join(", ",names);
    }
}

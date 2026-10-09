using System.Globalization;
using WebApi.Gateway.Policies;
namespace WebApi.Gateway.Security;
public sealed class GatewayPlatformHeadersMiddleware(RequestDelegate next)
{
    private static void Remove(IHeaderDictionary headers)
    {foreach(var key in headers.Keys.Where(k=>k.StartsWith("X-WebApi-",StringComparison.OrdinalIgnoreCase)).ToArray())headers.Remove(key);}
    public async Task InvokeAsync(HttpContext context)
    {
        Remove(context.Request.Headers);
        // Registered first in proxy pipeline, runs last (OnStarting callbacks are LIFO).
        context.Response.OnStarting(()=>{
            Remove(context.Response.Headers);context.Response.Headers["X-WebApi-Trace-Id"]=context.TraceIdentifier;
            if(TrafficExecutionContext.From(context) is { } execution)context.Response.Headers["X-WebApi-Deployment-Sequence"]=execution.Generation.Envelope.DeploymentSequence.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });await next(context);
    }
}

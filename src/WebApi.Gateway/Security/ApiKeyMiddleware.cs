using System.Globalization;
using WebApi.Gateway.Configuration;
using WebApi.Infrastructure.Security;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Security;
public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    public static readonly object GenerationItem=new();
    public async Task InvokeAsync(HttpContext ctx,RuntimeGenerationStore store)
    {
        var feature=ctx.GetReverseProxyFeature();var metadata=feature.Route.Config.Metadata!;using var lease=store.Acquire(long.Parse(metadata["deploymentSequence"],CultureInfo.InvariantCulture));var generation=lease.Generation;ctx.Items[GenerationItem]=generation;
        // Release after capturing the route's generation, before any response or backend I/O.
        if(ctx.Items.TryGetValue(AdmissionGate.Item,out var ticket)) ((AdmissionGate.Ticket)ticket!).Dispose();
        try
        {
            var route=generation.Snapshot.Routes.Single(r=>r.Id==Guid.Parse(metadata["runtimeRouteId"]));var raw=ctx.Request.Headers["X-API-Key"].ToString();ctx.Request.Headers.Remove("X-API-Key");ctx.Request.Headers["X-WebApi-Deployment-Sequence"]=generation.Envelope.DeploymentSequence.ToString(CultureInfo.InvariantCulture);ctx.Request.Headers["X-WebApi-Trace-Id"]=ctx.TraceIdentifier;
            if(route.RequireApiKey)
            {
                var parts=raw.Split('.');var app=parts.Length==2?generation.Snapshot.Applications.SingleOrDefault(a=>a.Credentials.Any(k=>k.AccessKey==parts[0])):null;var key=app?.Credentials.Single(k=>k.AccessKey==parts[0]);var now=DateTimeOffset.UtcNow;
                if(app?.Status!="Active"||key?.Status!="Active"||now<key.ValidFrom||now>=key.ExpiresAt||!ApiKeySecret.Verify(key.Hash,parts[1])) {ctx.Response.StatusCode=401;await ctx.Response.WriteAsJsonAsync(new {code="invalid_api_key",traceId=ctx.TraceIdentifier});return;}
                if(!app.Permissions.Any(p=>p.ApiId==route.ApiId&&now>=p.ValidFrom&&(p.ExpiresAt is null||now<p.ExpiresAt))) {ctx.Response.StatusCode=403;await ctx.Response.WriteAsJsonAsync(new {code="api_not_granted",traceId=ctx.TraceIdentifier});return;}
            }
            // The route and its auth snapshot are now leased together; old backend requests need not hold the admission gate.
            await next(ctx);
        }
        finally {ctx.Items.Remove(GenerationItem);}
    }
}

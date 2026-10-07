using System.Globalization;
namespace WebApi.Gateway.Policies;
public sealed class CacheResponseMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context,IResponseCacheStore store,CacheKeyBuilder keys,GatewayCacheSettings settings)
    {
        var execution=TrafficExecutionContext.From(context)??throw new InvalidOperationException("Authorized traffic context is unavailable.");
        var binding=(execution.Route.PolicyBindings??[]).SingleOrDefault(b=>execution.Generation.Cache.ContainsKey(b.PolicyId));
        if(binding is null){await next(context);return;}
        context.Response.OnStarting(()=>{context.Response.Headers["X-WebApi-Trace-Id"]=context.TraceIdentifier;context.Response.Headers["X-WebApi-Deployment-Sequence"]=execution.Generation.Envelope.DeploymentSequence.ToString(CultureInfo.InvariantCulture);return Task.CompletedTask;});
        var config=execution.Generation.Cache[binding.PolicyId];
        if(execution.VerifiedIdentity is null||!CacheEligibility.Request(context,execution.VerifiedIdentity,config,settings).Eligible||keys.Build(execution,context.Request,config) is not { } key)
        {execution.CacheDisposition="Bypass";await next(context);return;}
        var read=await store.GetAsync(key,context.RequestAborted);
        if(read.Kind==CacheReadKind.Hit&&read.Entry is { } entry&&DateTimeOffset.UtcNow-entry.ResponseAt<entry.FreshFor)
        {
            execution.CacheDisposition="Hit";context.Response.StatusCode=200;
            foreach(var header in entry.Headers)context.Response.Headers[header.Key]=header.Value;
            var age=Math.Max(0,entry.InitialAge+Math.Max(0,(DateTimeOffset.UtcNow-entry.ResponseAt).TotalSeconds));context.Response.Headers.Age=((long)Math.Floor(age)).ToString(CultureInfo.InvariantCulture);
            context.Response.ContentLength=entry.Body.Length;await context.Response.Body.WriteAsync(entry.Body,context.RequestAborted);return;
        }
        if(read.Kind==CacheReadKind.Unavailable){execution.CacheDisposition="Bypass";await next(context);return;}
        execution.CacheDisposition="Miss";var requestAt=DateTimeOffset.UtcNow;
        using var capture=BoundedResponseCapture.Install(context,Math.Min(config.MaxEntryBytes,settings.MaxEntryBytes));
        await next(context);
        var rule=CacheEligibility.Response(context.Response.Headers,context.Response.StatusCode,requestAt,capture.ResponseAt,config);
        if(capture.TryComplete(rule) is not { } stored)return;
        try{var result=await store.PutAsync(key,stored,context.RequestAborted);if(result.Kind==CacheWriteKind.Stored)execution.CacheDisposition="Stored";}
        catch(OperationCanceledException)when(context.RequestAborted.IsCancellationRequested){/* Completed proxy response is never retried after client departure. */}
    }
}

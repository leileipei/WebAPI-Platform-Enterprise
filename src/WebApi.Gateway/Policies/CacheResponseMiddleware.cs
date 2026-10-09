using System.Globalization;
namespace WebApi.Gateway.Policies;
public sealed class CacheResponseMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context,IResponseCacheStore store,CacheKeyBuilder keys,GatewayCacheSettings settings)
    {
        var execution=TrafficExecutionContext.From(context)??throw new InvalidOperationException("Authorized traffic context is unavailable.");
        var binding=(execution.Route.PolicyBindings??[]).SingleOrDefault(b=>execution.Generation.Cache.ContainsKey(b.PolicyId));
        if(binding is null){await next(context);return;}
        var policy=execution.Generation.PoliciesById[binding.PolicyId];string? readPhase=null,writePhase=null,reason=null;
        try
        {
            var config=execution.Generation.Cache[binding.PolicyId];
            if(execution.VerifiedIdentity is null||!CacheEligibility.Request(context,execution.VerifiedIdentity,config,settings).Eligible)
            {execution.CacheDisposition=readPhase="Bypass";reason="unsafe_request";await next(context);return;}
            if(keys.Build(execution,context.Request,config) is not { } key){execution.CacheDisposition=readPhase="Bypass";reason="cache_key_budget";await next(context);return;}
            var read=await store.GetAsync(key,context.RequestAborted);
            if(read.Kind==CacheReadKind.Hit&&read.Entry is { } entry&&DateTimeOffset.UtcNow-entry.ResponseAt<entry.FreshFor)
            {
                execution.CacheDisposition=readPhase="Hit";context.Response.StatusCode=200;
                foreach(var skipped in (execution.Route.PolicyBindings??[]).Select(b=>execution.Generation.PoliciesById[b.PolicyId]))
                    if(skipped.Type=="circuit_breaker")execution.Record(skipped,"CacheSkipped","cache_hit");else if(skipped.Type=="retry")execution.Record(skipped,"Bypass","cache_hit",attemptCount:0);
                foreach(var header in entry.Headers)context.Response.Headers[header.Key]=header.Value;
                var age=Math.Max(0,entry.InitialAge+Math.Max(0,(DateTimeOffset.UtcNow-entry.ResponseAt).TotalSeconds));context.Response.Headers.Age=((long)Math.Floor(age)).ToString(CultureInfo.InvariantCulture);
                context.Response.ContentLength=entry.Body.Length;await context.Response.Body.WriteAsync(entry.Body,context.RequestAborted);return;
            }
            if(read.Kind==CacheReadKind.Unavailable){execution.CacheDisposition=readPhase="Bypass";reason="cache_store_unavailable";await next(context);return;}
            execution.CacheDisposition=readPhase="Miss";var requestAt=DateTimeOffset.UtcNow;
            using var capture=BoundedResponseCapture.Install(context,Math.Min(config.MaxEntryBytes,settings.MaxEntryBytes));
            await next(context);
            var rule=CacheEligibility.Response(context.Response.Headers,context.Response.StatusCode,requestAt,capture.ResponseAt,config);writePhase="Bypass";
            if(capture.TryComplete(rule) is not { } stored)return;
            try{var result=await store.PutAsync(key,stored,context.RequestAborted);if(result.Kind==CacheWriteKind.Stored){execution.CacheDisposition="Stored";writePhase="Stored";}}
            catch(OperationCanceledException)when(context.RequestAborted.IsCancellationRequested){/* Never retry a cache write after departure. */}
        }
        finally{execution.Record(policy,execution.CacheDisposition??"Bypass",reason??(execution.CacheDisposition is null?"cache_store_unavailable":null),readPhase??"Bypass",writePhase);}
    }
}

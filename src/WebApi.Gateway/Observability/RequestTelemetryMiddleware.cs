using System.Diagnostics;
using Microsoft.AspNetCore.Http.Timeouts;
using Yarp.ReverseProxy.Forwarder;
namespace WebApi.Gateway.Observability;
public sealed class RequestTelemetryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx,GatewayTelemetryRecorder recorder,TelemetrySanitizer sanitizer,GatewaySettings settings,TelemetryDropTracker drops)
    {
        if(IsHealth(ctx.Request.Path)){await next(ctx);return;}
        var started=Stopwatch.GetTimestamp();
        var ip=ctx.Connection.RemoteIpAddress is { } address?sanitizer.Ip(address):("Unknown","");
        ActivityContext.TryParse(ctx.Request.Headers["traceparent"],ctx.Request.Headers["tracestate"],out var parent);
        using var activity=recorder.Activities.StartActivity("gateway.request",ActivityKind.Server,parent);
        var state=new RequestTelemetryState(new(){EnvironmentId=settings.EnvironmentId,NodeName=settings.NodeName,Method=TelemetrySanitizer.Method(ctx.Request.Method),RequestId=ctx.TraceIdentifier,TraceId=activity?.TraceId.ToHexString()??Activity.Current?.TraceId.ToHexString()??ActivityTraceId.CreateRandom().ToHexString(),SpanId=activity?.SpanId.ToHexString()??"",MaskedIp=ip.Item1,IpHmac=ip.Item2}){ServerActivity=activity};
        ctx.Items[RequestTelemetryState.Item]=state;
        var outcome="Completed";
        try{await next(ctx);}
        catch(OperationCanceledException)when(ctx.RequestAborted.IsCancellationRequested){outcome="ClientAborted";throw;}
        catch(Exception){outcome="ProxyError";throw;}
        finally
        {
            try
            {
                var error=ctx.Features.Get<IForwarderErrorFeature>();
                if(ctx.RequestAborted.IsCancellationRequested)outcome="ClientAborted";
                else if(ctx.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested==true)outcome="Timeout";
                else if(error is not null)outcome="ProxyError";
                var final=state.Context with{Time=DateTimeOffset.UtcNow,DurationSeconds=Stopwatch.GetElapsedTime(started).TotalSeconds,Status=outcome=="ClientAborted"&&!ctx.Response.HasStarted?null:ctx.Response.StatusCode,Outcome=outcome};
                state.Context=final;TelemetryAttributes.Apply(activity,final);
                activity?.SetStatus(final.Success?ActivityStatusCode.Ok:ActivityStatusCode.Error);
                recorder.Record(final);
            }
            catch(Exception){drops.Record("logs",1);}
        }
    }
    public static bool IsHealth(PathString path)=>path.Equals("/health/live",StringComparison.OrdinalIgnoreCase)||path.Equals("/health/ready",StringComparison.OrdinalIgnoreCase);
}

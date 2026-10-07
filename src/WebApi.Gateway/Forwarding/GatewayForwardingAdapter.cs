using System.Diagnostics;
using System.Net.Sockets;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Observability;
using WebApi.Contracts.Policies;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Forwarding;

public sealed class GatewayForwardingAdapter(IHttpForwarder forwarder, ForwardAttemptRegistry registry,
    IEnumerable<IPassiveHealthCheckPolicy> healthPolicies)
{
    public async Task<ForwardAttemptResult> SendAsync(HttpContext context, DestinationState destination, ForwardAttemptControl control, CancellationToken ct)
    {
        var execution = TrafficExecutionContext.From(context) ?? throw new InvalidOperationException("Traffic generation is unavailable.");
        var feature = context.GetReverseProxyFeature();
        feature.ProxiedDestination = destination;
        context.Features.Set<IForwarderErrorFeature>(null);
        using var lease = registry.Acquire(execution.Generation, destination);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(control.Timeout);
        var started = Stopwatch.GetTimestamp();
        var remaining = control.Timeout;
        var transformer = new RetryResponseTransformer(feature.Route.Transformer, control, ct,
            () => { remaining = control.Timeout - Stopwatch.GetElapsedTime(started); timeout.CancelAfter(Timeout.InfiniteTimeSpan); },
            () => { if (remaining <= TimeSpan.Zero) timeout.Cancel(); else timeout.CancelAfter(remaining); });
        var result = ForwarderError.None;
        var passive = new PassiveHealthCheckMiddleware(async ctx =>
        {
            result = await forwarder.SendAsync(ctx, destination.Model.Config.Address, feature.Cluster.HttpClient,
                feature.Cluster.Config.HttpRequest ?? ForwarderRequestConfig.Empty, transformer, timeout.Token);
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 504;
                result = ForwarderError.RequestTimedOut;
                ctx.Features.Set<IForwarderErrorFeature>(new AttemptTimeoutError(ctx.Features.Get<IForwarderErrorFeature>()?.Exception ?? new TimeoutException("Forward attempt deadline elapsed.")));
            }
        }, healthPolicies);
        var telemetry=RequestTelemetryState.From(context);var recorder=telemetry is null?null:context.RequestServices.GetRequiredService<GatewayTelemetryRecorder>();
        using var activity=recorder?.Activities.StartActivity("gateway.proxy",ActivityKind.Client);
        if(activity?.Id is { } traceparent)context.Request.Headers["traceparent"]=traceparent;
        var threw=false;
        try{await passive.Invoke(context);}catch{threw=true;throw;}
        finally
        {
            var outcome=context.RequestAborted.IsCancellationRequested?"ClientAborted":result==ForwarderError.RequestTimedOut||timeout.IsCancellationRequested&&!ct.IsCancellationRequested?"Timeout":threw||result!=ForwarderError.None?"ProxyError":"Completed";
            var status=result==ForwarderError.None&&!threw?transformer.StatusCode??context.Response.StatusCode:context.Response.StatusCode;
            var destinationId=Guid.TryParse(destination.DestinationId,out var id)?id:(Guid?)null;
            var observation=new ForwardAttemptObservation(control.Attempt,destinationId,status,outcome,Stopwatch.GetElapsedTime(started).TotalSeconds);execution.AddAttempt(observation);
            if(telemetry is not null)
            {
                telemetry.Context=telemetry.Context with{DestinationId=destinationId};
                var client=telemetry.Context with{Status=status,DurationSeconds=observation.DurationSeconds,Outcome=outcome,AttemptNumber=control.Attempt,AttemptCount=execution.Attempts.Count,ForwardAttempts=execution.Attempts};
                TelemetryAttributes.Apply(activity,client);activity?.SetStatus(client.Success?ActivityStatusCode.Ok:ActivityStatusCode.Error);
                var retry=(execution.Route.PolicyBindings??[]).Select(b=>execution.Generation.PoliciesById[b.PolicyId]).SingleOrDefault(p=>p.Type=="retry");recorder!.RecordAttempt(retry?.SourcePolicyId,outcome);
            }
        }
        var error = context.Features.Get<IForwarderErrorFeature>();
        var notSent = result == ForwarderError.Request && !timeout.IsCancellationRequested && !context.Response.HasStarted && ConnectionRefused(error?.Exception);
        return new(result, transformer.Suppressed, transformer.StatusCode, notSent);
    }
    private static bool ConnectionRefused(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return true;
        return false;
    }
    private sealed record AttemptTimeoutError(Exception Exception) : IForwarderErrorFeature
    {
        public ForwarderError Error => ForwarderError.RequestTimedOut;
    }

}

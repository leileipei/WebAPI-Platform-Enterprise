using System.Diagnostics;
using System.Net.Sockets;
using WebApi.Gateway.Policies;
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
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested && !ctx.Response.HasStarted)
            { ctx.Response.StatusCode = 504; result = ForwarderError.RequestTimedOut; }
        }, healthPolicies);
        await passive.Invoke(context);
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
}

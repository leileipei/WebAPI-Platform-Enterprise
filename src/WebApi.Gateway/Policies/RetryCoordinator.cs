using System.Diagnostics;
using Microsoft.AspNetCore.Http.Timeouts;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Forwarding;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;
namespace WebApi.Gateway.Policies;
public sealed class RetryCoordinator(GatewayForwardingAdapter adapter, WeightedDestinationSelector selector, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task ExecuteAsync(HttpContext context, TrafficExecutionContext execution, RetryConfiguration config, CancellationToken ct)
    {
        var feature = context.GetReverseProxyFeature();
        var routeTimeout = context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken ?? CancellationToken.None;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, routeTimeout);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(execution.Route.TimeoutMs));
        var started = Stopwatch.GetTimestamp();
        TimeSpan Remaining() => TimeSpan.FromMilliseconds(execution.Route.TimeoutMs) - Stopwatch.GetElapsedTime(started);
        bool CanContinue() => execution.CircuitState is null || execution.CircuitAdmission is { } admission && execution.CircuitState.CanContinue(admission);
        var generation = execution.Generation;
        var cluster = generation.Snapshot.Clusters.Single(c => c.Id == execution.Route.ClusterId);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 1; attempt <= config.MaxAttempts; attempt++)
        {
            if (deadline.IsCancellationRequested) { if (!context.Response.HasStarted && !ct.IsCancellationRequested) context.Response.StatusCode = 504; return; }
            if (attempt > 1 && !CanContinue()) { context.Response.StatusCode = 503; return; }
            var available = feature.Route.Cluster!.DestinationsState.AvailableDestinations;
            var destination = selector.Select(generation, cluster, available, attempted);
            if (destination is null) { context.Response.StatusCode = 503; return; }
            attempted.Add(destination.DestinationId);
            if (attempt > 1) { context.Response.Clear(); context.Features.Set<IForwarderErrorFeature>(null); }
            var number = attempt;
            var allowedTimeout = TimeSpan.FromMilliseconds(config.PerAttemptTimeoutMs);
            if (Remaining() < allowedTimeout) allowedTimeout = Remaining();
            if (allowedTimeout <= TimeSpan.Zero) { context.Response.StatusCode = 504; return; }
            async ValueTask<bool> Suppress(HttpResponseMessage response, CancellationToken cancellation)
            {
                if (number >= config.MaxAttempts || !config.RetryStatusCodes.Contains((int)response.StatusCode) || !CanContinue()) return false;
                var delay = Delay(config, number, response);
                if (delay is null || delay >= Remaining()) return false;
                try { if (delay > TimeSpan.Zero) await Task.Delay(delay.Value, clock, cancellation); }
                catch (OperationCanceledException) { return false; }
                return !deadline.IsCancellationRequested && !context.Response.HasStarted && CanContinue();
            }
            var result = await adapter.SendAsync(context, destination, new(number, allowedTimeout, _ => false, Suppress), deadline.Token);
            if (result.Suppressed) continue;
            if (result.Error == ForwarderError.None || !config.RetryConnectionFailures || !result.DefinitelyNotSent
                || number >= config.MaxAttempts || context.Response.HasStarted || !CanContinue() || deadline.IsCancellationRequested) return;
            var backoff = Delay(config, number, null);
            if (backoff is null || backoff >= Remaining()) return;
            try { if (backoff > TimeSpan.Zero) await Task.Delay(backoff.Value, clock, deadline.Token); }
            catch (OperationCanceledException) { if (!context.Response.HasStarted && !ct.IsCancellationRequested) context.Response.StatusCode = 504; return; }
        }
    }
    private TimeSpan? Delay(RetryConfiguration config, int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.Contains("Retry-After") == true)
        {
            var values = response.Headers.GetValues("Retry-After").ToArray();
            if (values.Length != 1 || !System.Net.Http.Headers.RetryConditionHeaderValue.TryParse(values[0], out var retryAfter)) return null;
            var delay = retryAfter.Delta ?? retryAfter.Date!.Value - clock.GetUtcNow();
            return delay < TimeSpan.Zero ? null : delay;
        }
        var milliseconds = Math.Min(config.MaxDelayMs, config.BaseDelayMs * (1L << (attempt - 1)));
        var span = milliseconds * config.JitterPercent / 100d;
        return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds + (Random.Shared.NextDouble() * 2 - 1) * span, 0, config.MaxDelayMs));
    }
}

using System.Diagnostics;
using WebApi.Contracts.Gateway;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Security;
using WebApi.Infrastructure.Routing;

namespace WebApi.Gateway.Workers;

public sealed class NodeMaintenanceWorker(GatewaySettings settings, NodeClient nodes, ConfigWatcher watcher, RuntimeGenerationStore generations,
    UpstreamAddressPolicy addresses, ILogger<NodeMaintenanceWorker> log, IGovernanceAdmissionStateProvider? governance = null) : BackgroundService
{
    private readonly HttpClient probeClient = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(3) };
    private (Guid Id, NodeOperationResult Result)? previous;

    public async Task PollOnceAsync(CancellationToken ct)
    {
        var op = await nodes.ClaimOperationAsync(ct);
        if (op == null || op.NodeId != nodes.NodeId || op.InstanceId != settings.InstanceId || op.ExpiresAt <= DateTimeOffset.UtcNow) return;
        if (previous?.Id != op.Id)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var remaining=op.ExpiresAt-DateTimeOffset.UtcNow;
            timeout.CancelAfter(remaining>TimeSpan.Zero?remaining:TimeSpan.Zero);
            NodeOperationResult result;
            try
            {
                result = op.Action switch
                {
                    "Retry" => new(settings.InstanceId, await watcher.RetryAsync(timeout.Token)),
                    "Probe" => await ProbeAsync(op, timeout.Token),
                    _ => throw new InvalidDataException("Unknown node operation.")
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result = new(settings.InstanceId, op.Action == "Probe" ? "timeout" : "unavailable"); }
            catch (HttpRequestException) { result = new(settings.InstanceId, "unavailable"); }
            previous = (op.Id, result);
        }
        await nodes.CompleteOperationAsync(op.Id, previous.Value.Result, ct);
    }

    private async Task<NodeOperationResult> ProbeAsync(NodeOperation op, CancellationToken ct)
    {
        var generation = generations.Current;
        if (generation == null || !GovernanceReadiness.CanUse(generation, governance)) return new(settings.InstanceId, "unavailable");
        foreach (var cluster in generation.Snapshot.Clusters.Where(c => (c.SourceId ?? c.Id) == op.ClusterId))
        {
            var destination = cluster.Destinations.FirstOrDefault(d => d.Id == op.DestinationId);
            if (destination == null || GatewayProbeTarget.Fingerprint(destination.Address, cluster.HealthCheckPath) != op.TargetFingerprint) continue;
            // Build only a path under the admitted destination. Neither the caller nor the path controls the authority.
            var origin = new Uri(addresses.Validate(destination.Address));
            var target = new UriBuilder(origin) { Path = origin.AbsolutePath.TrimEnd('/') + "/" + cluster.HealthCheckPath.TrimStart('/') }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            var start = Stopwatch.GetTimestamp();
            using var response = await probeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new(settings.InstanceId, response.IsSuccessStatusCode ? "healthy" : "unhealthy",
                (int)response.StatusCode, (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        return new(settings.InstanceId, "target_changed");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!settings.AutomaticUpdates) return;
        while (!ct.IsCancellationRequested)
        {
            try { await nodes.WaitForOperationAsync(ct); await PollOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) { log.LogWarning("Node maintenance poll failed: {ErrorType}", error.GetType().Name); }

        }
    }

    public override void Dispose() { probeClient.Dispose(); base.Dispose(); }
}

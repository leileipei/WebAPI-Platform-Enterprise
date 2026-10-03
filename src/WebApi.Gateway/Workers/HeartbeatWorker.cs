using WebApi.Gateway.Configuration;
namespace WebApi.Gateway.Workers;
public sealed class HeartbeatWorker(GatewaySettings settings,NodeClient nodes,RuntimeGenerationStore store,ILogger<HeartbeatWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {if(!settings.AutomaticUpdates) return;while(!ct.IsCancellationRequested) {try {await nodes.HeartbeatAsync(store,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}catch(Exception e) {log.LogWarning("Heartbeat retry: {ErrorType}",e.GetType().Name);}await Task.Delay(5000,ct);}}
}

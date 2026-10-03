using WebApi.Contracts.Runtime;
using WebApi.Gateway.Configuration;
using WebApi.Infrastructure.Runtime;
namespace WebApi.Gateway.Workers;
public sealed class ConfigWatcher(GatewaySettings settings,SnapshotActivation activation,NodeClient nodes,RedisSnapshotStore redis,ILogger<ConfigWatcher> log) : BackgroundService
{
    private readonly SemaphoreSlim changed=new(0,1);
    public async Task<ActivationResult> AcceptAsync(DesiredConfigResponse config,CancellationToken ct=default)
    {var result=await activation.ApplyAsync(config.Envelope,config.Payload,ct);if(result.Applied) await nodes.AckAsync(config.Envelope,true,null,ct);else if(result.ErrorCode is not ("stale_sequence" or "sequence_conflict")) await nodes.AckAsync(config.Envelope,false,result.ErrorCode,ct);return result;}
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!settings.AutomaticUpdates) return;var subscribed=false;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await nodes.RegisterAsync(ct);if(settings.RedisNotifications&&!subscribed) {try {await redis.SubscribeAsync(settings.EnvironmentId,()=>{if(changed.CurrentCount==0) changed.Release();},ct);subscribed=true;}catch(Exception e) when(e is not OperationCanceledException) {log.LogWarning("Redis subscription unavailable: {ErrorType}",e.GetType().Name);}}
                DesiredConfigResponse? desired;try {desired=await nodes.DesiredAsync(ct);}catch(HttpRequestException) {desired=await redis.GetDesiredAsync(settings.EnvironmentId,ct);}catch(TaskCanceledException) when(!ct.IsCancellationRequested) {desired=await redis.GetDesiredAsync(settings.EnvironmentId,ct);}
                if(desired is not null) await AcceptAsync(desired,ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}
            catch(Exception error) {log.LogWarning("Configuration poll retry: {ErrorType}",error.GetType().Name);}
            await changed.WaitAsync(TimeSpan.FromSeconds(2),ct);
        }
    }
}

using WebApi.Infrastructure.Sso;
namespace WebApi.ControlPlane.Sso;
public sealed class SsoAttemptCleanupWorker(IServiceScopeFactory scopes,TimeProvider clock,ILogger<SsoAttemptCleanupWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(5),clock);
        try
        {
            while(await timer.WaitForNextTickAsync(stoppingToken))
            {
                try {await using var scope=scopes.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<SsoAttemptCleanupService>().CleanupAsync(clock.GetUtcNow(),stoppingToken);}
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
                catch(Exception error){logger.LogWarning("SSO attempt cleanup failed: {Type}",error.GetType().Name);}
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){}
    }
}

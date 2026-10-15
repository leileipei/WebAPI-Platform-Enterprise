using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.CredentialUsage;
namespace WebApi.Worker.Workers;
public sealed class CredentialUsageCleanupWorker(IServiceScopeFactory scopes,TimeProvider clock,ILogger<CredentialUsageCleanupWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{await using var scope=scopes.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<CredentialUsageCleanupService>().CleanupAsync(clock.GetUtcNow(),stoppingToken);}
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception error){logger.LogWarning("Credential usage cleanup failed: {Type}",error.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(60),clock,stoppingToken);}catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
        }
    }
}

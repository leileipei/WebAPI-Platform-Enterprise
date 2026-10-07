using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.Notifications;
namespace WebApi.Worker.Workers;
public sealed class NotificationDeliveryWorker(IServiceScopeFactory scopes,NotificationDeploymentSettings settings,ILogger<NotificationDeliveryWorker> log):BackgroundService
{
    private readonly string owner="notification-worker-"+Guid.NewGuid().ToString("N");
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using(var coordination=scopes.CreateScope())await coordination.ServiceProvider.GetRequiredService<NotificationPlanner>().CoordinateNextResolvedAsync(ct);
                await Task.WhenAll(Enumerable.Range(0,settings.Concurrency).Select(async slot=>{using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchNextAsync(owner+"-"+slot,ct);}));
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception error){log.LogWarning("Notification worker retry: {ErrorType}",error.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds),ct);
        }
    }
}

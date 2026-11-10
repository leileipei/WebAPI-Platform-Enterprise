using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.Messaging;
namespace WebApi.Worker.Workers;
public sealed class OutboxWorker(IServiceScopeFactory scopes,ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var reconcileAt=DateTimeOffset.MinValue;
        while(!ct.IsCancellationRequested)
        {
            if(DateTimeOffset.UtcNow>=reconcileAt)
            {
                try
                {
                    using var scope=scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().ReconcileDesiredAsync(ct);
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}
                catch(Exception error) {log.LogError("Desired reconciliation retry: {ErrorType}",error.GetType().Name);}
                // A failed pass also observes the retry interval.
                reconcileAt=DateTimeOffset.UtcNow.AddSeconds(30);
            }
            try
            {
                // A reconciliation failure must not poison the dispatch scope or skip dispatch.
                using var scope=scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchNextAsync(ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}
            catch(Exception error) {log.LogError("Outbox retry: {ErrorType}",error.GetType().Name);}
            try {await Task.Delay(1000,ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}
        }
    }
}

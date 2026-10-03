using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.Messaging;
namespace WebApi.Worker.Workers;
public sealed class OutboxWorker(IServiceScopeFactory scopes,ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {var reconcileAt=DateTimeOffset.MinValue;while(!ct.IsCancellationRequested) {try {using var scope=scopes.CreateScope();var dispatcher=scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();if(DateTimeOffset.UtcNow>=reconcileAt) {await dispatcher.ReconcileDesiredAsync(ct);reconcileAt=DateTimeOffset.UtcNow.AddSeconds(30);}await dispatcher.DispatchNextAsync(ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}catch(Exception error) {log.LogError("Outbox retry: {ErrorType}",error.GetType().Name);}await Task.Delay(1000,ct);}}
}

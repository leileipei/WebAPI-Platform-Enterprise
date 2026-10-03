using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.Releases;
namespace WebApi.Worker.Workers;
public sealed class ReleaseBuildWorker(IServiceScopeFactory scopes,ILogger<ReleaseBuildWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {while(!ct.IsCancellationRequested) {try {using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<PublishCoordinator>().BuildNextAsync(ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}catch(Exception error) {log.LogError("Release build retry: {ErrorType}",error.GetType().Name);}await Task.Delay(1000,ct);}}
}

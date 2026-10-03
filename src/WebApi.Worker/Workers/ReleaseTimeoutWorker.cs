using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.Gateway;
namespace WebApi.Worker.Workers;
public sealed class ReleaseTimeoutWorker(IServiceScopeFactory scopes,ILogger<ReleaseTimeoutWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {while(!ct.IsCancellationRequested) {try {using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<ReleaseTimeoutService>().ExpireAsync(ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}catch(Exception error) {log.LogError("Release timeout retry: {ErrorType}",error.GetType().Name);}await Task.Delay(1000,ct);}}
}

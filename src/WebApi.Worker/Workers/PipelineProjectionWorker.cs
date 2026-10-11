using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.Delivery.Pipelines;
namespace WebApi.Worker.Workers;
public sealed class PipelineProjectionWorker(IServiceScopeFactory scopes,TimeProvider clock,ILogger<PipelineProjectionWorker> log):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken ct)
 {while(!ct.IsCancellationRequested){try{using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<PipelineProjectionService>().ScanAsync(50,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}catch(Exception e){log.LogError("Pipeline projection retry: {ErrorType}",e.GetType().Name);}await Task.Delay(TimeSpan.FromSeconds(1),clock,ct);}}
}

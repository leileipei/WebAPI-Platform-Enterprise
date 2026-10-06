using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.Catalog;
namespace WebApi.Worker.Workers;
public sealed class ImportPreviewCleanupWorker(IServiceScopeFactory scopes,TimeProvider clock,ILogger<ImportPreviewCleanupWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1),clock);
        try{while(await timer.WaitForNextTickAsync(ct)){
            try{await using var scope=scopes.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<ImportPreviewCleanupService>().RunAsync(100,ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception error){logger.LogWarning("Import preview cleanup failed: {Type}",error.GetType().Name);}
        }}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
    }
}

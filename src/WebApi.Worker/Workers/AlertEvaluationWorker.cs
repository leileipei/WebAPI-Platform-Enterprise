using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.Alerts;
namespace WebApi.Worker.Workers;
public sealed class AlertEvaluationWorker(IServiceScopeFactory scopes,ILogger<AlertEvaluationWorker> log):BackgroundService
{
    private readonly string owner="alert-worker-"+Guid.NewGuid().ToString("N");
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<EvaluationLease> leases;
                using(var scope=scopes.CreateScope()){var store=scope.ServiceProvider.GetRequiredService<AlertEvaluationLeaseStore>();leases=await store.ClaimAsync(await store.CurrentSlotAsync(ct),owner,ct);}
                // Batch size is bounded by configured concurrency; each lease gets a distinct DbContext.
                await Task.WhenAll(leases.Select(async lease=>{try{using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<AlertEvaluationService>().EvaluateAsync(lease,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){}catch(Exception error){log.LogWarning("Alert evaluation retry: {ErrorType}",error.GetType().Name);}}));
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception error){log.LogWarning("Alert lease retry: {ErrorType}",error.GetType().Name);}
            await Task.Delay(1000,ct);
        }
    }
}

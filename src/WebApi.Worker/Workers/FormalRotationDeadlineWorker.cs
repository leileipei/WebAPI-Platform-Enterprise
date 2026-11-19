using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Infrastructure.CredentialSecurity;

namespace WebApi.Worker.Workers;

internal sealed class FormalRotationDeadlineWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<FormalRotationDeadlineWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var rotations = scope.ServiceProvider.GetRequiredService<PostgresFormalRotationService>();
                    await rotations.RevokeDueAsync(stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Formal rotation deadline processing unavailable ({ErrorType})", error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

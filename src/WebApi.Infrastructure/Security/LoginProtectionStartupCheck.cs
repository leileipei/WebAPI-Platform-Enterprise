using Microsoft.Extensions.Hosting;
namespace WebApi.Infrastructure.Security;
public sealed class LoginProtectionStartupCheck(LoginProtectionDeploymentSettings settings) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken){_ = settings.HmacSecret;return Task.CompletedTask;}
    public Task StopAsync(CancellationToken cancellationToken)=>Task.CompletedTask;
}

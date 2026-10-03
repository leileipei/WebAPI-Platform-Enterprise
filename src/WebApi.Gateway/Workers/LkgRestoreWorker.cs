using WebApi.Gateway.Configuration;
namespace WebApi.Gateway.Workers;
public sealed class LkgRestoreWorker(SnapshotActivation activation) : BackgroundService
{protected override Task ExecuteAsync(CancellationToken ct)=>activation.RestoreAsync(ct);}

using System.Threading.Channels;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;

namespace WebApi.Gateway.Security;

internal sealed class FormalRotationProofDelivery(IServiceProvider services, ILogger<FormalRotationProofDelivery> log)
    : BackgroundService, IFormalRotationRequestProofSink
{
    private readonly Channel<FormalRotationRequestProof> pending = Channel.CreateBounded<FormalRotationRequestProof>(
        new BoundedChannelOptions(256) { SingleReader = true });

    public bool TryRecord(FormalRotationRequestProof proof) => pending.Writer.TryWrite(proof);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var proof in pending.Reader.ReadAllAsync(ct))
            {
                try { await services.GetRequiredService<FormalRotationProofHttpWitness>().StoreAsync(proof, ct); }
                catch (Exception error) when (!ct.IsCancellationRequested && error is (InvalidDataException or IOException or
                    ApiException or ArgumentException or InvalidOperationException))
                {
                    log.LogDebug("Independent rotation proof unavailable ({ErrorType})", error.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { pending.Writer.TryComplete(); }
    }
}

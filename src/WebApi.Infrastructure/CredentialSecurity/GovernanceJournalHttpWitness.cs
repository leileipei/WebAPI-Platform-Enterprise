using System.Security.Cryptography.X509Certificates;
using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

// The service must authenticate and scope the writer's certificate. Pinned
// response signatures authenticate evidence separately from the TLS channel.
public sealed class GovernanceJournalHttpWitness : IGovernanceJournalWitness, IDisposable
{
    private readonly Uri endpoint;
    private readonly GovernanceWitnessTrust trust;
    private readonly BoundedWitnessHttpClient transport;

    public GovernanceJournalHttpWitness(Uri endpoint, GovernanceWitnessTrust trust, X509Certificate2 writerCertificate)
        : this(BoundedWitnessHttpClient.RequireEndpoint(endpoint), trust ?? throw new ArgumentNullException(nameof(trust)),
            CreateTransport(writerCertificate)) { }

    internal GovernanceJournalHttpWitness(Uri endpoint, GovernanceWitnessTrust trust, BoundedWitnessHttpClient transport)
    {
        this.endpoint = endpoint;
        this.trust = trust;
        this.transport = transport;
    }

    private static BoundedWitnessHttpClient CreateTransport(X509Certificate2 writerCertificate)
    {
        ArgumentNullException.ThrowIfNull(writerCertificate);
        if (!writerCertificate.HasPrivateKey) throw new ArgumentException("The witness writer certificate requires a private key.", nameof(writerCertificate));
        return new(writerCertificate);
    }

    public Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId journal, Guid? operationId, CancellationToken ct) =>
        SendAsync(GovernanceJournalProtocol.Observe(journal, operationId), ct);

    public Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct) =>
        SendAsync(GovernanceJournalProtocol.Prepare(journal, intent), ct);

    public Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct) =>
        SendAsync(GovernanceJournalProtocol.Confirm(journal, intent), ct);

    private async Task<GovernanceWitnessState> SendAsync(GovernanceWitnessRequest request, CancellationToken ct)
    {
        var response = await transport.PostAsync(endpoint, GovernanceJournalProtocol.EncodeRequest(request), GovernanceJournalProtocol.MaxWireBytes, ct);
        return GovernanceJournalProtocol.VerifyResponse(request, response, trust);
    }

    public void Dispose() => transport.Dispose();
}

using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

public sealed class IndependentEvidenceHttpClient : IIndependentTimeEvidenceSource, IIndependentSecurityHistoryWitness, IDisposable
{
    private readonly Uri timeEndpoint;
    private readonly Uri historyEndpoint;
    private readonly BoundedWitnessHttpClient transport;

    public IndependentEvidenceHttpClient(Uri timeEndpoint, Uri historyEndpoint)
        : this(BoundedWitnessHttpClient.RequireEndpoint(timeEndpoint), BoundedWitnessHttpClient.RequireEndpoint(historyEndpoint), new()) { }

    internal IndependentEvidenceHttpClient(Uri timeEndpoint, Uri historyEndpoint, BoundedWitnessHttpClient transport)
    {
        this.timeEndpoint = timeEndpoint;
        this.historyEndpoint = historyEndpoint;
        this.transport = transport;
    }

    Task<Stream?> IIndependentTimeEvidenceSource.RequestAsync(EvidenceChallenge challenge, CancellationToken ct) =>
        RequestAsync(timeEndpoint, challenge, ct);

    Task<Stream?> IIndependentSecurityHistoryWitness.RequestAsync(EvidenceChallenge challenge, CancellationToken ct) =>
        RequestAsync(historyEndpoint, challenge, ct);

    private async Task<Stream?> RequestAsync(Uri endpoint, EvidenceChallenge challenge, CancellationToken ct)
    {
        var bytes = await transport.PostAsync(endpoint, CanonicalJson.Serialize(challenge), IndependentEvidenceSession.MaxWireBytes, ct);
        return new MemoryStream(bytes, writable: false);
    }

    public void Dispose() => transport.Dispose();
}

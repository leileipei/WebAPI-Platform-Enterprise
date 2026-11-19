using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

public interface IGovernanceEnvironmentHistoryWitness
{
    // Return fresh authenticated evidence from independent durable state. A Confirmed response must
    // bind an already enrolled environment to this exact, still-confirmed journal position, with
    // no pending journal or environment change. The challenge carries only the journal identity,
    // confirmed position and environment identifier.
    Task<GovernanceEnvironmentHistory> ObserveAsync(GovernanceJournalId journal, GovernanceJournalPosition position,
        Guid environmentId, CancellationToken ct);
}

public sealed class GovernanceEnvironmentHistoryHttpWitness : IGovernanceEnvironmentHistoryWitness, IDisposable
{
    private readonly Uri endpoint;
    private readonly GovernanceWitnessTrust trust;
    private readonly BoundedWitnessHttpClient transport;

    public GovernanceEnvironmentHistoryHttpWitness(Uri endpoint, GovernanceWitnessTrust trust)
        : this(BoundedWitnessHttpClient.RequireEndpoint(endpoint), trust ?? throw new ArgumentNullException(nameof(trust)), new()) { }

    internal GovernanceEnvironmentHistoryHttpWitness(Uri endpoint, GovernanceWitnessTrust trust, BoundedWitnessHttpClient transport)
    {
        this.endpoint = endpoint;
        this.trust = trust;
        this.transport = transport;
    }

    public async Task<GovernanceEnvironmentHistory> ObserveAsync(GovernanceJournalId journal, GovernanceJournalPosition position,
        Guid environmentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var request = GovernanceEnvironmentHistoryProtocol.Observe(journal, position, environmentId);
        var wire = await transport.PostAsync(endpoint, GovernanceEnvironmentHistoryProtocol.EncodeRequest(request),
            GovernanceEnvironmentHistoryProtocol.MaxWireBytes, ct);
        return GovernanceEnvironmentHistoryProtocol.VerifyResponse(request, wire, trust);
    }

    public void Dispose() => transport.Dispose();
}

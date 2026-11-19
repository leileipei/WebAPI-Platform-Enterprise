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
    private readonly TimeProvider clock;

    public GovernanceJournalHttpWitness(Uri endpoint, GovernanceWitnessTrust trust, X509Certificate2 writerCertificate)
        : this(BoundedWitnessHttpClient.RequireEndpoint(endpoint), trust ?? throw new ArgumentNullException(nameof(trust)),
            CreateTransport(writerCertificate)) { }

    internal GovernanceJournalHttpWitness(Uri endpoint, GovernanceWitnessTrust trust, BoundedWitnessHttpClient transport,
        TimeProvider? clock = null)
    {
        this.endpoint = endpoint;
        this.trust = trust;
        this.transport = transport;
        this.clock = clock ?? TimeProvider.System;
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

    internal Task<GovernanceWitnessState> PrepareFormalRotationAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct) =>
        SendFormalRotationAsync(FormalRotationJournalProtocol.Prepare(journal, intent, environmentId, hardDeadline), ct);

    internal Task<GovernanceWitnessState> PrepareFormalRotationRevocationAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct) =>
        SendFormalRotationAsync(FormalRotationJournalProtocol.PrepareRevocation(journal, intent, scheduleId, environmentId, hardDeadline), ct);

    internal Task<GovernanceWitnessState> PrepareFormalRotationDecisionAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, string decision, CancellationToken ct) =>
        SendFormalRotationDecisionAsync(FormalRotationDecisionJournalProtocol.Prepare(journal, intent, scheduleId, environmentId, hardDeadline, decision), ct);

    internal Task<GovernanceWitnessState> ObserveFormalRotationDecisionAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, string decision, CancellationToken ct) =>
        SendFormalRotationDecisionAsync(FormalRotationDecisionJournalProtocol.Observe(journal, intent, scheduleId, environmentId, hardDeadline, decision), ct);

    private async Task<GovernanceWitnessState> SendFormalRotationDecisionAsync(FormalRotationDecisionPrepareRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = FormalRotationDecisionJournalProtocol.EncodeRequest(request);
        var response = await transport.PostAsync(endpoint, bytes, GovernanceJournalProtocol.MaxWireBytes, ct);
        return FormalRotationDecisionJournalProtocol.VerifyResponse(request, response, trust);
    }

    internal async Task<VerifiedGovernanceLeaseReservation> ReserveAllowLeaseAsync(GovernanceJournalId journal,
        GovernanceJournalPosition expectedConfirmed, GovernanceLeaseReservation reservation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var request = GovernanceLeaseReservationProtocol.Reserve(journal, expectedConfirmed, reservation);
        // These local time checks can only reject. Issuance requires its own trusted time evidence.
        var bytes = GovernanceLeaseReservationProtocol.EncodeRequest(request, clock.GetUtcNow());
        var response = await transport.PostAsync(endpoint, bytes, GovernanceLeaseReservationProtocol.MaxWireBytes, ct);
        return GovernanceLeaseReservationProtocol.VerifyResponse(request, response, trust, clock.GetUtcNow());
    }

    private async Task<GovernanceWitnessState> SendAsync(GovernanceWitnessRequest request, CancellationToken ct)
    {
        var response = await transport.PostAsync(endpoint, GovernanceJournalProtocol.EncodeRequest(request), GovernanceJournalProtocol.MaxWireBytes, ct);
        return GovernanceJournalProtocol.VerifyResponse(request, response, trust);
    }

    private async Task<GovernanceWitnessState> SendFormalRotationAsync(FormalRotationPrepareRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = await transport.PostAsync(endpoint, FormalRotationJournalProtocol.EncodeRequest(request), GovernanceJournalProtocol.MaxWireBytes, ct);
        return FormalRotationJournalProtocol.VerifyResponse(request, response, trust);
    }

    public void Dispose() => transport.Dispose();
}

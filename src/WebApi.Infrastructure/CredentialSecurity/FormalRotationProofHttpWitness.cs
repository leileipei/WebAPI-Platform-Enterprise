using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

internal sealed class GovernanceProofHttpTransport(Uri endpoint, GovernanceWitnessTrust trust, GovernanceJournalId journal,
    BoundedWitnessHttpClient transport) : IDisposable
{
    public GovernanceJournalId Journal { get; } = journal;

    public GovernanceProofHttpTransport(IndependentWitnessRole role, GovernanceJournalId journal, X509Certificate2 certificate)
        : this(BoundedWitnessHttpClient.RequireEndpoint(role.Endpoint), new(role.SourceId, role.KeyId, role.SubjectPublicKeyInfoBase64),
            journal, new(certificate)) { }

    public async Task<TResult> ExchangeAsync<TScope, TResult>(string protocol, TScope scope, CancellationToken ct)
    {
        var request = GovernanceEvidenceExchangeProtocol.Create(Journal, scope);
        var wire = await transport.PostAsync(endpoint, CanonicalJson.Serialize(request), GovernanceEvidenceExchangeProtocol.MaxWireBytes, ct);
        return GovernanceEvidenceExchangeProtocol.Verify<TScope, TResult>(protocol, request, wire, trust);
    }

    public void Dispose() => transport.Dispose();
}

public sealed record FormalRotationProofObservation(FormalRotationRequestProofScope Scope, string Action = "observe");
public sealed record FormalRotationProofSubmission(FormalRotationRequestProof Proof, string Action = "submit");
public sealed record FormalRotationProofReadResult(IReadOnlyList<FormalRotationRequestProof> Proofs);
public sealed record FormalRotationProofStoreResult([property: JsonRequired] bool Stored);

/// <summary>Observes proofs from an independent service or submits completed proofs using authenticated gateway identity.</summary>
public sealed class FormalRotationProofHttpWitness : IFormalRotationRequestProofWitness, IDisposable
{
    public const string Protocol = "rotation-request-proof";
    private readonly GovernanceProofHttpTransport transport;
    private readonly X509Certificate2? ownedCertificate;

    internal FormalRotationProofHttpWitness(GovernanceProofHttpTransport transport) => this.transport = transport;

    public FormalRotationProofHttpWitness(IndependentWitnessRole role, GovernanceJournalId journal, X509Certificate2 certificate)
        : this(new GovernanceProofHttpTransport(role, journal, certificate)) { }

    private FormalRotationProofHttpWitness(GovernanceProofHttpTransport transport, X509Certificate2 ownedCertificate)
        : this(transport) => this.ownedCertificate = ownedCertificate;

    public static FormalRotationProofHttpWitness OpenGatewayPublisher(IndependentWitnessRole role, GovernanceJournalId journal,
        string certificateFile, IReadOnlyCollection<string> otherSigningKeys)
    {
        var certificate = CredentialGovernanceServiceRegistration.ReadCertificate(certificateFile);
        try
        {
            IndependentGovernanceWitnessClients.RequireCertificateForRole(certificate, role, otherSigningKeys);
            return new(new GovernanceProofHttpTransport(role, journal, certificate), certificate);
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<FormalRotationRequestProof>> ObserveAsync(FormalRotationRequestProofScope scope, CancellationToken ct)
    {
        if (scope.Journal != transport.Journal) throw Unavailable();
        var reply = await transport.ExchangeAsync<FormalRotationProofObservation, FormalRotationProofReadResult>(Protocol, new(scope), ct);
        var nodes = scope.Deployment.Nodes.Select(node => (node.NodeId, node.InstanceId)).ToHashSet();
        if (reply.Proofs is null || reply.Proofs.Count > nodes.Count || reply.Proofs.Any(proof => proof is null ||
            proof.EnvironmentId != scope.Deployment.EnvironmentId || proof.CredentialId != scope.ReplacementCredentialId ||
            !nodes.Contains((proof.NodeId, proof.InstanceId)))) throw Unavailable();
        return reply.Proofs;
    }

    public async Task StoreAsync(FormalRotationRequestProof proof, CancellationToken ct)
    {
        var reply = await transport.ExchangeAsync<FormalRotationProofSubmission, FormalRotationProofStoreResult>(Protocol, new(proof), ct);
        if (!reply.Stored) throw Unavailable();
    }

    private static InvalidDataException Unavailable() => new("credential_governance_rotation_request_proof_unavailable");

    public void Dispose()
    {
        transport.Dispose();
        ownedCertificate?.Dispose();
    }
}

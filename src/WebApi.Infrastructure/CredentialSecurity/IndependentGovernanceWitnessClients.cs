using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

/// <summary>A fixed witness endpoint and the signing identity expected in its responses.</summary>
public sealed record IndependentWitnessRole(Uri Endpoint, string SourceId, string KeyId, string SubjectPublicKeyInfoBase64);

public sealed record IndependentGovernanceWitnessOptions
{
    public bool Enabled { get; init; }
    public IndependentWitnessRole? TimeEvidence { get; init; }
    public IndependentWitnessRole? HistoryEvidence { get; init; }
    public IndependentWitnessRole? RecoveryHistory { get; init; }
    public IndependentWitnessRole? GovernanceJournal { get; init; }
    public GovernanceJournalId? JournalId { get; init; }
}

/// <summary>Explicit host composition for the independent evidence and journal transports.</summary>
public sealed class IndependentGovernanceWitnessClients : IFormalRotationJournalWitness, IGovernanceLeaseReservationWitness,
    IGovernanceEnvironmentHistoryWitness, IDisposable
{
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string AnyExtendedKeyUsage = "2.5.29.37.0";
    private readonly IndependentEvidenceHttpClient evidence;
    private readonly IndependentEvidenceTrust evidenceTrust;
    private readonly GovernanceJournalHttpWitness journal;
    private readonly GovernanceEnvironmentHistoryHttpWitness? recoveryHistory;

    public GovernanceJournalId JournalId { get; }
    public IGovernanceJournalWitness JournalWitness => this;
    public IGovernanceLeaseReservationWitness LeaseReservationWitness => this;
    public IFormalRotationJournalWitness FormalRotationWitness => this;
    public IGovernanceEnvironmentHistoryWitness? RecoveryHistoryWitness => recoveryHistory is null ? null : this;

    private IndependentGovernanceWitnessClients(IndependentEvidenceHttpClient evidence, IndependentEvidenceTrust evidenceTrust,
        GovernanceJournalHttpWitness journal, GovernanceJournalId journalId, GovernanceEnvironmentHistoryHttpWitness? recoveryHistory)
    {
        this.evidence = evidence;
        this.evidenceTrust = evidenceTrust;
        this.journal = journal;
        this.recoveryHistory = recoveryHistory;
        JournalId = journalId;
    }

    /// <summary>Returns null for absent or disabled configuration. Enabled configuration must be complete.</summary>
    /// <param name="options">The host's independently provisioned witness settings.</param>
    /// <param name="writerCertificate">The caller retains ownership and must keep this certificate usable until the clients are disposed.</param>
    public static IndependentGovernanceWitnessClients? Create(IndependentGovernanceWitnessOptions? options = null,
        X509Certificate2? writerCertificate = null)
    {
        if (options is not { Enabled: true }) return null;

        var (time, timeKey) = RequireRole(options.TimeEvidence, nameof(options.TimeEvidence));
        var (history, historyKey) = RequireRole(options.HistoryEvidence, nameof(options.HistoryEvidence));
        var (write, writeKey) = RequireRole(options.GovernanceJournal, nameof(options.GovernanceJournal));
        var recovery = options.RecoveryHistory;
        var recoveryKey = recovery is null ? (ECPoint?)null : RequireRole(recovery, nameof(options.RecoveryHistory)).PublicPoint;
        if (options.JournalId is not { } journalId || journalId.AuthorityId == Guid.Empty || journalId.Generation == Guid.Empty)
            throw new ArgumentException("The journal authority and generation are required.", nameof(options.JournalId));

        // The time and history requests have the same body, so their addresses must distinguish them.
        if (string.Equals(time.Endpoint.IdnHost, history.Endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            time.Endpoint.Port == history.Endpoint.Port && time.Endpoint.AbsolutePath == history.Endpoint.AbsolutePath)
            throw new ArgumentException("Time and history evidence require distinct endpoints.", nameof(options));
        if (SameKey(timeKey, historyKey) || SameKey(timeKey, writeKey) || recoveryKey is { } key && SameKey(timeKey, key))
            throw new ArgumentException("The time evidence signing key must be dedicated to that role.", nameof(options.TimeEvidence));

        ArgumentNullException.ThrowIfNull(writerCertificate);
        RequireWriterCertificate(writerCertificate, timeKey, historyKey, writeKey, recoveryKey);

        var evidenceTrust = new IndependentEvidenceTrust(time.SourceId, time.KeyId, time.SubjectPublicKeyInfoBase64,
            history.SourceId, history.KeyId, history.SubjectPublicKeyInfoBase64);
        var journalTrust = new GovernanceWitnessTrust(write.SourceId, write.KeyId, write.SubjectPublicKeyInfoBase64);
        var evidence = new IndependentEvidenceHttpClient(time.Endpoint, history.Endpoint);
        GovernanceEnvironmentHistoryHttpWitness? recoveryHistory = null;
        try
        {
            if (recovery is not null)
                recoveryHistory = new(recovery.Endpoint, new(recovery.SourceId, recovery.KeyId, recovery.SubjectPublicKeyInfoBase64));
            var journal = new GovernanceJournalHttpWitness(write.Endpoint, journalTrust, writerCertificate);
            return new(evidence, evidenceTrust, journal, journalId, recoveryHistory);
        }
        catch
        {
            recoveryHistory?.Dispose();
            evidence.Dispose();
            throw;
        }
    }

    public Task<IndependentEvidenceInspection> InspectAsync(EvidenceScope scope, TimeProvider clock, IndependentEvidenceLimits limits,
        CancellationToken ct = default) => IndependentEvidenceInspector.InspectAsync(scope, clock, evidence, evidence, evidenceTrust, limits, ct);

    Task<GovernanceEnvironmentHistory> IGovernanceEnvironmentHistoryWitness.ObserveAsync(GovernanceJournalId journalId,
        GovernanceJournalPosition position, Guid environmentId, CancellationToken ct) =>
        (recoveryHistory ?? throw new InvalidDataException("credential_governance_environment_history_unavailable"))
            .ObserveAsync(RequireConfiguredJournal(journalId), position, environmentId, ct);

    Task<GovernanceWitnessState> IGovernanceJournalWitness.ObserveAsync(GovernanceJournalId journalId, Guid? operationId,
        CancellationToken ct) => journal.ObserveAsync(RequireConfiguredJournal(journalId), operationId, ct);

    Task<GovernanceWitnessState> IGovernanceJournalWitness.PrepareAsync(GovernanceJournalId journalId, GovernanceWriteIntent intent,
        CancellationToken ct) => journal.PrepareAsync(RequireConfiguredJournal(journalId), intent, ct);

    Task<GovernanceWitnessState> IGovernanceJournalWitness.ConfirmAsync(GovernanceJournalId journalId, GovernanceWriteIntent intent,
        CancellationToken ct) => journal.ConfirmAsync(RequireConfiguredJournal(journalId), intent, ct);

    Task<GovernanceWitnessState> IFormalRotationJournalWitness.PrepareFormalRotationAsync(GovernanceJournalId journalId,
        GovernanceWriteIntent intent, Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct) =>
        journal.PrepareFormalRotationAsync(RequireConfiguredJournal(journalId), intent, environmentId, hardDeadline, ct);

    Task<GovernanceWitnessState> IFormalRotationJournalWitness.PrepareFormalRotationRevocationAsync(GovernanceJournalId journalId,
        GovernanceWriteIntent intent, Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct) =>
        journal.PrepareFormalRotationRevocationAsync(RequireConfiguredJournal(journalId), intent, scheduleId, environmentId, hardDeadline, ct);

    Task<VerifiedGovernanceLeaseReservation> IGovernanceLeaseReservationWitness.ReserveAllowLeaseAsync(GovernanceJournalId journalId,
        GovernanceJournalPosition expectedConfirmed, GovernanceLeaseReservation reservation, CancellationToken ct) =>
        journal.ReserveAllowLeaseAsync(RequireConfiguredJournal(journalId), expectedConfirmed, reservation, ct);

    private GovernanceJournalId RequireConfiguredJournal(GovernanceJournalId journalId) => journalId == JournalId
        ? journalId
        : throw new InvalidDataException("credential_governance_witness_unavailable");

    private static (IndependentWitnessRole Role, ECPoint PublicPoint) RequireRole(IndependentWitnessRole? role, string parameter)
    {
        if (role is null || !SecuritySignedEncoding.KeyId(role.SourceId) || !SecuritySignedEncoding.KeyId(role.KeyId))
            throw new ArgumentException("A witness source ID and key ID are required for this role.", parameter);

        try { BoundedWitnessHttpClient.RequireEndpoint(role.Endpoint); }
        catch (ArgumentException) { throw new ArgumentException("A fixed HTTPS endpoint is required for this role.", parameter); }

        if (role.SubjectPublicKeyInfoBase64 is { Length: > 0 and <= 1024 } encoded)
        {
            try
            {
                using var key = ECDsa.Create();
                var bytes = Convert.FromBase64String(encoded);
                key.ImportSubjectPublicKeyInfo(bytes, out var read);
                SecuritySignedEncoding.RequireP256(key);
                if (read == bytes.Length && Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) == encoded)
                    return (role, key.ExportParameters(false).Q);
            }
            catch (Exception error) when (error is FormatException or CryptographicException or InvalidDataException)
            {
                // Do not include configured key material in a startup error.
            }
        }

        throw new ArgumentException("This role requires a canonical Base64 P-256 DER SPKI public key.", parameter);
    }

    private static void RequireWriterCertificate(X509Certificate2 writerCertificate, ECPoint timeKey, ECPoint historyKey, ECPoint writeKey,
        ECPoint? recoveryKey)
    {
        try
        {
            if (!writerCertificate.HasPrivateKey)
                throw new ArgumentException("The journal writer certificate requires a private key.", nameof(writerCertificate));

            var extensions = writerCertificate.Extensions;
            if (extensions.OfType<X509KeyUsageExtension>().Any(usage => !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)) ||
                extensions.OfType<X509EnhancedKeyUsageExtension>().Any(usage => !usage.EnhancedKeyUsages.Cast<Oid>()
                    .Any(oid => oid.Value is ClientAuthentication or AnyExtendedKeyUsage)))
                throw new ArgumentException("The journal writer certificate does not permit TLS client authentication.", nameof(writerCertificate));

            using var certificateKey = writerCertificate.GetECDsaPublicKey();
            if (certificateKey is not null)
            {
                var publicPoint = certificateKey.ExportParameters(false).Q;
                if (SameKey(publicPoint, timeKey) || SameKey(publicPoint, historyKey) || SameKey(publicPoint, writeKey) ||
                    recoveryKey is { } key && SameKey(publicPoint, key))
                    throw new ArgumentException("The journal writer credential must use a different key from witness response signing.", nameof(writerCertificate));
            }
        }
        catch (Exception error) when (error is CryptographicException or ObjectDisposedException)
        {
            throw new ArgumentException("The journal writer certificate is invalid.", nameof(writerCertificate));
        }
    }

    private static bool SameKey(ECPoint first, ECPoint second) =>
        first.X.AsSpan().SequenceEqual(second.X) && first.Y.AsSpan().SequenceEqual(second.Y);

    public void Dispose()
    {
        recoveryHistory?.Dispose();
        journal.Dispose();
        evidence.Dispose();
    }
}

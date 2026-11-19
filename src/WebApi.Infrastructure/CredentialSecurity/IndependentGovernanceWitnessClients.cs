using System.Collections.Frozen;
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
    public IndependentWitnessRole? DeploymentEvidence { get; init; }
    public IndependentWitnessRole? RotationProofs { get; init; }
    public IndependentWitnessRole? LegacyInitializationHistory { get; init; }
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
    internal FormalRotationProofHttpWitness? RotationProofWitness { get; }
    internal GovernanceProofHttpTransport? LegacyHistoryTransport { get; }

    public GovernanceJournalId JournalId { get; }
    public SecurityLeaseEvidenceSources LeaseEvidenceSources { get; }
    public IGovernanceJournalWitness JournalWitness => this;
    public IGovernanceLeaseReservationWitness LeaseReservationWitness => this;
    public IFormalRotationJournalWitness FormalRotationWitness => this;
    public IGovernanceEnvironmentHistoryWitness? RecoveryHistoryWitness => recoveryHistory is null ? null : this;

    private IndependentGovernanceWitnessClients(IndependentEvidenceHttpClient evidence, IndependentEvidenceTrust evidenceTrust,
        GovernanceJournalHttpWitness journal, GovernanceJournalId journalId, GovernanceEnvironmentHistoryHttpWitness? recoveryHistory,
        FormalRotationProofHttpWitness? rotationProofWitness, GovernanceProofHttpTransport? legacyHistoryTransport,
        IReadOnlySet<string> independentPublicKeys)
    {
        this.evidence = evidence;
        this.evidenceTrust = evidenceTrust;
        LeaseEvidenceSources = new(evidence, evidence, evidenceTrust, independentPublicKeys);
        this.journal = journal;
        this.recoveryHistory = recoveryHistory;
        RotationProofWitness = rotationProofWitness;
        LegacyHistoryTransport = legacyHistoryTransport;
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
        var deploymentKey = options.DeploymentEvidence is null ? (ECPoint?)null : RequireDeploymentRole(options).PublicPoint;
        var rotation = options.RotationProofs;
        var rotationKey = rotation is null ? (ECPoint?)null : RequireDedicatedRole(rotation, nameof(options.RotationProofs),
            options.TimeEvidence, options.HistoryEvidence, options.GovernanceJournal, recovery, options.LegacyInitializationHistory).PublicPoint;
        var legacy = options.LegacyInitializationHistory;
        var legacyKey = legacy is null ? (ECPoint?)null : RequireDedicatedRole(legacy, nameof(options.LegacyInitializationHistory),
            options.TimeEvidence, options.HistoryEvidence, options.GovernanceJournal, recovery).PublicPoint;
        if (options.JournalId is not { } journalId || journalId.AuthorityId == Guid.Empty || journalId.Generation == Guid.Empty)
            throw new ArgumentException("The journal authority and generation are required.", nameof(options.JournalId));

        var evidenceTrust = RequireEvidenceTrust(time, timeKey, history, historyKey);
        if (SameKey(timeKey, writeKey) || recoveryKey is { } key && SameKey(timeKey, key))
            throw new ArgumentException("The time evidence signing key must be dedicated to that role.", nameof(options.TimeEvidence));

        ArgumentNullException.ThrowIfNull(writerCertificate);
        RequireWriterCertificate(writerCertificate, timeKey, historyKey, writeKey, recoveryKey, deploymentKey, rotationKey, legacyKey);

        var journalTrust = new GovernanceWitnessTrust(write.SourceId, write.KeyId, write.SubjectPublicKeyInfoBase64);
        var evidence = new IndependentEvidenceHttpClient(time.Endpoint, history.Endpoint);
        GovernanceEnvironmentHistoryHttpWitness? recoveryHistory = null;
        FormalRotationProofHttpWitness? rotationProofs = null;
        GovernanceProofHttpTransport? legacyTransport = null;
        try
        {
            if (recovery is not null)
                recoveryHistory = new(recovery.Endpoint, new(recovery.SourceId, recovery.KeyId, recovery.SubjectPublicKeyInfoBase64));
            if (rotation is not null) rotationProofs = new(rotation, journalId, writerCertificate);
            if (legacy is not null) legacyTransport = new(legacy, journalId, writerCertificate);
            var journal = new GovernanceJournalHttpWitness(write.Endpoint, journalTrust, writerCertificate);
            return new(evidence, evidenceTrust, journal, journalId, recoveryHistory, rotationProofs, legacyTransport, PublicKeyPins(options));
        }
        catch
        {
            legacyTransport?.Dispose();
            rotationProofs?.Dispose();
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

    Task<GovernanceWitnessState> IFormalRotationJournalWitness.PrepareFormalRotationDecisionAsync(GovernanceJournalId journalId,
        GovernanceWriteIntent intent, Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, string decision, CancellationToken ct) =>
        journal.PrepareFormalRotationDecisionAsync(RequireConfiguredJournal(journalId), intent, scheduleId, environmentId, hardDeadline, decision, ct);

    Task<GovernanceWitnessState> IFormalRotationJournalWitness.ObserveFormalRotationDecisionAsync(GovernanceJournalId journalId,
        GovernanceWriteIntent intent, Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, string decision, CancellationToken ct) =>
        journal.ObserveFormalRotationDecisionAsync(RequireConfiguredJournal(journalId), intent, scheduleId, environmentId, hardDeadline, decision, ct);

    Task<VerifiedGovernanceLeaseReservation> IGovernanceLeaseReservationWitness.ReserveAllowLeaseAsync(GovernanceJournalId journalId,
        GovernanceJournalPosition expectedConfirmed, GovernanceLeaseReservation reservation, CancellationToken ct) =>
        journal.ReserveAllowLeaseAsync(RequireConfiguredJournal(journalId), expectedConfirmed, reservation, ct);

    private GovernanceJournalId RequireConfiguredJournal(GovernanceJournalId journalId) => journalId == JournalId
        ? journalId
        : throw new InvalidDataException("credential_governance_witness_unavailable");

    public static IndependentEvidenceTrust RequireEvidenceTrust(IndependentWitnessRole? time, IndependentWitnessRole? history)
    {
        var (clock, clockKey) = RequireRole(time, nameof(time));
        var (ledger, ledgerKey) = RequireRole(history, nameof(history));
        return RequireEvidenceTrust(clock, clockKey, ledger, ledgerKey);
    }

    private static IndependentEvidenceTrust RequireEvidenceTrust(IndependentWitnessRole time, ECPoint timeKey,
        IndependentWitnessRole history, ECPoint historyKey)
    {
        if (string.Equals(time.Endpoint.IdnHost, history.Endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            time.Endpoint.Port == history.Endpoint.Port && time.Endpoint.AbsolutePath == history.Endpoint.AbsolutePath)
            throw new ArgumentException("Time and history evidence require distinct endpoints.");
        if (SameKey(timeKey, historyKey))
            throw new ArgumentException("The time evidence signing key must be dedicated to that role.");
        return new(time.SourceId, time.KeyId, time.SubjectPublicKeyInfoBase64, history.SourceId, history.KeyId, history.SubjectPublicKeyInfoBase64);
    }

    public static TrustedSigningKeys RequireGatewayLeaseKeys(IReadOnlyDictionary<string, string> configured, IndependentEvidenceTrust trust)
    {
        if (configured.Count == 0 || configured.Any(pair => !SecuritySignedEncoding.KeyId(pair.Key) ||
            ReadPublicPoint(pair.Value) is null || pair.Value == trust.TimePublicKey || pair.Value == trust.HistoryPublicKey))
            throw new ArgumentException("Independent gateway lease signing keys are required.", nameof(configured));
        return new(configured.ToFrozenDictionary(StringComparer.Ordinal));
    }

    internal static IReadOnlySet<string> PublicKeyPins(IndependentGovernanceWitnessOptions options) =>
        new[] { options.TimeEvidence, options.HistoryEvidence, options.GovernanceJournal, options.RecoveryHistory,
                options.DeploymentEvidence, options.RotationProofs, options.LegacyInitializationHistory }
            .Where(role => role?.SubjectPublicKeyInfoBase64 is not null)
            .Select(role => role!.SubjectPublicKeyInfoBase64).ToFrozenSet(StringComparer.Ordinal);

    internal static (IndependentWitnessRole Role, ECPoint PublicPoint) RequireDeploymentRole(IndependentGovernanceWitnessOptions options) =>
        RequireDedicatedRole(options.DeploymentEvidence, nameof(options.DeploymentEvidence),
            options.TimeEvidence, options.HistoryEvidence, options.GovernanceJournal, options.RecoveryHistory, options.RotationProofs,
            options.LegacyInitializationHistory);

    public static IndependentWitnessRole RequireGatewayRotationProofRole(IndependentWitnessRole? role,
        IndependentEvidenceTrust trust, TrustedSigningKeys leases)
    {
        var (validated, _) = RequireRole(role, nameof(role));
        var key = validated.SubjectPublicKeyInfoBase64;
        if (key == trust.TimePublicKey || key == trust.HistoryPublicKey || leases.SubjectPublicKeys.Values.Contains(key))
            throw new ArgumentException("The rotation proof service requires a dedicated signing key.", nameof(role));
        return validated;
    }

    private static (IndependentWitnessRole Role, ECPoint PublicPoint) RequireDedicatedRole(IndependentWitnessRole? provided, string parameter,
        params IndependentWitnessRole?[] otherRoles)
    {
        var role = RequireRole(provided, parameter);
        foreach (var other in otherRoles)
            if (ReadPublicPoint(other?.SubjectPublicKeyInfoBase64) is { } key && SameKey(role.PublicPoint, key))
                throw new ArgumentException("This evidence role requires a dedicated signing key.", parameter);
        return role;
    }

    internal static void RequireCertificateForRole(X509Certificate2 certificate, IndependentWitnessRole role,
        IReadOnlyCollection<string> otherSigningKeys) => RequireWriterCertificate(certificate,
            [RequireRole(role, nameof(role)).PublicPoint, .. otherSigningKeys.Select(ReadPublicPoint)]);

    private static (IndependentWitnessRole Role, ECPoint PublicPoint) RequireRole(IndependentWitnessRole? role, string parameter)
    {
        if (role is null || !SecuritySignedEncoding.KeyId(role.SourceId) || !SecuritySignedEncoding.KeyId(role.KeyId))
            throw new ArgumentException("A witness source ID and key ID are required for this role.", parameter);

        try { BoundedWitnessHttpClient.RequireEndpoint(role.Endpoint); }
        catch (ArgumentException) { throw new ArgumentException("A fixed HTTPS endpoint is required for this role.", parameter); }

        if (ReadPublicPoint(role.SubjectPublicKeyInfoBase64) is not { } point)
            throw new ArgumentException("This role requires a canonical Base64 P-256 DER SPKI public key.", parameter);
        return (role, point);
    }

    private static ECPoint? ReadPublicPoint(string? encoded)
    {
        if (encoded is not { Length: > 0 and <= 1024 }) return null;
        try
        {
            using var key = ECDsa.Create();
            var bytes = Convert.FromBase64String(encoded);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            SecuritySignedEncoding.RequireP256(key);
            if (read == bytes.Length && Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) == encoded)
                return key.ExportParameters(false).Q;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or InvalidDataException)
        {
            // Do not include configured key material in a startup error.
        }
        return null;
    }

    private static void RequireWriterCertificate(X509Certificate2 writerCertificate, params ECPoint?[] responseKeys)
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
                if (responseKeys.Any(key => key is { } point && SameKey(publicPoint, point)))
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
        LegacyHistoryTransport?.Dispose();
        RotationProofWitness?.Dispose();
        recoveryHistory?.Dispose();
        journal.Dispose();
        evidence.Dispose();
    }
}

using Microsoft.Extensions.DependencyInjection.Extensions;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Storage;
using WebApi.Gateway.Workers;
using WebApi.Infrastructure.CredentialSecurity;

namespace WebApi.Gateway.Security;

public sealed record GatewaySecurityEvidence(TrustedSigningKeys LeaseKeys, IIndependentTimeEvidenceSource Time,
    IIndependentSecurityHistoryWitness History, IndependentEvidenceTrust Trust);

public static class WitnessedGatewaySecurityRegistration
{
    public static IServiceCollection AddWitnessedGatewaySecurity(this IServiceCollection services, GatewaySecurityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Trust.TimePublicKey == evidence.Trust.HistoryPublicKey ||
            evidence.LeaseKeys.SubjectPublicKeys.Values.Any(key => key == evidence.Trust.TimePublicKey || key == evidence.Trust.HistoryPublicKey))
            throw new ArgumentException("Gateway lease, time, and history authorities must have separate signing keys.", nameof(evidence));
        services.TryAddSingleton(evidence);
        return Register(services);
    }

    internal static void AddConfiguredGatewaySecurity(this IServiceCollection services, IConfiguration configuration, GatewaySettings settings)
    {
        var section = configuration.GetSection("CredentialGovernance");
        if (section["Enabled"] is null or "false") return;
        if (bool.TryParse(section["Enabled"], out var enabled) && !enabled) return;
        try
        {
            if (!enabled || settings.ControlPlaneUrl.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException();
            var time = section.GetSection("TimeEvidence").Get<IndependentWitnessRole>();
            var history = section.GetSection("HistoryEvidence").Get<IndependentWitnessRole>();
            var trust = IndependentGovernanceWitnessClients.RequireEvidenceTrust(time, history);
            var configuredKeys = section.GetSection("LeaseVerificationKeys").GetChildren()
                .ToDictionary(key => key.Key, key => key.Value ?? "", StringComparer.Ordinal);
            var leaseKeys = IndependentGovernanceWitnessClients.RequireGatewayLeaseKeys(configuredKeys, trust);
            services.TryAddSingleton(_ => new IndependentEvidenceHttpClient(time!.Endpoint, history!.Endpoint));
            services.TryAddSingleton(sp =>
            {
                var client = sp.GetRequiredService<IndependentEvidenceHttpClient>();
                return new GatewaySecurityEvidence(leaseKeys, client, client, trust);
            });
            ConfigureRotationProofDelivery(services, section, trust, leaseKeys);
            Register(services);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException or InvalidDataException)
        {
            throw new InvalidOperationException("Invalid credential governance settings for gateway.");
        }
    }

    private static void ConfigureRotationProofDelivery(IServiceCollection services, IConfigurationSection section,
        IndependentEvidenceTrust trust, TrustedSigningKeys leases)
    {
        var settings = section.GetSection("RotationProofs");
        if (!settings.Exists()) return;
        var role = IndependentGovernanceWitnessClients.RequireGatewayRotationProofRole(settings.Get<IndependentWitnessRole>(), trust, leases);
        var journal = section.GetSection("JournalId").Get<GovernanceJournalId>() ?? throw new ArgumentException();
        GovernanceJournalProtocol.RequireJournal(journal);
        var certificate = section["RotationProofCertificateFile"];
        if (certificate is null || !Path.IsPathFullyQualified(certificate)) throw new ArgumentException();
        var disjointKeys = new[] { trust.TimePublicKey, trust.HistoryPublicKey }.Concat(leases.SubjectPublicKeys.Values).ToArray();
        services.TryAddSingleton(_ => FormalRotationProofHttpWitness.OpenGatewayPublisher(role, journal, certificate, disjointKeys));
        services.TryAddSingleton<FormalRotationProofDelivery>();
        services.TryAddSingleton<IFormalRotationRequestProofSink>(sp => sp.GetRequiredService<FormalRotationProofDelivery>());
        services.AddHostedService(sp => sp.GetRequiredService<FormalRotationProofDelivery>());
    }

    private static IServiceCollection Register(IServiceCollection services)
    {
        services.TryAddSingleton<WitnessedGatewaySecurity>();
        services.TryAddSingleton<IGovernanceAdmissionStateProvider>(sp => sp.GetRequiredService<WitnessedGatewaySecurity>());
        services.AddHostedService<GatewaySecurityRenewalWorker>();
        return services;
    }
}

internal sealed class WitnessedGatewaySecurity : IGovernanceAdmissionStateProvider, IAsyncDisposable
{
    private static readonly IndependentEvidenceLimits Limits = new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
    private readonly NodeClient nodes;
    private readonly GatewaySettings settings;
    private readonly GatewaySecurityEvidence evidence;
    private readonly TimeProvider clock;
    private readonly SecurityStateStore state;
    private bool restored;
    private AdmissionLease? verified;
    private sealed record AdmissionLease(Guid Id, long Start, long Frequency, TimeSpan ValidFor, DateTimeOffset AppliedAt);

    public WitnessedGatewaySecurity(NodeClient nodes, GatewaySettings settings, GatewaySecurityEvidence evidence, TimeProvider clock)
    {
        this.nodes = nodes;
        this.settings = settings;
        this.evidence = evidence;
        this.clock = clock;
        state = new(new(settings.EnvironmentId, () => nodes.NodeId, settings.InstanceId), new SecurityLkgStore(settings.LkgDirectory), clock);
    }

    public SecurityStateStore? GetActiveState(RuntimeGeneration generation)
    {
        var approval = Volatile.Read(ref verified);
        return generation.Snapshot.EnvironmentId == settings.EnvironmentId && approval is not null && IsCurrent(approval) ? state : null;
    }

    internal async Task RenewAsync(CancellationToken ct)
    {
        if (!restored)
        {
            // Restore authentic high-water only; the old instance's lease is never a new approval.
            await state.RestoreAsync(evidence.LeaseKeys, ct);
            restored = true;
        }
        var material = await nodes.SecurityMaterialAsync(ct);
        state.InvalidateReadOnlyHandoff();
        var nodeId = nodes.NodeId ?? throw Unavailable();
        var checkpoint = SecurityCheckpointVerifier.VerifyCheckpoint(new(material.Checkpoint), evidence.LeaseKeys, settings.EnvironmentId);
        var lease = SecurityCheckpointVerifier.VerifyNodeLease(new(material.Lease), evidence.LeaseKeys, settings.EnvironmentId, nodeId, settings.InstanceId);
        var cp = checkpoint.Payload;
        var grant = lease.Payload;
        if (!cp.Full || !grant.AllowAuthenticatedTraffic || grant.CheckpointHash != checkpoint.Hash ||
            grant.SecurityGeneration != cp.SecurityGeneration || grant.Epoch != cp.Epoch) throw Unavailable();

        var start = clock.GetTimestamp();
        var frequency = clock.TimestampFrequency;
        var result = await IndependentEvidenceInspector.InspectAsync(
            new("gateway-admission", settings.EnvironmentId, nodeId, settings.InstanceId, cp.SecurityGeneration, cp.Epoch, cp.HeadHash),
            clock, evidence.Time, evidence.History, evidence.Trust, Limits, ct);
        var now = clock.GetUtcNow();
        var skew = TimeSpan.FromSeconds(grant.MaximumClockSkewSeconds);
        var expiry = new[] { grant.FreshUntil, cp.FreshUntil, result.ValidUntil }.Min();
        if (now < result.LowerAtSource - skew || now > result.ConservativeUpperAtInspection + skew ||
            grant.IssuedAt > result.ConservativeUpperAtInspection || expiry <= result.ConservativeUpperAtInspection)
            throw Unavailable();

        if (!(await state.ApplyAsync(checkpoint, lease, ct)).Applied) throw Unavailable();
        var approval = new AdmissionLease(grant.LeaseId, start, frequency, expiry - result.ConservativeUpperAtInspection, clock.GetUtcNow());
        if (!IsCurrent(approval)) throw Unavailable();
        Volatile.Write(ref verified, approval);
        var proof = new CurrentSecurityProof(settings.InstanceId, checkpoint.Bytes.ToArray(), lease.Bytes.ToArray(),
            approval.AppliedAt, Durable: true, AdmissionEnforced: true);
        await nodes.RecordSecurityProofAsync(proof, ct);
        foreach (var challenge in grant.AckChallenges ?? [])
        {
            if (!IsCurrent(approval)) return;
            var acknowledgement = new SecurityAck(challenge.OperationId, nodeId, settings.InstanceId, settings.EnvironmentId,
                cp.SecurityGeneration, cp.Epoch, checkpoint.Hash, approval.AppliedAt, true, true, true,
                challenge.ChallengeNonce, null, proof.Checkpoint, proof.Lease);
            await nodes.RecordSecurityAckAsync(acknowledgement, ct);
        }
    }

    private bool IsCurrent(AdmissionLease approval)
    {
        if (approval.Id != state.CurrentAdmissionLeaseId || approval.Frequency <= 0 || approval.Frequency != clock.TimestampFrequency)
            return false;
        var timestamp = clock.GetTimestamp();
        return timestamp >= approval.Start && clock.GetElapsedTime(approval.Start, timestamp) < approval.ValidFor && state.IsReady;
    }

    public ValueTask DisposeAsync() => state.DisposeAsync();
    private static InvalidDataException Unavailable() => new("gateway_security_evidence_unavailable");
}

internal sealed class GatewaySecurityRenewalWorker(WitnessedGatewaySecurity security, GatewaySettings settings,
    TimeProvider clock, ILogger<GatewaySecurityRenewalWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!settings.AutomaticUpdates) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        try
        {
            do
            {
                try { await security.RenewAsync(ct); }
                catch (Exception error) when (!ct.IsCancellationRequested)
                { log.LogWarning("Gateway security renewal unavailable ({ErrorType})", error.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

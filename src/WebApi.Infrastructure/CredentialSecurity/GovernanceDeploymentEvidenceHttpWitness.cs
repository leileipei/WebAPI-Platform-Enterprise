using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;

namespace WebApi.Infrastructure.CredentialSecurity;

internal sealed class GovernanceDeploymentEvidenceHttpWitness(Uri endpoint, GovernanceWitnessTrust trust, GovernanceJournalId journal,
    TimeProvider clock, GovernanceDeploymentEvidenceCache cache, BoundedWitnessHttpClient transport, Action? requireDisjointSigner = null) : IDisposable
{
    internal static readonly TimeSpan MaximumSkew = TimeSpan.FromSeconds(2);

    public GovernanceDeploymentEvidenceHttpWitness(IndependentWitnessRole role, GovernanceJournalId journal,
        TimeProvider clock, GovernanceDeploymentEvidenceCache cache, Action requireDisjointSigner)
        : this(BoundedWitnessHttpClient.RequireEndpoint(role.Endpoint), new(role.SourceId, role.KeyId, role.SubjectPublicKeyInfoBase64),
            journal, clock, cache, new(), requireDisjointSigner) { }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            requireDisjointSigner?.Invoke();
            var request = GovernanceDeploymentEvidenceProtocol.Observe(journal);
            var wall = clock.GetUtcNow();
            var start = clock.GetTimestamp();
            var frequency = clock.TimestampFrequency;
            var wire = await transport.PostAsync(endpoint, CanonicalJson.Serialize(request), GovernanceDeploymentEvidenceProtocol.MaxWireBytes, ct);
            var payload = GovernanceDeploymentEvidenceProtocol.Verify(request, wire, trust);
            var end = clock.GetTimestamp();
            var completed = clock.GetUtcNow();
            if (frequency <= 0 || clock.TimestampFrequency != frequency || end < start || completed < wall ||
                payload.ObservedAt < wall - MaximumSkew || payload.ObservedAt > completed + MaximumSkew ||
                payload.ValidUntil - payload.ObservedAt <= clock.GetElapsedTime(start, end) + MaximumSkew ||
                payload.ValidUntil - completed <= MaximumSkew)
                throw new InvalidDataException("credential_governance_deployment_evidence_unavailable");
            cache.Replace(payload, start, frequency);
        }
        catch
        {
            cache.Clear();
            throw;
        }
    }

    public void Dispose() => transport.Dispose();
}

internal sealed class GovernanceDeploymentEvidenceCache(TimeProvider clock) : ICredentialRevocationCoverage, ICredentialRunningIngressWitness,
    ICredentialRevocationHistory, ICredentialArchivedIngressWitness, IFormalRotationLeaseHorizonWitness, ICredentialRetiredTargetWitness
{
    private sealed record Snapshot(DateTimeOffset ObservedAt, DateTimeOffset ValidUntil, long Start, long Frequency,
        IReadOnlyDictionary<(string Purpose, string ScopeHash), GovernanceDeploymentClaim> Claims,
        IReadOnlyDictionary<string, GovernanceDeploymentHorizon> Horizons,
        IReadOnlyDictionary<string, GovernanceRetiredTargetCoverage> RetiredTargets);

    private Snapshot? latest;

    public void Replace(GovernanceDeploymentEvidencePayload payload, long start, long frequency) =>
        Volatile.Write(ref latest, new(payload.ObservedAt, payload.ValidUntil, start, frequency,
            payload.Claims.ToDictionary(x => (x.Purpose, x.ScopeHash)), payload.Horizons.ToDictionary(x => x.ScopeHash, StringComparer.Ordinal),
            (payload.RetiredTargets ?? []).ToDictionary(x => GovernanceDeploymentEvidenceProtocol.ScopeHash(
                GovernanceRetiredTargetProtocol.Purpose, x.Scope), StringComparer.Ordinal)));

    public void Clear() => Volatile.Write(ref latest, null);

    // Configured hosts bind coverage to the exact deployment and addresses in the richer scope.
    bool ICredentialRevocationCoverage.IsVerified(CredentialRevocationCoverageScope scope) => false;

    public DateTimeOffset? VerifiedUntil(CredentialRunningIngressScope scope, DateTimeOffset now) =>
        Claim(GovernanceDeploymentEvidenceProtocol.RunningIngress, scope, now)?.Bound;

    public bool IsVerified(CredentialRevocationHistoryScope scope) =>
        Claim(GovernanceDeploymentEvidenceProtocol.RevocationHistory, scope, clock.GetUtcNow()) is not null;

    public CredentialArchivedIngressSeal? GetVerifiedSeal(CredentialArchivedIngressScope scope)
    {
        var evidence = Claim(GovernanceDeploymentEvidenceProtocol.ArchivedIngress, scope, clock.GetUtcNow());
        return evidence is { } qualified ? new(scope, qualified.Claim.ProofHash, qualified.Claim.EvidenceAt, qualified.Bound) : null;
    }

    public FormalRotationLeaseHorizonEvidence? GetVerifiedHorizon(FormalRotationLeaseScope scope)
    {
        var snapshot = Volatile.Read(ref latest);
        var now = clock.GetUtcNow();
        var upper = CurrentUpper(snapshot, now);
        if (snapshot is null || upper is null || !snapshot.Horizons.TryGetValue(
                GovernanceDeploymentEvidenceProtocol.ScopeHash(GovernanceDeploymentEvidenceProtocol.RotationLeaseHorizon, scope), out var proof) ||
            upper >= proof.ValidUntil)
            return null;
        return new(scope, proof.ProofHash, proof.Complete, proof.HasPendingWrite, proof.Lower, proof.Upper,
            now + (proof.ValidUntil - upper.Value), proof.LatestAllowExpiry);
    }

    public GovernanceRetiredTargetCoverage? GetVerifiedCoverage(GovernanceRetiredTargetScope scope)
    {
        var snapshot = Volatile.Read(ref latest);
        var now = clock.GetUtcNow();
        var upper = CurrentUpper(snapshot, now);
        if (snapshot is null || upper is null || !snapshot.RetiredTargets.TryGetValue(
                GovernanceDeploymentEvidenceProtocol.ScopeHash(GovernanceRetiredTargetProtocol.Purpose, scope), out var proof) ||
            upper >= proof.ValidUntil || proof.IngressIsolated &&
                upper.Value - proof.Upper > GovernanceRetiredTargetProtocol.MaximumObservationAge)
            return null;
        return proof with { ValidUntil = now + (proof.ValidUntil - upper.Value) };
    }

    private (GovernanceDeploymentClaim Claim, DateTimeOffset Bound)? Claim<T>(string purpose, T scope, DateTimeOffset now)
    {
        var snapshot = Volatile.Read(ref latest);
        var upper = CurrentUpper(snapshot, now);
        if (snapshot is null || upper is null || !snapshot.Claims.TryGetValue(
                (purpose, GovernanceDeploymentEvidenceProtocol.ScopeHash(purpose, scope)), out var proof) || upper >= proof.ValidUntil)
            return null;
        return (proof, now + (proof.ValidUntil - upper.Value));
    }

    private DateTimeOffset? CurrentUpper(Snapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null || clock.TimestampFrequency != snapshot.Frequency) return null;
        var timestamp = clock.GetTimestamp();
        var skew = GovernanceDeploymentEvidenceHttpWitness.MaximumSkew;
        var wall = clock.GetUtcNow();
        if (timestamp < snapshot.Start || now < snapshot.ObservedAt - skew || wall < snapshot.ObservedAt - skew) return null;
        var elapsed = clock.GetElapsedTime(snapshot.Start, timestamp);
        if (elapsed + skew >= snapshot.ValidUntil - snapshot.ObservedAt) return null;
        var upper = snapshot.ObservedAt + elapsed + skew;
        if (wall > upper) upper = wall;
        if (now > upper) upper = now;
        return upper < snapshot.ValidUntil ? upper : null;
    }
}

internal sealed class GovernanceDeploymentEvidenceRefreshWorker(IServiceProvider services, TimeProvider clock,
    ILogger<GovernanceDeploymentEvidenceRefreshWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        try
        {
            do
            {
                try { await services.GetRequiredService<GovernanceDeploymentEvidenceHttpWitness>().RefreshAsync(ct); }
                catch (Exception error) when (!ct.IsCancellationRequested && error is (InvalidDataException or InvalidOperationException or
                    ArgumentException or ApiException))
                {
                    log.LogWarning("Independent deployment evidence unavailable ({ErrorType})", error.GetType().Name);
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

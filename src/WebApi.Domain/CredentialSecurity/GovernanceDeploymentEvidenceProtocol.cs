using System.Security.Cryptography;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;

namespace WebApi.Domain.CredentialSecurity;

public sealed record GovernanceDeploymentEvidenceRequest(GovernanceJournalId Journal, string Nonce);

public sealed record GovernanceDeploymentClaim(string Purpose, string ScopeHash, string ProofHash,
    DateTimeOffset EvidenceAt, DateTimeOffset ValidUntil);

public sealed record GovernanceDeploymentHorizon(string ScopeHash, string ProofHash, DateTimeOffset Lower, DateTimeOffset Upper,
    DateTimeOffset ValidUntil, [property: JsonRequired] bool Complete, [property: JsonRequired] bool HasPendingWrite,
    [property: JsonRequired] DateTimeOffset? LatestAllowExpiry);

public sealed record GovernanceDeploymentEvidencePayload(string RequestHash, string SourceId, string KeyId,
    DateTimeOffset ObservedAt, DateTimeOffset ValidUntil, IReadOnlyList<GovernanceDeploymentClaim> Claims,
    IReadOnlyList<GovernanceDeploymentHorizon> Horizons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GovernanceRetiredTargetCoverage>? RetiredTargets = null);

public sealed record SignedGovernanceDeploymentEvidenceResponse([property: JsonRequired] GovernanceDeploymentEvidencePayload Payload,
    [property: JsonRequired] string Signature);

public static class GovernanceDeploymentEvidenceProtocol
{
    public const string RequestDomain = "webapi.credential-governance.deployment-evidence-request.v1";
    public const string ResponseDomain = "webapi.credential-governance.deployment-evidence-response.v1";
    public const string RunningIngress = "running-ingress";
    public const string ArchivedIngress = "archived-ingress";
    public const string RevocationHistory = "revocation-history";
    public const string RotationLeaseHorizon = "rotation-lease-horizon";
    public const int MaxWireBytes = 512 * 1024;

    private const string Unavailable = "credential_governance_deployment_evidence_unavailable";
    private static readonly TimeSpan MaximumValidity = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumIngressObservationAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumIngressValidity = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumTimeWidth = TimeSpan.FromSeconds(4);

    public static GovernanceDeploymentEvidenceRequest Observe(GovernanceJournalId journal)
    {
        GovernanceJournalProtocol.RequireJournal(journal);
        return new(journal, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
    }

    public static string RequestHash(GovernanceDeploymentEvidenceRequest request)
    {
        GovernanceJournalProtocol.RequireJournal(request.Journal);
        if (!SecuritySignedEncoding.HexHash(request.Nonce)) throw Invalid();
        return Hash(SecuritySignedEncoding.Unsigned(RequestDomain, request));
    }

    public static string ScopeHash<T>(string purpose, T scope) =>
        Hash(SecuritySignedEncoding.Unsigned($"webapi.credential-governance.{purpose}-scope.v1", scope));

    public static GovernanceDeploymentEvidencePayload Verify(GovernanceDeploymentEvidenceRequest request, byte[] wire, GovernanceWitnessTrust trust)
    {
        var response = IndependentEvidenceWire.Read<SignedGovernanceDeploymentEvidenceResponse>(wire, MaxWireBytes, Unavailable);
        var payload = response.Payload;
        if (payload is null || payload.RequestHash != RequestHash(request) || payload.SourceId != trust.SourceId ||
            payload.KeyId != trust.KeyId || !SecuritySignedEncoding.KeyId(payload.SourceId) || !SecuritySignedEncoding.KeyId(payload.KeyId))
            throw Invalid();
        IndependentEvidenceWire.Verify(ResponseDomain, payload, response.Signature, trust.PublicKey, Unavailable);

        if (!IsUtc(payload.ObservedAt) || !IsUtc(payload.ValidUntil) || payload.ValidUntil <= payload.ObservedAt ||
            payload.ValidUntil - payload.ObservedAt > MaximumValidity || payload.Claims is null || payload.Horizons is null ||
            payload.Claims.Any(claim => !ValidClaim(claim, payload)) || payload.Horizons.Any(horizon => !ValidHorizon(horizon, payload)) ||
            payload.Claims.Select(x => (x.Purpose, x.ScopeHash)).Distinct().Count() != payload.Claims.Count ||
            payload.Horizons.Select(x => x.ScopeHash).Distinct(StringComparer.Ordinal).Count() != payload.Horizons.Count ||
            payload.RetiredTargets is { } retired && (retired.Any(proof =>
                !GovernanceRetiredTargetProtocol.Valid(proof) || proof.Scope.Journal != request.Journal ||
                proof.Lower > payload.ObservedAt || proof.Upper < payload.ObservedAt || proof.ValidUntil > payload.ValidUntil) ||
                retired.Select(proof => ScopeHash(GovernanceRetiredTargetProtocol.Purpose, proof.Scope))
                    .Distinct(StringComparer.Ordinal).Count() != retired.Count))
            throw Invalid();
        return payload;
    }

    private static bool ValidClaim(GovernanceDeploymentClaim? claim, GovernanceDeploymentEvidencePayload payload) =>
        claim is not null && claim.Purpose is RunningIngress or ArchivedIngress or RevocationHistory &&
        SecuritySignedEncoding.HexHash(claim.ScopeHash) && SecuritySignedEncoding.HexHash(claim.ProofHash) &&
        IsUtc(claim.EvidenceAt) && claim.EvidenceAt <= payload.ObservedAt && IsUtc(claim.ValidUntil) &&
        payload.ObservedAt < claim.ValidUntil && claim.ValidUntil <= payload.ValidUntil &&
        (claim.Purpose != RunningIngress || payload.ObservedAt - claim.EvidenceAt <= MaximumIngressObservationAge &&
            claim.ValidUntil - claim.EvidenceAt <= MaximumIngressValidity);

    private static bool ValidHorizon(GovernanceDeploymentHorizon? horizon, GovernanceDeploymentEvidencePayload payload) =>
        horizon is not null && SecuritySignedEncoding.HexHash(horizon.ScopeHash) && SecuritySignedEncoding.HexHash(horizon.ProofHash) &&
        IsUtc(horizon.Lower) && IsUtc(horizon.Upper) && horizon.Lower <= payload.ObservedAt && payload.ObservedAt <= horizon.Upper &&
        horizon.Upper - horizon.Lower <= MaximumTimeWidth && IsUtc(horizon.ValidUntil) &&
        horizon.ValidUntil > horizon.Upper && horizon.ValidUntil <= payload.ValidUntil &&
        (horizon.LatestAllowExpiry is not DateTimeOffset expiry || IsUtc(expiry));

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero &&
        value > DateTimeOffset.MinValue && value < DateTimeOffset.MaxValue;
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static InvalidDataException Invalid() => new(Unavailable);
}

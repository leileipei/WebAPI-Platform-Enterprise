using System.Security.Cryptography;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;

namespace WebApi.Domain.CredentialSecurity;

public sealed record FormalRotationDecisionPrepareRequest([property: JsonRequired] GovernanceJournalId Journal,
    [property: JsonRequired] string Action, [property: JsonRequired] string Nonce,
    [property: JsonRequired] GovernanceWriteIntent Intent, [property: JsonRequired] Guid ScheduleId,
    [property: JsonRequired] Guid EnvironmentId, [property: JsonRequired] DateTimeOffset HardDeadline,
    [property: JsonRequired] string Decision);

public sealed record FormalRotationDecisionPreparePayload([property: JsonRequired] string RequestHash,
    [property: JsonRequired] string SourceId, [property: JsonRequired] string KeyId, [property: JsonRequired] Guid ScheduleId,
    [property: JsonRequired] Guid EnvironmentId, [property: JsonRequired] DateTimeOffset HardDeadline,
    [property: JsonRequired] string Decision, [property: JsonRequired] DateTimeOffset AllowLeaseCutoff,
    [property: JsonRequired] DateTimeOffset TrustedTimeUpper, [property: JsonRequired] bool Durable,
    [property: JsonRequired] GovernanceWitnessState State);

public sealed record SignedFormalRotationDecisionPrepareResponse([property: JsonRequired] FormalRotationDecisionPreparePayload Payload,
    [property: JsonRequired] string Signature);

public static class FormalRotationDecisionJournalProtocol
{
    public const string RequestDomain = "webapi.credential-governance.formal-rotation-decision-request.v1";
    public const string ResponseDomain = "webapi.credential-governance.formal-rotation-decision-response.v1";
    private const string PrepareAction = "prepareFormalRotationDecision";
    private const string ObserveAction = "observeFormalRotationDecision";
    private const string Unavailable = "credential_governance_witness_unavailable";
    private static readonly TimeSpan MaximumLeaseClockSkew = TimeSpan.FromSeconds(2);

    public static FormalRotationDecisionPrepareRequest Prepare(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, string decision) =>
        new(journal, PrepareAction, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), intent, scheduleId, environmentId,
            hardDeadline, decision);

    public static FormalRotationDecisionPrepareRequest Observe(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, string decision) =>
        new(journal, ObserveAction, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), intent, scheduleId, environmentId,
            hardDeadline, decision);

    public static byte[] EncodeRequest(FormalRotationDecisionPrepareRequest request)
    {
        ValidateRequest(request);
        return CanonicalJson.Serialize(request);
    }

    public static string RequestHash(FormalRotationDecisionPrepareRequest request)
    {
        ValidateRequest(request);
        return Convert.ToHexStringLower(SHA256.HashData(SecuritySignedEncoding.Unsigned(RequestDomain, request)));
    }

    public static GovernanceWitnessState VerifyResponse(FormalRotationDecisionPrepareRequest request, byte[] response, GovernanceWitnessTrust trust)
    {
        var requestHash = RequestHash(request);
        var wire = IndependentEvidenceWire.Read<SignedFormalRotationDecisionPrepareResponse>(response, GovernanceJournalProtocol.MaxWireBytes, Unavailable);
        var payload = wire.Payload;
        if (trust is null || payload is null || payload.RequestHash != requestHash || payload.SourceId != trust.SourceId ||
            payload.KeyId != trust.KeyId || !SecuritySignedEncoding.KeyId(payload.SourceId) || !SecuritySignedEncoding.KeyId(payload.KeyId))
            throw Invalid();
        IndependentEvidenceWire.Verify(ResponseDomain, payload, wire.Signature, trust.PublicKey, Unavailable);

        var expectedPending = new GovernanceWitnessState(request.Intent.Previous, request.Intent, null);
        var next = GovernanceJournalProtocol.NextPosition(request.Journal, request.Intent);
        var expectedConfirmed = new GovernanceWitnessState(next, null, new(request.Intent.OperationId, next));
        var matchesState = payload.State == expectedPending || request.Action == ObserveAction && payload.State == expectedConfirmed;
        if (!payload.Durable || payload.ScheduleId != request.ScheduleId || payload.EnvironmentId != request.EnvironmentId ||
            payload.Decision != request.Decision || !payload.HardDeadline.EqualsExact(request.HardDeadline) ||
            !payload.AllowLeaseCutoff.EqualsExact(request.HardDeadline - MaximumLeaseClockSkew) || !matchesState ||
            !ValidUtc(payload.TrustedTimeUpper) || payload.TrustedTimeUpper >= request.HardDeadline)
            throw Invalid();

        return payload.State;
    }

    private static void ValidateRequest(FormalRotationDecisionPrepareRequest request)
    {
        if (request is null || request.Action is not (PrepareAction or ObserveAction) || request.Intent is null ||
            request.ScheduleId == Guid.Empty || request.ScheduleId == request.Intent.OperationId || request.EnvironmentId == Guid.Empty ||
            request.Decision is not ("Complete" or "AbortRequested" or "AbortConfirmed") || !SecuritySignedEncoding.HexHash(request.Nonce) ||
            !ValidUtc(request.HardDeadline) || request.HardDeadline.Ticks <= MaximumLeaseClockSkew.Ticks)
            throw Invalid();

        _ = GovernanceJournalProtocol.NextPosition(request.Journal, request.Intent);
    }

    private static bool ValidUtc(DateTimeOffset instant) => instant.Offset == TimeSpan.Zero &&
        instant > DateTimeOffset.MinValue && instant < DateTimeOffset.MaxValue;

    private static InvalidDataException Invalid() => new(Unavailable);
}

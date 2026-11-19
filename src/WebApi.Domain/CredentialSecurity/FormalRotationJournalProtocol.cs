using System.Security.Cryptography;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;

namespace WebApi.Domain.CredentialSecurity;

public sealed record FormalRotationPrepareRequest([property: JsonRequired] GovernanceJournalId Journal,
    [property: JsonRequired] string Action, [property: JsonRequired] string Nonce,
    [property: JsonRequired] GovernanceWriteIntent Intent, [property: JsonRequired] Guid EnvironmentId,
    [property: JsonRequired] DateTimeOffset HardDeadline, [property: JsonRequired] Guid? ScheduleId);

public sealed record FormalRotationPreparePayload([property: JsonRequired] string RequestHash,
    [property: JsonRequired] string SourceId, [property: JsonRequired] string KeyId, [property: JsonRequired] Guid EnvironmentId,
    [property: JsonRequired] DateTimeOffset HardDeadline, [property: JsonRequired] Guid? ScheduleId,
    [property: JsonRequired] DateTimeOffset AllowLeaseCutoff, [property: JsonRequired] DateTimeOffset? TrustedTimeLower,
    [property: JsonRequired] bool Durable, [property: JsonRequired] GovernanceWitnessState State);

public sealed record SignedFormalRotationPrepareResponse([property: JsonRequired] FormalRotationPreparePayload Payload,
    [property: JsonRequired] string Signature);

public static class FormalRotationJournalProtocol
{
    public const string RequestDomain = "webapi.credential-governance.formal-rotation-prepare-request.v1";
    public const string ResponseDomain = "webapi.credential-governance.formal-rotation-prepare-response.v1";
    private const string Schedule = "prepareFormalRotation";
    private const string Revoke = "prepareFormalRotationRevocation";
    private const string Unavailable = "credential_governance_witness_unavailable";
    private static readonly TimeSpan MaximumLeaseClockSkew = TimeSpan.FromSeconds(2);

    public static FormalRotationPrepareRequest Prepare(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid environmentId, DateTimeOffset hardDeadline) => new(journal, Schedule, Nonce(), intent, environmentId, hardDeadline, null);

    public static FormalRotationPrepareRequest PrepareRevocation(GovernanceJournalId journal, GovernanceWriteIntent intent,
        Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline) => new(journal, Revoke, Nonce(), intent, environmentId, hardDeadline, scheduleId);

    public static byte[] EncodeRequest(FormalRotationPrepareRequest request)
    {
        ValidateRequest(request);
        return CanonicalJson.Serialize(request);
    }

    public static string RequestHash(FormalRotationPrepareRequest request)
    {
        ValidateRequest(request);
        return Convert.ToHexStringLower(SHA256.HashData(SecuritySignedEncoding.Unsigned(RequestDomain, request)));
    }

    public static GovernanceWitnessState VerifyResponse(FormalRotationPrepareRequest request, byte[] response, GovernanceWitnessTrust trust)
    {
        var requestHash = RequestHash(request);
        var wire = IndependentEvidenceWire.Read<SignedFormalRotationPrepareResponse>(response, GovernanceJournalProtocol.MaxWireBytes, Unavailable);
        var payload = wire.Payload;
        if (trust is null || payload is null || payload.RequestHash != requestHash || payload.SourceId != trust.SourceId ||
            payload.KeyId != trust.KeyId || !SecuritySignedEncoding.KeyId(payload.SourceId) || !SecuritySignedEncoding.KeyId(payload.KeyId)) throw Invalid();
        IndependentEvidenceWire.Verify(ResponseDomain, payload, wire.Signature, trust.PublicKey, Unavailable);

        var expectedState = new GovernanceWitnessState(request.Intent.Previous, request.Intent, null);
        if (!payload.Durable || payload.EnvironmentId != request.EnvironmentId || payload.ScheduleId != request.ScheduleId ||
            !payload.HardDeadline.EqualsExact(request.HardDeadline) || !payload.AllowLeaseCutoff.EqualsExact(request.HardDeadline - MaximumLeaseClockSkew) ||
            payload.State != expectedState) throw Invalid();

        if (request.Action == Revoke)
        {
            if (payload.TrustedTimeLower is not DateTimeOffset due || !ValidUtc(due) || due < request.HardDeadline) throw Invalid();
        }
        else if (payload.TrustedTimeLower is not null) throw Invalid();

        return payload.State;
    }

    private static void ValidateRequest(FormalRotationPrepareRequest request)
    {
        if (request is null || request.Intent is null || request.EnvironmentId == Guid.Empty || !SecuritySignedEncoding.HexHash(request.Nonce) ||
            !ValidUtc(request.HardDeadline) || request.HardDeadline.Ticks <= MaximumLeaseClockSkew.Ticks) throw Invalid();
        if (request.Action != Schedule && request.Action != Revoke || request.Action == Schedule && request.ScheduleId is not null ||
            request.Action == Revoke && (request.ScheduleId is null || request.ScheduleId == Guid.Empty || request.ScheduleId == request.Intent.OperationId))
            throw Invalid();
        // Keep the existing journal bounds and canonical recovery representation.
        _ = GovernanceJournalProtocol.NextPosition(request.Journal, request.Intent);
    }

    private static bool ValidUtc(DateTimeOffset instant) => instant.Offset == TimeSpan.Zero &&
        instant > DateTimeOffset.MinValue && instant < DateTimeOffset.MaxValue;

    private static string Nonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static InvalidDataException Invalid() => new(Unavailable);
}

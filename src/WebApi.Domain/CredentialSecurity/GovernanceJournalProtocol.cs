using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;

namespace WebApi.Domain.CredentialSecurity;

// The authority and generation are pinned in the deployment independently of
// the database. One journal serializes writes that can span environments.
public sealed record GovernanceJournalId(Guid AuthorityId, Guid Generation);
public sealed record GovernanceJournalPosition(long Sequence, string Hash);

// RecoveryMaterial is canonical unpadded base64url of the bounded, secret-free mutation
// plan. Its schema, full scope and effects are validated by the commit store.
public sealed record GovernanceWriteIntent(Guid OperationId, GovernanceJournalPosition Previous, string RecoveryMaterial);
public sealed record GovernanceOperationConfirmation(Guid OperationId, GovernanceJournalPosition Position);
public sealed record GovernanceWitnessState(GovernanceJournalPosition Confirmed, GovernanceWriteIntent? Pending,
    GovernanceOperationConfirmation? OperationConfirmation);
public sealed record GovernanceWitnessRequest(GovernanceJournalId Journal, string Action, string Nonce, Guid? OperationId,
    GovernanceWriteIntent? Intent);
public sealed record GovernanceWitnessPayload(string RequestHash, string SourceId, string KeyId, GovernanceWitnessState State);
public sealed record SignedGovernanceWitnessResponse([property: JsonRequired] GovernanceWitnessPayload Payload,
    [property: JsonRequired] string Signature);
public sealed record GovernanceWitnessTrust(string SourceId, string KeyId, string PublicKey);

public static class GovernanceJournalProtocol
{
    public const string RequestDomain = "webapi.credential-governance.journal-request.v1";
    public const string ResponseDomain = "webapi.credential-governance.journal-response.v1";
    public const string PositionDomain = "webapi.credential-governance.journal-position.v1";
    public const int MaxRecoveryBytes = 256 * 1024;
    public const int MaxWireBytes = 512 * 1024;
    private const string Unavailable = "credential_governance_witness_unavailable";

    public static GovernanceJournalPosition Genesis { get; } = new(0, Hash("webapi.credential-governance.journal-genesis.v1"u8));

    public static GovernanceWitnessRequest Observe(GovernanceJournalId journal, Guid? operationId) =>
        new(journal, "observe", Nonce(), operationId, null);

    public static GovernanceWitnessRequest Prepare(GovernanceJournalId journal, GovernanceWriteIntent intent) =>
        new(journal, "prepare", Nonce(), intent.OperationId, intent);

    public static GovernanceWitnessRequest Confirm(GovernanceJournalId journal, GovernanceWriteIntent intent) =>
        new(journal, "confirm", Nonce(), intent.OperationId, intent);

    public static string RequestHash(GovernanceWitnessRequest request)
    {
        ValidateRequest(request);
        return Hash(SecuritySignedEncoding.Unsigned(RequestDomain, request));
    }

    public static byte[] EncodeRequest(GovernanceWitnessRequest request)
    {
        ValidateRequest(request);
        return CanonicalJson.Serialize(request);
    }

    public static GovernanceJournalPosition NextPosition(GovernanceJournalId journal, GovernanceWriteIntent intent)
    {
        RequireJournal(journal);
        if (!ValidIntent(intent)) throw Invalid();
        return new(intent.Previous.Sequence + 1, Hash(SecuritySignedEncoding.Unsigned(PositionDomain, new { journal, intent })));
    }

    public static GovernanceWitnessState VerifyResponse(GovernanceWitnessRequest request, byte[] response, GovernanceWitnessTrust trust)
    {
        var requestHash = RequestHash(request);
        var wire = IndependentEvidenceWire.Read<SignedGovernanceWitnessResponse>(response, MaxWireBytes, Unavailable);
        var payload = wire.Payload;
        if (trust is null || payload is null || payload.RequestHash != requestHash || payload.SourceId != trust.SourceId ||
            payload.KeyId != trust.KeyId || !SecuritySignedEncoding.KeyId(payload.SourceId) || !SecuritySignedEncoding.KeyId(payload.KeyId)) throw Invalid();
        IndependentEvidenceWire.Verify(ResponseDomain, payload, wire.Signature, trust.PublicKey, Unavailable);

        var state = payload.State;
        if (state is null || !ValidPosition(state.Confirmed)) throw Invalid();
        if (state.Pending is { } pending && (!ValidIntent(pending) || pending.Previous != state.Confirmed)) throw Invalid();
        if (state.OperationConfirmation is { } confirmation &&
            (confirmation.OperationId == Guid.Empty || confirmation.OperationId != request.OperationId ||
             confirmation.OperationId == state.Pending?.OperationId || !ValidPosition(confirmation.Position) ||
             confirmation.Position.Sequence == 0 || confirmation.Position.Sequence > state.Confirmed.Sequence ||
             confirmation.Position.Sequence == state.Confirmed.Sequence && confirmation.Position != state.Confirmed)) throw Invalid();

        if (request.Action == "prepare" && (state.Pending != request.Intent || state.OperationConfirmation is not null)) throw Invalid();
        if (request.Action == "confirm" && (state.Pending is not null || state.Confirmed != NextPosition(request.Journal, request.Intent!) ||
            state.OperationConfirmation != new GovernanceOperationConfirmation(request.OperationId!.Value, state.Confirmed))) throw Invalid();
        return state;
    }

    public static void RequireJournal(GovernanceJournalId journal)
    {
        if (journal is null || journal.AuthorityId == Guid.Empty || journal.Generation == Guid.Empty) throw Invalid();
    }

    public static bool ValidPosition(GovernanceJournalPosition? position) => position is not null && position.Sequence >= 0 &&
        SecuritySignedEncoding.HexHash(position.Hash) && (position.Sequence == 0 ? position == Genesis : position.Hash != Genesis.Hash);

    private static void ValidateRequest(GovernanceWitnessRequest request)
    {
        RequireJournal(request.Journal);
        if (!SecuritySignedEncoding.HexHash(request.Nonce) || request.OperationId == Guid.Empty) throw Invalid();
        if (request.Action == "observe" && request.Intent is null) return;
        if (request.Action is not ("prepare" or "confirm") || request.Intent is null || request.Intent.OperationId != request.OperationId ||
            !ValidIntent(request.Intent)) throw Invalid();
    }

    private static bool ValidIntent(GovernanceWriteIntent? intent)
    {
        if (intent is null || intent.OperationId == Guid.Empty || !ValidPosition(intent.Previous) || intent.Previous.Sequence == long.MaxValue ||
            intent.RecoveryMaterial is not { Length: >= 2 and <= 4 * ((MaxRecoveryBytes + 2) / 3) }) return false;
        try
        {
            var decoded = Base64Url.DecodeFromChars(intent.RecoveryMaterial);
            return decoded.Length is > 0 and <= MaxRecoveryBytes && Base64Url.EncodeToString(decoded) == intent.RecoveryMaterial;
        }
        catch (FormatException) { return false; }
    }

    private static string Nonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static InvalidDataException Invalid() => new(Unavailable);
}

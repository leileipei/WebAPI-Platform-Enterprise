using System.Security.Cryptography;
using System.Text.Json.Serialization;
using WebApi.Contracts.Common;

namespace WebApi.Domain.CredentialSecurity;

public sealed record GovernanceEnvironmentOrigin(Guid EnvironmentId, Guid ProjectId, Guid OrganizationId, string State,
    string? EvidenceHash, Guid? FreshProofId, Guid? ConfirmedBy, DateTimeOffset? ConfirmedAt, string? ConfirmationReason, long Revision);

public sealed record GovernanceEnvironmentHistoryCheckpoint(Guid OrganizationId, Guid ProjectId, Guid SecurityGeneration,
    long Epoch, string HeadHash, string InitializationHash, bool RequireEnvironmentCredentials, string MinimumSnapshotSchema,
    bool? InitialRequireEnvironmentCredentials);

public sealed record GovernanceEnvironmentHistory(GovernanceJournalId Journal, GovernanceJournalPosition Position,
    Guid EnvironmentId, string State, GovernanceEnvironmentHistoryCheckpoint? Checkpoint);

public sealed record GovernanceEnvironmentHistoryRequest(GovernanceJournalId Journal, GovernanceJournalPosition Position,
    Guid EnvironmentId, string Nonce);

public sealed record GovernanceEnvironmentHistoryPayload(string RequestHash, string SourceId, string KeyId, GovernanceEnvironmentHistory History);

public sealed record SignedGovernanceEnvironmentHistoryResponse([property: JsonRequired] GovernanceEnvironmentHistoryPayload Payload,
    [property: JsonRequired] string Signature);

public static class GovernanceEnvironmentHistoryProtocol
{
    public const string RequestDomain = "webapi.credential-governance.environment-history-request.v1";
    public const string ResponseDomain = "webapi.credential-governance.environment-history-response.v1";
    public const int MaxWireBytes = 32 * 1024;
    private const string Unavailable = "credential_governance_environment_history_unavailable";

    public static GovernanceEnvironmentHistoryRequest Observe(GovernanceJournalId journal, GovernanceJournalPosition position,
        Guid environmentId) => new(journal, position, environmentId, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    public static string InitializationHash(GovernanceEnvironmentOrigin origin) =>
        Hash(SecuritySignedEncoding.Unsigned("webapi.credential-governance.environment-origin.v1", origin));

    public static string RequestHash(GovernanceEnvironmentHistoryRequest request)
    {
        RequireRequest(request);
        return Hash(SecuritySignedEncoding.Unsigned(RequestDomain, request));
    }

    public static byte[] EncodeRequest(GovernanceEnvironmentHistoryRequest request)
    {
        RequireRequest(request);
        return CanonicalJson.Serialize(request);
    }

    public static GovernanceEnvironmentHistory VerifyResponse(GovernanceEnvironmentHistoryRequest request, byte[] wire, GovernanceWitnessTrust trust)
    {
        var requestHash = RequestHash(request);
        var response = IndependentEvidenceWire.Read<SignedGovernanceEnvironmentHistoryResponse>(wire, MaxWireBytes, Unavailable);
        var payload = response.Payload;
        if (trust is null || payload is null || payload.RequestHash != requestHash || payload.SourceId != trust.SourceId ||
            payload.KeyId != trust.KeyId || !SecuritySignedEncoding.KeyId(payload.SourceId) || !SecuritySignedEncoding.KeyId(payload.KeyId))
            throw Invalid();
        IndependentEvidenceWire.Verify(ResponseDomain, payload, response.Signature, trust.PublicKey, Unavailable);

        var history = payload.History;
        if (history is null || history.Journal != request.Journal || history.Position != request.Position ||
            history.EnvironmentId != request.EnvironmentId || history.State is not ("Confirmed" or "Pending" or "Unknown") ||
            (history.State == "Confirmed" ? !ValidCheckpoint(history.Checkpoint) : history.Checkpoint is not null)) throw Invalid();
        return history;
    }

    private static bool ValidCheckpoint(GovernanceEnvironmentHistoryCheckpoint? checkpoint) => checkpoint is not null &&
        checkpoint.OrganizationId != Guid.Empty && checkpoint.ProjectId != Guid.Empty && checkpoint.SecurityGeneration != Guid.Empty &&
        checkpoint.Epoch >= 0 && SecuritySignedEncoding.HexHash(checkpoint.HeadHash) &&
        SecuritySignedEncoding.HexHash(checkpoint.InitializationHash) && checkpoint.MinimumSnapshotSchema is "2.0" or "2.1" or "2.2" or "2.3" or "2.4";

    private static void RequireRequest(GovernanceEnvironmentHistoryRequest request)
    {
        GovernanceJournalProtocol.RequireJournal(request.Journal);
        if (!GovernanceJournalProtocol.ValidPosition(request.Position) || request.EnvironmentId == Guid.Empty ||
            !SecuritySignedEncoding.HexHash(request.Nonce)) throw Invalid();
    }

    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static InvalidDataException Invalid() => new(Unavailable);
}

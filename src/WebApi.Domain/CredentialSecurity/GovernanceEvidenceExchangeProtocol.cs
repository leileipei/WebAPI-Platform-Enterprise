using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace WebApi.Domain.CredentialSecurity;

public sealed record GovernanceEvidenceExchangeRequest<T>(GovernanceJournalId Journal, string Nonce, T Scope);

public sealed record GovernanceEvidenceExchangePayload<T>(string RequestHash, string SourceId, string KeyId, T Result);

public sealed record SignedGovernanceEvidenceExchangeResponse<T>([property: JsonRequired] GovernanceEvidenceExchangePayload<T> Payload,
    [property: JsonRequired] string Signature);

public static class GovernanceEvidenceExchangeProtocol
{
    public const int MaxWireBytes = 512 * 1024;
    private const string Unavailable = "credential_governance_independent_proof_unavailable";

    public static GovernanceEvidenceExchangeRequest<T> Create<T>(GovernanceJournalId journal, T scope)
    {
        GovernanceJournalProtocol.RequireJournal(journal);
        return new(journal, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), scope);
    }

    public static string RequestHash<T>(string protocol, GovernanceEvidenceExchangeRequest<T> request)
    {
        GovernanceJournalProtocol.RequireJournal(request.Journal);
        if (!SecuritySignedEncoding.HexHash(request.Nonce) || request.Scope is null) throw Invalid();
        return Convert.ToHexStringLower(SHA256.HashData(SecuritySignedEncoding.Unsigned(
            $"webapi.credential-governance.{protocol}-request.v1", request)));
    }

    public static string ResponseDomain(string protocol) => $"webapi.credential-governance.{protocol}-response.v1";

    public static TResult Verify<TScope, TResult>(string protocol, GovernanceEvidenceExchangeRequest<TScope> request,
        byte[] wire, GovernanceWitnessTrust trust)
    {
        var response = IndependentEvidenceWire.Read<SignedGovernanceEvidenceExchangeResponse<TResult>>(wire, MaxWireBytes, Unavailable);
        var payload = response.Payload;
        if (payload is null || payload.RequestHash != RequestHash(protocol, request) || payload.SourceId != trust.SourceId ||
            payload.KeyId != trust.KeyId || !SecuritySignedEncoding.KeyId(payload.SourceId) || !SecuritySignedEncoding.KeyId(payload.KeyId) ||
            payload.Result is null) throw Invalid();
        IndependentEvidenceWire.Verify(ResponseDomain(protocol), payload, response.Signature, trust.PublicKey, Unavailable);
        return payload.Result;
    }

    private static InvalidDataException Invalid() => new(Unavailable);
}

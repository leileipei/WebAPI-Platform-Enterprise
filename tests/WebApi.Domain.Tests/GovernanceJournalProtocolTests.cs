using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class GovernanceJournalProtocolTests
{
    [Fact]
    public void IndependentlyEncodedOpenSslVectorVerifies()
    {
        // Produced with Python's sorted JSON encoder and OpenSSL P-256/SHA-256.
        // The fixture retains only the public key, wire bytes and expected hash.
        const string requestWire = """
            {"action":"prepare","intent":{"operationId":"30000000-0000-0000-0000-000000000001","previous":{"hash":"807377463ffba18bf346dfe5008f82e29baa5af00a1e26688e0f5c6e29768b0e","sequence":0},"recoveryMaterial":"cmV2b2tlIGZpeHR1cmU"},"journal":{"authorityId":"10000000-0000-0000-0000-000000000001","generation":"20000000-0000-0000-0000-000000000001"},"nonce":"abababababababababababababababababababababababababababababababab","operationId":"30000000-0000-0000-0000-000000000001"}
            """;
        const string responseWire = """
            {"payload":{"keyId":"fixture-public","requestHash":"bce89ca94b58f7f1e10a335bd9c4009768a0717d993faa4aa0b7a8c822e93076","sourceId":"separate-implementation","state":{"confirmed":{"hash":"807377463ffba18bf346dfe5008f82e29baa5af00a1e26688e0f5c6e29768b0e","sequence":0},"operationConfirmation":null,"pending":{"operationId":"30000000-0000-0000-0000-000000000001","previous":{"hash":"807377463ffba18bf346dfe5008f82e29baa5af00a1e26688e0f5c6e29768b0e","sequence":0},"recoveryMaterial":"cmV2b2tlIGZpeHR1cmU"}}},"signature":"5KwMyAe0WM9D1Nvb7bjl5xrGC/hYyq\u002BaJGhldCLbgGWM02BwGxpkPOwDhGCxDQI9BEQqoa9VtXpsp5SV8sO4gA=="}
            """;
        const string publicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEPNQTrKH+XZJ3/aoguegd3MQlB+S6Yu/uL5YapvBVsc4K7zKHV3fkLaBesIhh+X1mooWm7fIbiEjXfuUOqsyKIg==";
        var request = JsonSerializer.Deserialize<GovernanceWitnessRequest>(requestWire, CanonicalJson.Options)!;
        Assert.Equal(requestWire, Encoding.UTF8.GetString(GovernanceJournalProtocol.EncodeRequest(request)));
        var observed = GovernanceJournalProtocol.VerifyResponse(request, Encoding.UTF8.GetBytes(responseWire),
            new("separate-implementation", "fixture-public", publicKey));
        Assert.Equal(request.Intent, observed.Pending);
        Assert.Equal(new(1, "52af5752ff3700d9cb2f55ab9e068f0acd29226669c4781d5c89219ccf671a9c"),
            GovernanceJournalProtocol.NextPosition(request.Journal, observed.Pending!));
    }

    [Fact]
    public void ValidSignedObservationBindsTheOperationAndExactJournalRequest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request();
        var state = new GovernanceWitnessState(GovernanceJournalProtocol.Genesis, request.Intent, null);
        var payload = new GovernanceWitnessPayload(GovernanceJournalProtocol.RequestHash(request), "history", "key-1", state);
        Assert.Equal(state, GovernanceJournalProtocol.VerifyResponse(request, Signed(key, payload), Trust(key)));
        Assert.NotEqual(GovernanceJournalProtocol.RequestHash(request), GovernanceJournalProtocol.RequestHash(Request()));
        var next = GovernanceJournalProtocol.NextPosition(request.Journal, request.Intent!);
        Assert.Equal(1, next.Sequence);
        Assert.NotEqual(next, GovernanceJournalProtocol.NextPosition(request.Journal,
            request.Intent! with { RecoveryMaterial = Base64Url.EncodeToString("changed"u8) }));
        Assert.NotEqual(next, GovernanceJournalProtocol.NextPosition(request.Journal with { Generation = Guid.NewGuid() }, request.Intent!));
    }

    [Theory]
    [InlineData("other-source")]
    [InlineData("wrong-key")]
    [InlineData("changed-intent")]
    [InlineData("stale-nonce")]
    [InlineData("unknown-member")]
    [InlineData("wrong-position")]
    [InlineData("operation-confirmation-not-requested")]
    public void UntrustedOrInconsistentWitnessResponsesAreRejected(string fault)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request();
        var state = new GovernanceWitnessState(GovernanceJournalProtocol.Genesis, request.Intent, null);
        var payload = new GovernanceWitnessPayload(GovernanceJournalProtocol.RequestHash(request), "history", "key-1", state);
        switch (fault)
        {
            case "other-source": payload = payload with { SourceId = "other" }; break;
            case "changed-intent": payload = payload with { State = state with { Pending = state.Pending! with { OperationId = Guid.NewGuid() } } }; break;
            case "stale-nonce": request = request with { Nonce = new string('0', 64) }; break;
            case "wrong-position": payload = payload with { State = state with { Confirmed = new(1, new string('b', 64)) } }; break;
            case "operation-confirmation-not-requested":
                payload = payload with { State = new(new(1, new string('b', 64)), null, new(Guid.NewGuid(), new(1, new string('b', 64)))) };
                break;
        }
        var wire = Signed(fault == "wrong-key" ? other : key, payload);
        if (fault == "unknown-member") wire = [.. "{\"unexpected\":true,"u8.ToArray(), .. wire.Skip(1)];
        Assert.Throws<InvalidDataException>(() => GovernanceJournalProtocol.VerifyResponse(request, wire, Trust(key)));
    }

    [Fact]
    public void PrepareRejectsUnboundedOrNoncanonicalRecoveryMaterial()
    {
        var request = Request();
        Assert.Throws<InvalidDataException>(() => GovernanceJournalProtocol.NextPosition(request.Journal,
            request.Intent! with { RecoveryMaterial = " YQ==" }));
        Assert.Throws<InvalidDataException>(() => GovernanceJournalProtocol.NextPosition(request.Journal,
            request.Intent! with { RecoveryMaterial = Base64Url.EncodeToString(new byte[256 * 1024 + 1]) }));
    }

    [Fact]
    public void MaximumRecoveryMaterialFitsBothRequestAndSignedPendingResponse()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request();
        var recovery = new byte[GovernanceJournalProtocol.MaxRecoveryBytes];
        Array.Fill(recovery, (byte)0xfb);
        request = request with { Intent = request.Intent! with { RecoveryMaterial = Base64Url.EncodeToString(recovery) } };
        var state = new GovernanceWitnessState(GovernanceJournalProtocol.Genesis, request.Intent, null);
        var wire = Signed(key, new(GovernanceJournalProtocol.RequestHash(request), "history", "key-1", state));
        Assert.True(GovernanceJournalProtocol.EncodeRequest(request).Length <= GovernanceJournalProtocol.MaxWireBytes);
        Assert.Equal(state, GovernanceJournalProtocol.VerifyResponse(request, wire, Trust(key)));
    }

    private static GovernanceWitnessRequest Request()
    {
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var intent = new GovernanceWriteIntent(Guid.NewGuid(), GovernanceJournalProtocol.Genesis, Base64Url.EncodeToString("command"u8));
        return GovernanceJournalProtocol.Prepare(journal, intent);
    }

    private static GovernanceWitnessTrust Trust(ECDsa key) => new("history", "key-1", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

    private static byte[] Signed(ECDsa key, GovernanceWitnessPayload payload) => CanonicalJson.Serialize(new SignedGovernanceWitnessResponse(payload,
        Convert.ToBase64String(key.SignData(SecuritySignedEncoding.Unsigned(GovernanceJournalProtocol.ResponseDomain, payload),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
}

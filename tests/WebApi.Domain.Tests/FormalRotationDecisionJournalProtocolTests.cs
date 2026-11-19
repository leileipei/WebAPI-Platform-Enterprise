using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class FormalRotationDecisionJournalProtocolTests
{
    private const string RequestDomain = "webapi.credential-governance.formal-rotation-decision-request.v1";
    private const string ResponseDomain = "webapi.credential-governance.formal-rotation-decision-response.v1";
    private static readonly DateTimeOffset Deadline = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Complete")]
    [InlineData("AbortRequested")]
    [InlineData("AbortConfirmed")]
    public void AcceptsTheExactDurablePendingDecisionStrictlyBeforeTheDeadline(string decision)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateRequest(decision);
        var expected = new GovernanceWitnessState(request.Intent.Previous, request.Intent, null);
        var response = Sign(key, Reply(request));

        Assert.Equal(expected, FormalRotationDecisionJournalProtocol.VerifyResponse(request, response, Trust(key)));
    }

    [Theory]
    [InlineData("Complete")]
    [InlineData("AbortRequested")]
    [InlineData("AbortConfirmed")]
    public void RejectsATrustedDecisionAtTheExactDeadline(string decision)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateRequest(decision);
        var response = Sign(key, Reply(request) with { TrustedTimeUpper = Deadline });

        Assert.Throws<InvalidDataException>(() => FormalRotationDecisionJournalProtocol.VerifyResponse(request, response, Trust(key)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryRequiresFreshSignatureOfTheExactPastDecision(bool confirmed)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var prepared = CreateRequest("AbortConfirmed");
        var request = FormalRotationDecisionJournalProtocol.Observe(prepared.Journal, prepared.Intent, prepared.ScheduleId,
            prepared.EnvironmentId, prepared.HardDeadline, prepared.Decision);
        Assert.Equal("observeFormalRotationDecision", request.Action);
        Assert.NotEqual(prepared.Nonce, request.Nonce);
        var next = GovernanceJournalProtocol.NextPosition(request.Journal, request.Intent);
        var expected = confirmed ? new GovernanceWitnessState(next, null, new(request.Intent.OperationId, next))
            : new(request.Intent.Previous, request.Intent, null);
        var reply = Reply(request) with { State = expected };
        Assert.Equal(expected, FormalRotationDecisionJournalProtocol.VerifyResponse(request, Sign(key, reply), Trust(key)));
        Assert.Throws<InvalidDataException>(() => FormalRotationDecisionJournalProtocol.VerifyResponse(request, Sign(key, Reply(prepared)), Trust(key)));
        Assert.Throws<InvalidDataException>(() => FormalRotationDecisionJournalProtocol.VerifyResponse(request,
            Sign(key, reply with { TrustedTimeUpper = request.HardDeadline }), Trust(key)));
        Assert.Throws<InvalidDataException>(() => FormalRotationDecisionJournalProtocol.VerifyResponse(request,
            Sign(key, reply with { State = expected with { Pending = request.Intent with { OperationId = Guid.NewGuid() } } }), Trust(key)));
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("generation")]
    [InlineData("nonce")]
    [InlineData("changed-request-decision")]
    [InlineData("recovery-material")]
    [InlineData("response-decision")]
    [InlineData("environment")]
    [InlineData("schedule")]
    [InlineData("deadline")]
    [InlineData("deadline-offset")]
    [InlineData("cutoff")]
    [InlineData("cutoff-offset")]
    [InlineData("late-time")]
    [InlineData("default-time")]
    [InlineData("non-utc-time")]
    [InlineData("not-durable")]
    [InlineData("confirmed-position")]
    [InlineData("no-pending")]
    [InlineData("other-pending")]
    [InlineData("already-confirmed")]
    [InlineData("source")]
    [InlineData("key-id")]
    [InlineData("wrong-key")]
    [InlineData("ordinary-signature-domain")]
    [InlineData("old-formal-signature-domain")]
    public void RejectsReceiptsThatDoNotProveTheExactPreDeadlinePendingDecision(string fault)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateRequest("AbortConfirmed");
        var reply = Reply(request);
        var domain = ResponseDomain;
        switch (fault)
        {
            case "authority": request = request with { Journal = request.Journal with { AuthorityId = Guid.NewGuid() } }; break;
            case "generation": request = request with { Journal = request.Journal with { Generation = Guid.NewGuid() } }; break;
            case "nonce": request = request with { Nonce = new string('a', 64) }; break;
            case "changed-request-decision": request = request with { Decision = "Complete" }; reply = reply with { Decision = "Complete" }; break;
            case "recovery-material": request = request with { Intent = request.Intent with { RecoveryMaterial = "Yg" } }; break;
            case "response-decision": reply = reply with { Decision = "AbortRequested" }; break;
            case "environment": reply = reply with { EnvironmentId = Guid.NewGuid() }; break;
            case "schedule": reply = reply with { ScheduleId = Guid.NewGuid() }; break;
            case "deadline": reply = reply with { HardDeadline = Deadline.AddTicks(1) }; break;
            case "deadline-offset": reply = reply with { HardDeadline = Deadline.ToOffset(TimeSpan.FromHours(1)) }; break;
            case "cutoff": reply = reply with { AllowLeaseCutoff = reply.AllowLeaseCutoff.AddTicks(-1) }; break;
            case "cutoff-offset": reply = reply with { AllowLeaseCutoff = reply.AllowLeaseCutoff.ToOffset(TimeSpan.FromHours(1)) }; break;
            case "late-time": reply = reply with { TrustedTimeUpper = Deadline.AddTicks(1) }; break;
            case "default-time": reply = reply with { TrustedTimeUpper = default }; break;
            case "non-utc-time": reply = reply with { TrustedTimeUpper = reply.TrustedTimeUpper.ToOffset(TimeSpan.FromHours(1)) }; break;
            case "not-durable": reply = reply with { Durable = false }; break;
            case "confirmed-position": reply = reply with { State = reply.State with { Confirmed = GovernanceJournalProtocol.Genesis } }; break;
            case "no-pending": reply = reply with { State = reply.State with { Pending = null } }; break;
            case "other-pending": reply = reply with { State = reply.State with { Pending = request.Intent with { OperationId = Guid.NewGuid() } } }; break;
            case "already-confirmed":
                var position = GovernanceJournalProtocol.NextPosition(request.Journal, request.Intent);
                reply = reply with { State = new(position, null, new(request.Intent.OperationId, position)) };
                break;
            case "source": reply = reply with { SourceId = "other-source" }; break;
            case "key-id": reply = reply with { KeyId = "other-key" }; break;
            case "wrong-key": break;
            case "ordinary-signature-domain": domain = GovernanceJournalProtocol.ResponseDomain; break;
            case "old-formal-signature-domain": domain = FormalRotationJournalProtocol.ResponseDomain; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }

        var response = Sign(fault == "wrong-key" ? other : key, reply, domain);
        Assert.Throws<InvalidDataException>(() => FormalRotationDecisionJournalProtocol.VerifyResponse(request, response, Trust(key)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("complete")]
    [InlineData("AbortRequested ")]
    [InlineData("Abort")]
    [InlineData("DeadlineRevocation")]
    public void RejectsUnknownOrNoncanonicalDecisionNames(string decision)
    {
        Assert.Throws<InvalidDataException>(() => FormalRotationDecisionJournalProtocol.EncodeRequest(CreateRequest(decision)));
    }

    [Theory]
    [InlineData("wrong-action")]
    [InlineData("bad-nonce")]
    [InlineData("empty-schedule")]
    [InlineData("empty-environment")]
    [InlineData("reused-operation")]
    [InlineData("non-utc-deadline")]
    [InlineData("deadline-underflow")]
    public void RejectsInvalidDecisionRequests(string fault)
    {
        var request = CreateRequest("Complete");
        var invalid = fault switch
        {
            "wrong-action" => request with { Action = "prepareFormalRotationRevocation" },
            "bad-nonce" => request with { Nonce = "short" },
            "empty-schedule" => request with { ScheduleId = Guid.Empty },
            "empty-environment" => request with { EnvironmentId = Guid.Empty },
            "reused-operation" => request with { ScheduleId = request.Intent.OperationId },
            "non-utc-deadline" => request with { HardDeadline = Deadline.ToOffset(TimeSpan.FromHours(1)) },
            "deadline-underflow" => request with { HardDeadline = DateTimeOffset.MinValue.AddSeconds(2) },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };

        Assert.Throws<InvalidDataException>(() => FormalRotationDecisionJournalProtocol.EncodeRequest(invalid));
    }

    [Fact]
    public async Task AProviderWithoutTheDecisionProtocolFailsClosed()
    {
        IFormalRotationJournalWitness witness = new LegacyWitness();
        var request = CreateRequest("AbortConfirmed");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => witness.PrepareFormalRotationDecisionAsync(request.Journal,
            request.Intent, request.ScheduleId, request.EnvironmentId, request.HardDeadline, request.Decision, default));

        Assert.Equal("credential_governance_witness_unavailable", error.Message);
    }

    private static FormalRotationDecisionPrepareRequest CreateRequest(string decision) => FormalRotationDecisionJournalProtocol.Prepare(
        new(Guid.NewGuid(), Guid.NewGuid()), new(Guid.NewGuid(), new(4, new string('b', 64)), "YQ"), Guid.NewGuid(), Guid.NewGuid(),
        Deadline, decision);

    private static FormalRotationDecisionPreparePayload Reply(FormalRotationDecisionPrepareRequest request) => new(
        Convert.ToHexStringLower(SHA256.HashData(SecuritySignedEncoding.Unsigned(RequestDomain, request))), "history", "rotation-key",
        request.ScheduleId, request.EnvironmentId, request.HardDeadline, request.Decision, request.HardDeadline.AddSeconds(-2),
        request.HardDeadline.AddTicks(-1), true, new(request.Intent.Previous, request.Intent, null));

    private static GovernanceWitnessTrust Trust(ECDsa key) => new("history", "rotation-key",
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

    private static byte[] Sign(ECDsa key, FormalRotationDecisionPreparePayload payload, string domain = ResponseDomain)
    {
        var signature = Convert.ToBase64String(key.SignData(SecuritySignedEncoding.Unsigned(domain, payload),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        return CanonicalJson.Serialize(new SignedFormalRotationDecisionPrepareResponse(payload, signature));
    }

    private sealed class LegacyWitness : IFormalRotationJournalWitness
    {
        public Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId journal, Guid? operationId, CancellationToken ct) =>
            throw new InvalidOperationException();

        public Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new InvalidOperationException();

        public Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct) =>
            throw new InvalidOperationException();

        public Task<GovernanceWitnessState> PrepareFormalRotationAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
            Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct) => throw new InvalidOperationException();

        public Task<GovernanceWitnessState> PrepareFormalRotationRevocationAsync(GovernanceJournalId journal, GovernanceWriteIntent intent,
            Guid scheduleId, Guid environmentId, DateTimeOffset hardDeadline, CancellationToken ct) => throw new InvalidOperationException();
    }
}

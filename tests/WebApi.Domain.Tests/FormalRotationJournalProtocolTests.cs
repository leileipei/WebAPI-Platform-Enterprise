using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class FormalRotationJournalProtocolTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothFormalActionsRequireTheExactDurablePendingIntent(bool revocation)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateRequest(revocation);
        Assert.Equal(revocation ? "prepareFormalRotationRevocation" : "prepareFormalRotation", request.Action);
        var reply = Reply(request);
        var verified = FormalRotationJournalProtocol.VerifyResponse(request, Signed(key, reply), Trust(key));
        Assert.Equal(new GovernanceWitnessState(request.Intent.Previous, request.Intent, null), verified);
    }

    [Theory]
    [InlineData("other-authority")]
    [InlineData("other-generation")]
    [InlineData("other-nonce")]
    [InlineData("other-action")]
    [InlineData("other-environment")]
    [InlineData("other-deadline")]
    [InlineData("other-schedule")]
    [InlineData("other-intent")]
    [InlineData("other-position")]
    [InlineData("other-pending")]
    [InlineData("cutoff-too-late")]
    [InlineData("non-utc-cutoff")]
    [InlineData("not-durable")]
    [InlineData("ordinary-signature-domain")]
    [InlineData("wrong-key")]
    [InlineData("due-time-missing")]
    [InlineData("due-time-too-early")]
    [InlineData("due-time-non-utc")]
    public void RejectsSubstitutionAndUnverifiedDueTime(string fault)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateRequest(revocation: true);
        var reply = Reply(request);
        var domain = FormalRotationJournalProtocol.ResponseDomain;
        switch (fault)
        {
            case "other-authority": request = request with { Journal = request.Journal with { AuthorityId = Guid.NewGuid() } }; break;
            case "other-generation": request = request with { Journal = request.Journal with { Generation = Guid.NewGuid() } }; break;
            case "other-nonce": request = request with { Nonce = new string('a', 64) }; break;
            case "other-action": request = request with { Action = "prepareFormalRotation", ScheduleId = null }; break;
            case "other-environment": reply = reply with { EnvironmentId = Guid.NewGuid() }; break;
            case "other-deadline": reply = reply with { HardDeadline = Deadline.AddSeconds(1) }; break;
            case "other-schedule": reply = reply with { ScheduleId = Guid.NewGuid() }; break;
            case "other-intent": request = request with { Intent = request.Intent with { RecoveryMaterial = "Yg" } }; break;
            case "other-position": reply = reply with { State = reply.State with { Confirmed = new(1, new string('b', 64)) } }; break;
            case "other-pending": reply = reply with { State = reply.State with { Pending = request.Intent with { OperationId = Guid.NewGuid() } } }; break;
            case "cutoff-too-late": reply = reply with { AllowLeaseCutoff = reply.AllowLeaseCutoff.AddTicks(1) }; break;
            case "non-utc-cutoff": reply = reply with { AllowLeaseCutoff = reply.AllowLeaseCutoff.ToOffset(TimeSpan.FromHours(1)) }; break;
            case "not-durable": reply = reply with { Durable = false }; break;
            case "ordinary-signature-domain": domain = GovernanceJournalProtocol.ResponseDomain; break;
            case "due-time-missing": reply = reply with { TrustedTimeLower = null }; break;
            case "due-time-too-early": reply = reply with { TrustedTimeLower = Deadline.AddTicks(-1) }; break;
            case "due-time-non-utc": reply = reply with { TrustedTimeLower = Deadline.ToOffset(TimeSpan.FromHours(1)) }; break;
        }

        Assert.Throws<InvalidDataException>(() => FormalRotationJournalProtocol.VerifyResponse(request,
            Signed(fault == "wrong-key" ? other : key, reply, domain), Trust(key)));
    }

    [Fact]
    public void AnOtherwiseMatchingOrdinaryPrepareReceiptCannotQualifyAFormalPrepare()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = CreateRequest();
        var ordinary = GovernanceJournalProtocol.Prepare(request.Journal, request.Intent) with { Nonce = request.Nonce };
        var payload = new GovernanceWitnessPayload(GovernanceJournalProtocol.RequestHash(ordinary), "history", "rotation-key", Reply(request).State);
        var receipt = CanonicalJson.Serialize(new SignedGovernanceWitnessResponse(payload, Sign(key, GovernanceJournalProtocol.ResponseDomain, payload)));
        Assert.Equal(payload.State, GovernanceJournalProtocol.VerifyResponse(ordinary, receipt, Trust(key)));
        Assert.Throws<InvalidDataException>(() => FormalRotationJournalProtocol.VerifyResponse(request, receipt, Trust(key)));
    }

    [Theory]
    [InlineData("empty-schedule")]
    [InlineData("empty-environment")]
    [InlineData("non-utc-deadline")]
    [InlineData("deadline-underflow")]
    [InlineData("noncanonical-intent")]
    public void RejectsInvalidPrepareRequests(string fault)
    {
        var request = CreateRequest(revocation: true);
        request = fault switch
        {
            "empty-schedule" => request with { ScheduleId = Guid.Empty },
            "empty-environment" => request with { EnvironmentId = Guid.Empty },
            "non-utc-deadline" => request with { HardDeadline = Deadline.ToOffset(TimeSpan.FromHours(1)) },
            "deadline-underflow" => request with { HardDeadline = DateTimeOffset.MinValue.AddSeconds(2) },
            "noncanonical-intent" => request with { Intent = request.Intent with { RecoveryMaterial = "YQ==" } },
            _ => throw new InvalidOperationException("Unknown fixture fault.")
        };
        Assert.Throws<InvalidDataException>(() => FormalRotationJournalProtocol.EncodeRequest(request));
    }

    private static FormalRotationPrepareRequest CreateRequest(bool revocation = false)
    {
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var intent = new GovernanceWriteIntent(Guid.NewGuid(), GovernanceJournalProtocol.Genesis, "YQ");
        var environment = Guid.NewGuid();
        return revocation ? FormalRotationJournalProtocol.PrepareRevocation(journal, intent, Guid.NewGuid(), environment, Deadline)
            : FormalRotationJournalProtocol.Prepare(journal, intent, environment, Deadline);
    }

    private static FormalRotationPreparePayload Reply(FormalRotationPrepareRequest request) => new(
        FormalRotationJournalProtocol.RequestHash(request), "history", "rotation-key", request.EnvironmentId,
        request.HardDeadline, request.ScheduleId, request.HardDeadline.AddSeconds(-2), request.ScheduleId is null ? null : request.HardDeadline,
        true, new(request.Intent.Previous, request.Intent, null));

    private static GovernanceWitnessTrust Trust(ECDsa key) => new("history", "rotation-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

    private static byte[] Signed(ECDsa key, FormalRotationPreparePayload payload, string domain = FormalRotationJournalProtocol.ResponseDomain) =>
        CanonicalJson.Serialize(new SignedFormalRotationPrepareResponse(payload, Sign(key, domain, payload)));

    private static string Sign<T>(ECDsa key, string domain, T payload) => Convert.ToBase64String(key.SignData(
        SecuritySignedEncoding.Unsigned(domain, payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}

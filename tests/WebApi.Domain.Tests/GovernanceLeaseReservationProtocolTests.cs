using System.Security.Cryptography;
using System.Text;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using Xunit;

namespace WebApi.Domain.Tests;

public sealed class GovernanceLeaseReservationProtocolTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SignedDurableAcknowledgementBindsTheExactJournalPositionAndReservation(int observedClockSkewSeconds)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request();
        var accepted = GovernanceLeaseReservationProtocol.VerifyResponse(request, Signed(signer, Reply(request)),
            Trust(signer), IssuedAt.AddSeconds(-observedClockSkewSeconds));

        Assert.Equal(request.Journal, accepted.Journal);
        Assert.Equal(request.ExpectedConfirmed, accepted.Confirmed);
        Assert.Equal(request.Reservation, accepted.Reservation);
        Assert.NotEqual(GovernanceJournalProtocol.RequestDomain, GovernanceLeaseReservationProtocol.RequestDomain);
        Assert.NotEqual(GovernanceJournalProtocol.ResponseDomain, GovernanceLeaseReservationProtocol.ResponseDomain);
    }

    [Theory]
    [InlineData("other-source")]
    [InlineData("other-key-id")]
    [InlineData("other-signature-key")]
    [InlineData("journal-response-domain")]
    [InlineData("reservation-request-domain")]
    [InlineData("pending")]
    [InlineData("not-durable")]
    [InlineData("wrong-position")]
    [InlineData("different-lease-id")]
    [InlineData("different-payload-hash")]
    [InlineData("different-scope")]
    [InlineData("different-expiry")]
    [InlineData("equivalent-non-utc-time")]
    [InlineData("expired-at-verification")]
    [InlineData("future-issue-time")]
    [InlineData("earlier-nonce")]
    [InlineData("other-authority")]
    [InlineData("other-generation")]
    [InlineData("missing-pending-flag")]
    [InlineData("has-unknown-field")]
    public void RejectsUntrustedOrInconsistentReservationResponses(string fault)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request();
        var reply = Reply(request);
        var now = IssuedAt;
        var domain = GovernanceLeaseReservationProtocol.ResponseDomain;
        switch (fault)
        {
            case "other-source": reply = reply with { SourceId = "other" }; break;
            case "other-key-id": reply = reply with { KeyId = "other" }; break;
            case "journal-response-domain": domain = GovernanceJournalProtocol.ResponseDomain; break;
            case "reservation-request-domain": domain = GovernanceLeaseReservationProtocol.RequestDomain; break;
            case "pending": reply = reply with { HasPendingWrite = true }; break;
            case "not-durable": reply = reply with { Durable = false }; break;
            case "wrong-position": reply = reply with { Confirmed = new(2, new string('d', 64)) }; break;
            case "different-lease-id": reply = reply with { Reservation = reply.Reservation with { LeaseId = Guid.NewGuid() } }; break;
            case "different-payload-hash": reply = reply with { Reservation = reply.Reservation with { LeasePayloadHash = new string('d', 64) } }; break;
            case "different-scope": reply = reply with { Reservation = reply.Reservation with { ProjectId = Guid.NewGuid() } }; break;
            case "different-expiry": reply = reply with { Reservation = reply.Reservation with { FreshUntil = IssuedAt.AddSeconds(6) } }; break;
            case "equivalent-non-utc-time":
                reply = reply with { Reservation = reply.Reservation with { FreshUntil = reply.Reservation.FreshUntil.ToOffset(TimeSpan.FromHours(1)) } };
                break;
            case "expired-at-verification": now = request.Reservation.FreshUntil; break;
            case "future-issue-time": now = request.Reservation.IssuedAt.AddSeconds(-3); break;
            case "earlier-nonce": request = request with { Nonce = new string('0', 64) }; break;
            case "other-authority": request = request with { Journal = request.Journal with { AuthorityId = Guid.NewGuid() } }; break;
            case "other-generation": request = request with { Journal = request.Journal with { Generation = Guid.NewGuid() } }; break;
        }

        var bytes = Signed(fault == "other-signature-key" ? other : signer, reply, domain);
        if (fault == "missing-pending-flag")
            bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"hasPendingWrite\":false,", "", StringComparison.Ordinal));
        if (fault == "has-unknown-field") bytes = [.. "{\"unexpected\":true,"u8.ToArray(), .. bytes.Skip(1)];

        Assert.Throws<InvalidDataException>(() => GovernanceLeaseReservationProtocol.VerifyResponse(request, bytes, Trust(signer), now));
    }

    [Theory]
    [InlineData("empty-scope")]
    [InlineData("empty-lease-id")]
    [InlineData("negative-epoch")]
    [InlineData("invalid-payload-hash")]
    [InlineData("invalid-checkpoint-hash")]
    [InlineData("invalid-signing-key-id")]
    [InlineData("zero-window")]
    [InlineData("long-window")]
    [InlineData("non-utc-issued")]
    [InlineData("wrong-confirmed-position")]
    [InlineData("empty-journal")]
    [InlineData("wrong-action")]
    public void RejectsInvalidExpectedPositionAndUnboundedOrIncompleteReservations(string fault)
    {
        var request = Request();
        var lease = request.Reservation;
        switch (fault)
        {
            case "empty-scope": lease = lease with { EnvironmentId = Guid.Empty }; break;
            case "empty-lease-id": lease = lease with { LeaseId = Guid.Empty }; break;
            case "negative-epoch": lease = lease with { Epoch = -1 }; break;
            case "invalid-payload-hash": lease = lease with { LeasePayloadHash = "arbitrary payload" }; break;
            case "invalid-checkpoint-hash": lease = lease with { CheckpointHash = new string('A', 64) }; break;
            case "invalid-signing-key-id": lease = lease with { SigningKeyId = "key with spaces" }; break;
            case "zero-window": lease = lease with { FreshUntil = lease.IssuedAt }; break;
            case "long-window": lease = lease with { FreshUntil = lease.IssuedAt.AddSeconds(31) }; break;
            case "non-utc-issued": lease = lease with { IssuedAt = lease.IssuedAt.ToOffset(TimeSpan.FromHours(1)) }; break;
            case "wrong-confirmed-position": request = request with { ExpectedConfirmed = new(0, new string('a', 64)) }; break;
            case "empty-journal": request = request with { Journal = request.Journal with { Generation = Guid.Empty } }; break;
            case "wrong-action": request = request with { Action = "prepare" }; break;
        }

        Assert.Throws<InvalidDataException>(() => GovernanceLeaseReservationProtocol.EncodeRequest(request with { Reservation = lease }, IssuedAt));
    }

    private static GovernanceLeaseReservationRequest Request() => GovernanceLeaseReservationProtocol.Reserve(
        new(Guid.NewGuid(), Guid.NewGuid()), new(1, new string('a', 64)),
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2,
            "lease-key", new string('b', 64), new string('c', 64), IssuedAt, IssuedAt.AddSeconds(5)));

    private static GovernanceLeaseReservationPayload Reply(GovernanceLeaseReservationRequest request) => new(
        GovernanceLeaseReservationProtocol.RequestHash(request), "witness", "reservation-key",
        request.ExpectedConfirmed, false, request.Reservation, true);

    private static GovernanceWitnessTrust Trust(ECDsa signer) => new("witness", "reservation-key",
        Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));

    private static byte[] Signed(ECDsa signer, GovernanceLeaseReservationPayload payload,
        string domain = GovernanceLeaseReservationProtocol.ResponseDomain) =>
        CanonicalJson.Serialize(new SignedGovernanceLeaseReservationResponse(payload, Convert.ToBase64String(signer.SignData(
            SecuritySignedEncoding.Unsigned(domain, payload), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
}

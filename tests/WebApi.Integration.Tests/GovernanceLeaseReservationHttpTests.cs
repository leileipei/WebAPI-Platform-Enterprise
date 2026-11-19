using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceLeaseReservationHttpTests
{
    private static readonly Uri Endpoint = new("https://journal.example.test/writes");
    private static readonly DateTimeOffset IssuedAt = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfiguredClientsRejectAnUnpinnedJournalBeforeTheTransportRuns(bool changeAuthority)
    {
        using var time = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var writer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificate = new CertificateRequest("CN=isolated-journal-writer", writer, HashAlgorithmName.SHA256);
        using var clientCertificate = certificate.CreateSelfSigned(IssuedAt.AddMinutes(-1), IssuedAt.AddMinutes(1));
        var pinned = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        using var clients = IndependentGovernanceWitnessClients.Create(new()
        {
            Enabled = true,
            JournalId = pinned,
            TimeEvidence = new(new Uri("https://time.example.test/proof"), "time-witness", "time-key",
                Convert.ToBase64String(time.ExportSubjectPublicKeyInfo())),
            HistoryEvidence = new(new Uri("https://journal.example.test/history"), "journal-witness", "witness-key", Trust(signer).PublicKey),
            GovernanceJournal = new(Endpoint, "journal-witness", "witness-key", Trust(signer).PublicKey)
        }, clientCertificate);
        Assert.NotNull(clients);
        var mismatch = changeAuthority ? pinned with { AuthorityId = Guid.NewGuid() } : pinned with { Generation = Guid.NewGuid() };
        // The canceled token also prevents accidental network access if the scope guard regresses.
        await Assert.ThrowsAsync<InvalidDataException>(() => clients.LeaseReservationWitness.ReserveAllowLeaseAsync(mismatch,
            GovernanceJournalProtocol.Genesis, Lease(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task AmbiguousResponseFailsAndExplicitRetryBindsTheSameLeaseToANewChallenge()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var confirmed = new GovernanceJournalPosition(7, new string('a', 64));
        var lease = Lease();
        var attempts = new List<GovernanceLeaseReservationRequest>();
        using var handler = new WitnessHandler(async (message, token) =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(Endpoint, message.RequestUri);
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("no-store", message.Headers.CacheControl!.ToString());
            var bytes = await message.Content.ReadAsByteArrayAsync(token);
            var request = JsonSerializer.Deserialize<GovernanceLeaseReservationRequest>(bytes, CanonicalJson.Options)!;
            Assert.Equal(GovernanceLeaseReservationProtocol.EncodeRequest(request, IssuedAt), bytes);
            Assert.Equal("reserveAllowLease", request.Action);
            Assert.Equal(journal, request.Journal);
            Assert.Equal(confirmed, request.ExpectedConfirmed);
            Assert.Equal(lease, request.Reservation);
            attempts.Add(request);
            if (attempts.Count == 1) throw new HttpRequestException("The fixture lost the response after recording the reservation.");
            return Acknowledge(signer, request);
        });
        using var client = new GovernanceJournalHttpWitness(Endpoint, Trust(signer),
            new BoundedWitnessHttpClient(handler, TimeSpan.FromSeconds(5)), new FixtureClock());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReserveAllowLeaseAsync(journal, confirmed, lease));
        Assert.Single(attempts);

        var verified = await client.ReserveAllowLeaseAsync(journal, confirmed, lease);
        Assert.Equal(lease, verified.Reservation);
        Assert.Equal(2, attempts.Count);
        Assert.NotEqual(attempts[0].Nonce, attempts[1].Nonce);
    }

    [Fact]
    public async Task ExpiredResponseCannotConfirmAReservationAndAnExpiredRetryIsNeverSent()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var lease = Lease();
        var clock = new FixtureClock();
        var requests = 0;
        using var handler = new WitnessHandler(async (message, token) =>
        {
            requests++;
            var request = JsonSerializer.Deserialize<GovernanceLeaseReservationRequest>(
                await message.Content!.ReadAsByteArrayAsync(token), CanonicalJson.Options)!;
            clock.Now = lease.FreshUntil;
            return Acknowledge(signer, request);
        });
        using var client = new GovernanceJournalHttpWitness(Endpoint, Trust(signer),
            new BoundedWitnessHttpClient(handler, TimeSpan.FromSeconds(5)), clock);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReserveAllowLeaseAsync(journal, GovernanceJournalProtocol.Genesis, lease));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReserveAllowLeaseAsync(journal, GovernanceJournalProtocol.Genesis, lease));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ALeaseIssuedBeyondTheMaximumClockSkewIsNeverSent()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var lease = Lease() with { IssuedAt = IssuedAt.AddSeconds(3), FreshUntil = IssuedAt.AddSeconds(8) };
        var requests = 0;
        using var handler = new WitnessHandler(async (message, token) =>
        {
            requests++;
            var request = JsonSerializer.Deserialize<GovernanceLeaseReservationRequest>(
                await message.Content!.ReadAsByteArrayAsync(token), CanonicalJson.Options)!;
            return Acknowledge(signer, request);
        });
        using var client = new GovernanceJournalHttpWitness(Endpoint, Trust(signer),
            new BoundedWitnessHttpClient(handler, TimeSpan.FromSeconds(5)), new FixtureClock());

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReserveAllowLeaseAsync(
            new(Guid.NewGuid(), Guid.NewGuid()), GovernanceJournalProtocol.Genesis, lease));
        Assert.Equal(0, requests);
    }

    private static GovernanceLeaseReservation Lease() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4, "lease-key", new string('b', 64), new string('c', 64), IssuedAt,
        IssuedAt.AddSeconds(5));

    private static GovernanceWitnessTrust Trust(ECDsa signer) => new("journal-witness", "witness-key",
        Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));

    private static HttpResponseMessage Acknowledge(ECDsa signer, GovernanceLeaseReservationRequest request)
    {
        var payload = new GovernanceLeaseReservationPayload(GovernanceLeaseReservationProtocol.RequestHash(request),
            "journal-witness", "witness-key", request.ExpectedConfirmed, false, request.Reservation, true);
        var signature = signer.SignData(SecuritySignedEncoding.Unsigned(GovernanceLeaseReservationProtocol.ResponseDomain, payload),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var content = new ByteArrayContent(CanonicalJson.Serialize(new SignedGovernanceLeaseReservationResponse(payload, Convert.ToBase64String(signature))));
        content.Headers.ContentType = new("application/json");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class FixtureClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = IssuedAt;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class WitnessHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class IndependentEvidenceHttpClientTests
{
    private static readonly Uri TimeEndpoint = new("https://time.example.test/proof");
    private static readonly Uri HistoryEndpoint = new("https://history.example.test/proof");
    private static readonly EvidenceChallenge Challenge = IndependentEvidenceSession.Start(
        new("control-plane-issuance", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, new string('a', 64)),
        TimeProvider.System).Challenge;

    [Fact]
    public async Task BothSourcesPostTheSameCanonicalChallengeToTheirOwnFixedEndpoint()
    {
        using var handler = new TestHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(CanonicalJson.Serialize(Challenge), await request.Content.ReadAsByteArrayAsync(token));
            Assert.Equal("no-store", request.Headers.CacheControl!.ToString());
            var response = Json(request.RequestUri == TimeEndpoint ? "time"u8.ToArray() : "history"u8.ToArray());
            response.Content.Headers.ContentType!.CharSet = "\"UTF-8\"";
            return response;
        });
        using var client = Create(handler);
        await using var time = await ((IIndependentTimeEvidenceSource)client).RequestAsync(Challenge);
        await using var history = await ((IIndependentSecurityHistoryWitness)client).RequestAsync(Challenge);
        Assert.Equal("time", await new StreamReader(time!).ReadToEndAsync());
        Assert.Equal("history", await new StreamReader(history!).ReadToEndAsync());
        Assert.Equal(new[] { TimeEndpoint, HistoryEndpoint }, handler.Endpoints);
    }

    [Theory]
    [InlineData("http://witness.example.test/proof")]
    [InlineData("https://user@witness.example.test/proof")]
    [InlineData("https://witness.example.test/proof?query=1")]
    [InlineData("https://witness.example.test/proof#fragment")]
    public void RejectsEndpointsWithUntrustedTransportOrEmbeddedParameters(string url) =>
        Assert.Throws<ArgumentException>(() => new IndependentEvidenceHttpClient(TimeEndpoint, new Uri(url)));

    [Theory]
    [InlineData("status")]
    [InlineData("type")]
    [InlineData("encoding")]
    [InlineData("declared-size")]
    [InlineData("actual-size")]
    [InlineData("empty")]
    public async Task RedirectsAndInvalidBodiesCannotProvideEvidence(string failure)
    {
        using var handler = new TestHandler((_, _) =>
        {
            var response = failure == "actual-size"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SizedStream(32769)) }
                : Json(failure == "empty" ? [] : "{}"u8.ToArray());
            response.Content.Headers.ContentType = new("application/json");
            switch (failure)
            {
                case "status": response.StatusCode = HttpStatusCode.TemporaryRedirect; response.Headers.Location = HistoryEndpoint; break;
                case "type": response.Content.Headers.ContentType = new("text/plain"); break;
                case "encoding": response.Content.Headers.ContentEncoding.Add("gzip"); break;
                case "declared-size": response.Content.Headers.ContentLength = 32769; break;
            }
            return Task.FromResult(response);
        });
        using var client = Create(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => ((IIndependentTimeEvidenceSource)client).RequestAsync(Challenge));
        Assert.Single(handler.Endpoints);
    }

    [Fact]
    public async Task BodyReadUsesTheSameTimeoutAndDisposesTheResponse()
    {
        using var body = new SizedStream(0, stall: true);
        using var handler = new TestHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        });
        using var client = new IndependentEvidenceHttpClient(TimeEndpoint, HistoryEndpoint,
            new BoundedWitnessHttpClient(handler, TimeSpan.FromMilliseconds(150)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ((IIndependentTimeEvidenceSource)client).RequestAsync(Challenge)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var handler = new TestHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}"u8.ToArray());
        });
        using var client = Create(handler);
        using var cancel = new CancellationTokenSource();
        var call = ((IIndependentSecurityHistoryWitness)client).RequestAsync(Challenge, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task JournalClientVerifiesEachTransitionAndRejectsAnEarlierSignedResponse()
    {
        using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var intent = new GovernanceWriteIntent(Guid.NewGuid(), GovernanceJournalProtocol.Genesis, "YQ");
        var next = GovernanceJournalProtocol.NextPosition(journal, intent);
        var trust = new GovernanceWitnessTrust("witness", "journal-key", Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo()));
        var nonces = new HashSet<string>();
        byte[]? previousWire = null;
        using var handler = new TestHandler(async (message, ct) =>
        {
            Assert.Equal(HistoryEndpoint, message.RequestUri);
            var body = await message.Content!.ReadAsByteArrayAsync(ct);
            var request = JsonSerializer.Deserialize<GovernanceWitnessRequest>(body, CanonicalJson.Options)!;
            Assert.Equal(journal, request.Journal);
            Assert.True(nonces.Add(request.Nonce));
            Assert.Equal(GovernanceJournalProtocol.EncodeRequest(request), body);
            if (nonces.Count == 4) return Json(previousWire!);

            GovernanceWitnessState state = request.Action switch
            {
                "observe" => new(intent.Previous, null, null),
                "prepare" => new(intent.Previous, request.Intent, null),
                "confirm" => new(next, null, new(request.OperationId!.Value, next)),
                _ => throw new InvalidOperationException("Unexpected fixture request.")
            };
            var payload = new GovernanceWitnessPayload(GovernanceJournalProtocol.RequestHash(request), trust.SourceId, trust.KeyId, state);
            var signature = signing.SignData(SecuritySignedEncoding.Unsigned(GovernanceJournalProtocol.ResponseDomain, payload),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            previousWire = CanonicalJson.Serialize(new SignedGovernanceWitnessResponse(payload, Convert.ToBase64String(signature)));
            return Json(previousWire);
        });
        using var client = new GovernanceJournalHttpWitness(HistoryEndpoint, trust, new BoundedWitnessHttpClient(handler, TimeSpan.FromSeconds(5)));
        Assert.Equal(intent.Previous, (await client.ObserveAsync(journal, null, default)).Confirmed);
        Assert.Equal(intent, (await client.PrepareAsync(journal, intent, default)).Pending);
        Assert.Equal(new(intent.OperationId, next), (await client.ConfirmAsync(journal, intent, default)).OperationConfirmation);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ObserveAsync(journal, intent.OperationId, default));
    }

    [Fact]
    public void JournalWriterRequiresAClientCertificateWithItsPrivateKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=isolated-fixture", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var complete = request.CreateSelfSigned(now.AddMinutes(-1), now.AddMinutes(5));
        using var publicOnly = X509CertificateLoader.LoadCertificate(complete.RawData);
        var trust = new GovernanceWitnessTrust("fixture", "journal-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        Assert.Throws<ArgumentException>(() => new GovernanceJournalHttpWitness(HistoryEndpoint, trust, publicOnly));
    }

    private static IndependentEvidenceHttpClient Create(TestHandler handler) =>
        new(TimeEndpoint, HistoryEndpoint, new BoundedWitnessHttpClient(handler, TimeSpan.FromSeconds(5)));

    private static HttpResponseMessage Json(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<Uri> Endpoints { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoints.Add(request.RequestUri!);
            return send(request, cancellationToken);
        }
    }

    private sealed class SizedStream(int size, bool stall = false) : Stream
    {
        private int remaining = size;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            int read = Math.Min(remaining, buffer.Length);
            buffer.Span[..read].Clear();
            remaining -= read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

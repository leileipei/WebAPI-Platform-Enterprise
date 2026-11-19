using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class FormalRotationJournalHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAmbiguousTimeoutDoesNotAttemptOrdinaryPrepareAndExactRetryUsesAFreshChallenge(bool revocation)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var endpoint = new Uri("https://journal.example.test/writes");
        var trust = new GovernanceWitnessTrust("history", "rotation-key", Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var intent = new GovernanceWriteIntent(Guid.NewGuid(), GovernanceJournalProtocol.Genesis, "YQ");
        var environment = Guid.NewGuid();
        var deadline = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var schedule = Guid.NewGuid();
        var requests = new List<FormalRotationPrepareRequest>();
        using var handler = new WitnessHandler(async (message, token) =>
        {
            Assert.Equal(endpoint, message.RequestUri);
            Assert.Equal(HttpMethod.Post, message.Method);
            var bytes = await message.Content!.ReadAsByteArrayAsync(token);
            var request = JsonSerializer.Deserialize<FormalRotationPrepareRequest>(bytes, CanonicalJson.Options)!;
            Assert.Equal(FormalRotationJournalProtocol.EncodeRequest(request), bytes);
            Assert.Equal(revocation ? "prepareFormalRotationRevocation" : "prepareFormalRotation", request.Action);
            Assert.Equal(journal, request.Journal);
            Assert.Equal(intent, request.Intent);
            Assert.Equal(environment, request.EnvironmentId);
            Assert.Equal(deadline, request.HardDeadline);
            Assert.Equal(revocation ? schedule : (Guid?)null, request.ScheduleId);
            requests.Add(request);
            if (requests.Count == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token);

            var payload = new FormalRotationPreparePayload(FormalRotationJournalProtocol.RequestHash(request), trust.SourceId, trust.KeyId,
                environment, deadline, request.ScheduleId, deadline.AddSeconds(-2), revocation ? deadline : null,
                true, new(intent.Previous, intent, null));
            var signature = Convert.ToBase64String(signer.SignData(SecuritySignedEncoding.Unsigned(FormalRotationJournalProtocol.ResponseDomain, payload),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            var content = new ByteArrayContent(CanonicalJson.Serialize(new SignedFormalRotationPrepareResponse(payload, signature)));
            content.Headers.ContentType = new("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new GovernanceJournalHttpWitness(endpoint, trust, new BoundedWitnessHttpClient(handler, TimeSpan.FromSeconds(1)));

        Task<GovernanceWitnessState> PrepareAsync() => revocation
            ? client.PrepareFormalRotationRevocationAsync(journal, intent, schedule, environment, deadline, default)
            : client.PrepareFormalRotationAsync(journal, intent, environment, deadline, default);

        await Assert.ThrowsAsync<InvalidDataException>(() => PrepareAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(requests);
        Assert.Equal(intent, (await PrepareAsync()).Pending);
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0].Nonce, requests[1].Nonce);
    }

    [Fact]
    public async Task ManualDecisionUsesItsOwnSignedActionAndAnExplicitRetryGetsAFreshChallenge()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var endpoint = new Uri("https://journal.example.test/writes");
        var trust = new GovernanceWitnessTrust("history", "rotation-key", Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()));
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var intent = new GovernanceWriteIntent(Guid.NewGuid(), GovernanceJournalProtocol.Genesis, "YQ");
        var schedule = Guid.NewGuid();
        var environment = Guid.NewGuid();
        var deadline = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var requests = new List<FormalRotationDecisionPrepareRequest>();
        using var handler = new WitnessHandler(async (message, token) =>
        {
            Assert.Equal(endpoint, message.RequestUri);
            Assert.Equal(HttpMethod.Post, message.Method);
            var bytes = await message.Content!.ReadAsByteArrayAsync(token);
            var request = JsonSerializer.Deserialize<FormalRotationDecisionPrepareRequest>(bytes, CanonicalJson.Options)!;
            Assert.Equal("prepareFormalRotationDecision", request.Action);
            Assert.Equal(journal, request.Journal);
            Assert.Equal(intent, request.Intent);
            Assert.Equal(schedule, request.ScheduleId);
            Assert.Equal(environment, request.EnvironmentId);
            Assert.True(deadline.EqualsExact(request.HardDeadline));
            Assert.Equal("AbortConfirmed", request.Decision);
            Assert.Equal(CanonicalJson.Serialize(request), bytes);
            requests.Add(request);
            if (requests.Count == 1) throw new HttpRequestException("The signed receipt was not received.");

            var payload = new FormalRotationDecisionPreparePayload(FormalRotationDecisionJournalProtocol.RequestHash(request),
                trust.SourceId, trust.KeyId, schedule, environment, deadline, "AbortConfirmed", deadline.AddSeconds(-2), deadline.AddTicks(-1),
                true, new(intent.Previous, intent, null));
            var signature = Convert.ToBase64String(signer.SignData(
                SecuritySignedEncoding.Unsigned(FormalRotationDecisionJournalProtocol.ResponseDomain, payload),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            var content = new ByteArrayContent(CanonicalJson.Serialize(new SignedFormalRotationDecisionPrepareResponse(payload, signature)));
            content.Headers.ContentType = new("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new GovernanceJournalHttpWitness(endpoint, trust, new BoundedWitnessHttpClient(handler, TimeSpan.FromSeconds(1)),
            new FixedTimeProvider(deadline.AddTicks(1)));

        Task<GovernanceWitnessState> PrepareAsync() => client.PrepareFormalRotationDecisionAsync(journal, intent, schedule, environment,
            deadline, "AbortConfirmed", default);

        await Assert.ThrowsAsync<InvalidDataException>(PrepareAsync);
        Assert.Single(requests);
        Assert.Equal(intent, (await PrepareAsync()).Pending);
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0].Nonce, requests[1].Nonce);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class WitnessHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed partial class GovernanceDeploymentEvidenceTests
{
    [Theory]
    [InlineData("good", true)]
    [InlineData("wrong-source", false)]
    [InlineData("wrong-signature", false)]
    [InlineData("old-time", false)]
    [InlineData("old-claim", false)]
    [InlineData("wrong-scope", false)]
    [InlineData("replay", false)]
    [InlineData("expired-monotonic", false)]
    public async Task DeploymentProofRequiresAuthenticatedFreshResponseForExactCurrentScope(string scenario, bool covered)
    {
        var clock = new FixtureClock();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var scope = new CredentialRunningIngressScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, new string('a', 64),
            Guid.NewGuid(), 1, new string('b', 64), Guid.NewGuid(), new string('c', 64), 1, 1,
            [new(Guid.NewGuid(), Guid.NewGuid(), new string('d', 64))]);
        var pinned = new GovernanceWitnessTrust("ingress-fixture", "fixture-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        var cache = new GovernanceDeploymentEvidenceCache(clock);
        var actual = scenario == "wrong-scope" ? scope with { AccessAddressRevision = 4 } : scope;
        byte[]? prior = null;
        using var transport = new BoundedWitnessHttpClient(new FixtureHandler(request =>
        {
            Assert.Equal(journal, request.Journal);
            if (scenario == "replay" && prior is not null) return prior;
            var observed = scenario == "old-time" ? clock.GetUtcNow().AddSeconds(-3) : clock.GetUtcNow();
            var expires = observed.AddSeconds(20);
            var claim = new GovernanceDeploymentClaim("running-ingress",
                GovernanceDeploymentEvidenceProtocol.ScopeHash("running-ingress", actual), new string('e', 64),
                scenario == "old-claim" ? observed.AddSeconds(-6) : observed, observed.AddSeconds(8));
            var payload = new GovernanceDeploymentEvidencePayload(GovernanceDeploymentEvidenceProtocol.RequestHash(request),
                scenario == "wrong-source" ? "other-source" : pinned.SourceId, pinned.KeyId, observed, expires, [claim], []);
            var signature = (scenario == "wrong-signature" ? other : key).SignData(
                SecuritySignedEncoding.Unsigned(GovernanceDeploymentEvidenceProtocol.ResponseDomain, payload),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            prior = CanonicalJson.Serialize(new SignedGovernanceDeploymentEvidenceResponse(payload, Convert.ToBase64String(signature)));
            return prior;
        }), TimeSpan.FromSeconds(1));
        using var client = new GovernanceDeploymentEvidenceHttpWitness(new Uri("https://ingress.example.test/observe"), pinned,
            journal, clock, cache, transport);

        Assert.Null(cache.VerifiedUntil(scope, clock.GetUtcNow()));
        if (scenario is "wrong-source" or "wrong-signature" or "old-time" or "old-claim")
            await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync(default));
        else
            await client.RefreshAsync(default);

        if (scenario == "replay")
        {
            Assert.NotNull(cache.VerifiedUntil(scope, clock.GetUtcNow()));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync(default));
        }
        if (scenario == "expired-monotonic") clock.Advance(21);
        var bound = cache.VerifiedUntil(scope, clock.GetUtcNow());
        Assert.Equal(covered, bound is not null);
        Assert.Null(cache.VerifiedUntil(scope with { ReleaseId = Guid.NewGuid() }, clock.GetUtcNow()));
    }

    private sealed class FixtureHandler(Func<GovernanceDeploymentEvidenceRequest, byte[]> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(ct);
            var evidence = JsonSerializer.Deserialize<GovernanceDeploymentEvidenceRequest>(body, CanonicalJson.Options)!;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(reply(evidence)) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
        private long ticks;

        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
}

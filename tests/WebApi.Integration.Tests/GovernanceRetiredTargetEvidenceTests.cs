using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed partial class GovernanceDeploymentEvidenceTests
{
    [Theory]
    [InlineData("good", true)]
    [InlineData("absent", false)]
    [InlineData("wrong-scope", false)]
    [InlineData("wrong-signature", false)]
    [InlineData("wrong-nonce", false)]
    [InlineData("wrong-journal", false)]
    [InlineData("duplicate-target", false)]
    [InlineData("foreign-reservation", false)]
    [InlineData("duplicate-reservation", false)]
    [InlineData("expired-monotonic", false)]
    [InlineData("stale-isolation", false)]
    [InlineData("replay", false)]
    public async Task RetiredCoverageUsesThePinnedDeploymentSignatureNonceAndExactScope(string scenario, bool expected)
    {
        var clock = new FixtureClock();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var scope = RetiredScope();
        var cache = new GovernanceDeploymentEvidenceCache(clock);
        var trust = new GovernanceWitnessTrust("retired-deployment", "retired-key", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        byte[]? prior = null;
        using var transport = new BoundedWitnessHttpClient(new FixtureHandler(request =>
        {
            if (scenario == "replay" && prior is not null) return prior;
            var now = clock.GetUtcNow();
            var actual = scenario switch
            {
                "wrong-scope" => scope with { ChallengeNonce = Guid.NewGuid() },
                "wrong-journal" => scope with { Journal = scope.Journal with { Generation = Guid.NewGuid() } },
                _ => scope
            };
            var reservation = RetiredReservation(actual, now.AddSeconds(-3), now.AddSeconds(20));
            if (scenario == "foreign-reservation") reservation = reservation with { EnvironmentId = Guid.NewGuid() };
            var proof = new GovernanceRetiredTargetCoverage(actual, new string('c', 64), true, false, true, true,
                now.AddMilliseconds(-100), now.AddMilliseconds(100), now.AddSeconds(8),
                scenario == "duplicate-reservation" ? [reservation, reservation] : [reservation]);
            var payload = new GovernanceDeploymentEvidencePayload(
                scenario == "wrong-nonce" ? new string('d', 64) : GovernanceDeploymentEvidenceProtocol.RequestHash(request),
                trust.SourceId, trust.KeyId, now, now.AddSeconds(20), [], [],
                scenario == "absent" ? null : scenario == "duplicate-target" ? [proof, proof] : [proof]);
            var signer = scenario == "wrong-signature" ? other : key;
            prior = CanonicalJson.Serialize(new SignedGovernanceDeploymentEvidenceResponse(payload,
                Convert.ToBase64String(signer.SignData(SecuritySignedEncoding.Unsigned(
                    GovernanceDeploymentEvidenceProtocol.ResponseDomain, payload), HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
            return prior;
        }), TimeSpan.FromSeconds(1));
        using var client = new GovernanceDeploymentEvidenceHttpWitness(new Uri("https://deployment.example.test/observe"),
            trust, scope.Journal, clock, cache, transport);
        if (scenario is "wrong-signature" or "wrong-nonce" or "wrong-journal" or "duplicate-target" or "foreign-reservation" or "duplicate-reservation")
            await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync(default));
        else await client.RefreshAsync(default);
        if (scenario == "expired-monotonic") clock.Advance(10);
        if (scenario == "stale-isolation") clock.Advance(4);
        if (scenario == "replay") await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync(default));
        Assert.Equal(expected, cache.GetVerifiedCoverage(scope) is not null);
        Assert.Null(cache.GetVerifiedCoverage(scope with { InstanceId = Guid.NewGuid() }));
        Assert.Null(cache.GetVerifiedCoverage(scope with { Operation = scope.Operation with { OperationId = Guid.NewGuid() } }));
        Assert.Null(cache.GetVerifiedCoverage(scope with { Epoch = scope.Epoch + 1 }));
        Assert.Null(cache.GetVerifiedCoverage(scope with { CurrentIngressScopeHash = new string('f', 64) }));
    }

    [Theory]
    [InlineData("isolated-live", true)]
    [InlineData("live", false)]
    [InlineData("expired-at-bound", true)]
    [InlineData("expired-before-bound", false)]
    [InlineData("database-clock-forward", false)]
    [InlineData("lost-reply-live-reservation", false)]
    [InlineData("missing-old-generation", false)]
    [InlineData("altered-payload", false)]
    [InlineData("shortened-horizon", false)]
    [InlineData("not-complete", false)]
    [InlineData("not-closed", false)]
    [InlineData("pending", false)]
    public void RetiredExpiryRequiresTheIndependentCompleteReservationUpperBound(string scenario, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = RetiredScope();
        var expiry = scenario is "isolated-live" or "live" or "database-clock-forward" ? now.AddSeconds(10) : now.AddSeconds(-2);
        var reservation = RetiredReservation(scope, expiry.AddSeconds(-25), expiry);
        var issued = new List<GatewaySecurityLeaseIssuance> { Issuance(reservation) };
        var reservations = new List<GovernanceLeaseReservation> { reservation };
        if (scenario == "missing-old-generation")
            issued.Add(Issuance(reservation with { LeaseId = Guid.NewGuid(), SecurityGeneration = Guid.NewGuid() }));
        if (scenario == "lost-reply-live-reservation")
            reservations.Add(reservation with { LeaseId = Guid.NewGuid(), IssuedAt = now.AddSeconds(-1), FreshUntil = now.AddSeconds(20) });
        if (scenario == "altered-payload") reservations[0] = reservation with { LeasePayloadHash = new string('b', 64) };
        var lower = scenario == "expired-before-bound" ? now.AddTicks(-1) : now;
        var proof = new GovernanceRetiredTargetCoverage(scope, new string('c', 64), scenario != "not-complete", scenario == "pending",
            scenario != "not-closed", scenario == "isolated-live", lower, now.AddMilliseconds(100), now.AddSeconds(15),
            reservations.OrderBy(row => row.LeaseId).ToArray());
        var observed = scenario == "database-clock-forward" ? now.AddSeconds(12) : now;
        Assert.Equal(expected, CredentialRetiredTargetCoverage.CoversKnownLeases(scope, proof, issued, [],
            scenario == "shortened-horizon" ? expiry.AddSeconds(1) : expiry, observed));
    }

    private static GovernanceRetiredTargetScope RetiredScope()
    {
        var journal = new GovernanceJournalId(Guid.NewGuid(), Guid.NewGuid());
        var position = new GovernanceJournalPosition(2, new string('a', 64));
        return new(journal, position, new(Guid.NewGuid(), position), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 1, new string('b', 64), 1, new string('b', 64), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, new string('d', 64));
    }

    private static GovernanceLeaseReservation RetiredReservation(GovernanceRetiredTargetScope scope, DateTimeOffset issued, DateTimeOffset expires) =>
        new(Guid.NewGuid(), scope.OrganizationId, scope.ProjectId, scope.EnvironmentId, scope.NodeId, scope.InstanceId,
            scope.SecurityGeneration, scope.Epoch, "retired-issuer", new string('e', 64), new string('f', 64), issued, expires);

    private static GatewaySecurityLeaseIssuance Issuance(GovernanceLeaseReservation row) => new()
    {
        LeaseId = row.LeaseId, OrganizationId = row.OrganizationId, ProjectId = row.ProjectId, EnvironmentId = row.EnvironmentId,
        NodeId = row.NodeId, InstanceId = row.InstanceId, SecurityGeneration = row.SecurityGeneration, Epoch = row.Epoch,
        SigningKeyId = row.SigningKeyId, CheckpointHash = row.CheckpointHash, PayloadHash = row.LeasePayloadHash,
        IssuedAt = row.IssuedAt, FreshUntil = row.FreshUntil
    };
}

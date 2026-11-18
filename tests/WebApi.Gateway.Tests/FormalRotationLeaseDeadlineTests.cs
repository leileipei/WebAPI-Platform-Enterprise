using WebApi.Domain.Applications;
using WebApi.Domain.CredentialSecurity;
using WebApi.Gateway.Security;
using WebApi.Gateway.Tests.Support;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class FormalRotationLeaseDeadlineTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(2)]
    public async Task CappedLeaseRejectsNewAdmissionsWithoutAWorkerAtTheCutoff(double clockOffset)
    {
        using var fixture = new SecurityFixture();
        var oldCredential = Guid.NewGuid();
        var now = fixture.Clock.GetUtcNow();
        var hardDeadline = now.AddSeconds(12);
        var original = fixture.Pair(0, []);
        var payload = original.Lease.Payload;
        var cap = RotationLeaseDeadlinePolicy.CapExpiry(hardDeadline, now, payload.FreshUntil, payload.FreshUntil,
            TimeSpan.FromSeconds(payload.MaximumClockSkewSeconds), TimeSpan.FromSeconds(1));
        Assert.Equal(now.AddSeconds(10), cap);
        var lease = SecurityCheckpointVerifier.VerifyNodeLease(new(fixture.Signer.SignNodeLease(payload with { FreshUntil = cap!.Value })),
            fixture.Keys, fixture.Env, fixture.Node, fixture.Instance);

        fixture.Clock.ShiftUtc(clockOffset);
        Assert.True((await fixture.State.ApplyAsync(original.Checkpoint, lease)).Applied);
        fixture.Clock.Advance(10 - clockOffset - 0.5);
        Assert.Equal(SecurityAdmissionDecision.Allow, fixture.State.CanAdmit(oldCredential, fixture.Clock.GetUtcNow()));
        fixture.Clock.Advance(0.5);
        Assert.Equal(SecurityAdmissionDecision.Unavailable, fixture.State.CanAdmit(oldCredential, fixture.Clock.GetUtcNow()));
        fixture.Clock.Advance(2 + clockOffset);
        Assert.Equal(SecurityAdmissionDecision.Unavailable, fixture.State.CanAdmit(oldCredential, fixture.Clock.GetUtcNow()));

        fixture.Clock.ShiftUtc(-clockOffset);
        var revoked = fixture.Pair(1, [oldCredential]);
        fixture.Clock.ShiftUtc(clockOffset);
        Assert.True((await fixture.State.ApplyAsync(revoked.Checkpoint, revoked.Lease)).Applied);
        Assert.Equal(SecurityAdmissionDecision.KnownRevoked, fixture.State.CanAdmit(oldCredential, fixture.Clock.GetUtcNow()));
        Assert.Equal(SecurityAdmissionDecision.Allow, fixture.State.CanAdmit(Guid.NewGuid(), fixture.Clock.GetUtcNow()));
    }
}

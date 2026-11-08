using WebApi.Contracts.Runtime;
using WebApi.Gateway.Security;
using WebApi.Gateway.Tests.Support;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class Governance24SecurityStateTests
{
    private static RuntimeSnapshot Legacy(SecurityFixture fixture, string schema)
        => new(schema, fixture.Env, 1, fixture.Clock.GetUtcNow(), [], [], [], []);

    private static RuntimeSnapshot Governance(SecurityFixture fixture, string schema, long epoch, string minimum)
        => new(schema, fixture.Env, 1, fixture.Clock.GetUtcNow(), [], [], [], [], fixture.Generation, epoch, false, minimum);

    [Fact]
    public async Task Network23RequiresFreshTrustedStateAndCannotBypassGovernance23Floor()
    {
        using var fixture = new SecurityFixture();
        var network = Legacy(fixture, "2.3");
        Assert.False(fixture.State.CanUseSnapshot(network));
        var legacyState = fixture.Pair(0, [], minimumSchema: "2.0");
        Assert.True((await fixture.State.ApplyAsync(legacyState.Checkpoint, legacyState.Lease)).Applied);
        Assert.True(fixture.State.CanUseSnapshot(network));
        var strict = fixture.Pair(1, [], minimumSchema: "2.3");
        Assert.True((await fixture.State.ApplyAsync(strict.Checkpoint, strict.Lease)).Applied);
        Assert.False(fixture.State.CanUseSnapshot(network));
        Assert.True(fixture.State.CanUseSnapshot(Governance(fixture, "2.3", 1, "2.3")));
    }

    [Fact]
    public async Task Governance24RequiresItsOwnShapeAndCannotFallBackToEither23Dialect()
    {
        using var fixture = new SecurityFixture();
        var strict = fixture.Pair(1, [], minimumSchema: "2.4");
        Assert.True((await fixture.State.ApplyAsync(strict.Checkpoint, strict.Lease)).Applied);
        var valid = Governance(fixture, "2.4", 1, "2.4");
        Assert.True(fixture.State.CanUseSnapshot(valid));
        Assert.False(fixture.State.CanUseSnapshot(Legacy(fixture, "2.3")));
        Assert.False(fixture.State.CanUseSnapshot(Governance(fixture, "2.3", 1, "2.3")));
        Assert.False(fixture.State.CanUseSnapshot(valid with { RequiredSecurityGeneration = null }));
        Assert.False(fixture.State.CanUseSnapshot(valid with { RequiredSecurityGeneration = Guid.NewGuid() }));
        Assert.False(fixture.State.CanUseSnapshot(valid with { RequiredSecurityEpoch = 2 }));
    }

    [Fact]
    public async Task RestartCannotReuseAFormerInstancesLeaseEvenForLegacyNetwork23()
    {
        using var fixture = new SecurityFixture();
        var original = fixture.Pair(0, [], minimumSchema: "2.0");
        Assert.True((await fixture.State.ApplyAsync(original.Checkpoint, original.Lease)).Applied);
        await fixture.State.DisposeAsync();
        var nextInstance = Guid.NewGuid();
        await using var restored = new SecurityStateStore(new(fixture.Env, () => fixture.Node, nextInstance), fixture.Disk, fixture.Clock);
        Assert.True(await restored.RestoreAsync(fixture.Keys));
        Assert.False(restored.IsReady);
        Assert.False(restored.CanUseSnapshot(Legacy(fixture, "2.3")));
        Assert.False((await restored.ApplyAsync(original.Checkpoint, original.Lease)).Applied);
        fixture.Clock.Advance(1);
        var fresh = fixture.Pair(0, [], leaseInstance: nextInstance, minimumSchema: "2.0");
        Assert.True((await restored.ApplyAsync(fresh.Checkpoint, fresh.Lease)).Applied);
        Assert.True(restored.CanUseSnapshot(Legacy(fixture, "2.3")));
    }

    [Fact]
    public async Task SnapshotMustMatchCurrentPolicyAndCannotOutliveItsLease()
    {
        using var fixture = new SecurityFixture();
        var strict = fixture.Pair(1, [], minimumSchema: "2.4", requireEnvironmentCredentials: true);
        Assert.True((await fixture.State.ApplyAsync(strict.Checkpoint, strict.Lease)).Applied);
        var valid = Governance(fixture, "2.4", 1, "2.4") with { RequireEnvironmentCredentials = true };
        Assert.True(fixture.State.CanUseSnapshot(valid));
        Assert.False(fixture.State.CanUseSnapshot(valid with { RequireEnvironmentCredentials = false }));
        Assert.False(fixture.State.CanUseSnapshot(valid with { MinimumSnapshotSchema = "2.3" }));
        fixture.Clock.Advance(31);
        Assert.False(fixture.State.CanUseSnapshot(valid));
        Assert.False((await fixture.State.ApplyAsync(strict.Checkpoint, strict.Lease)).Applied);
        Assert.False(fixture.State.CanUseSnapshot(valid));
    }

    [Theory]
    [InlineData("missing-collection")]
    [InlineData("null-cluster")]
    [InlineData("null-policy")]
    [InlineData("null-destination")]
    public async Task MalformedSnapshotFailsClosedWithoutEscapingTheAdmissionCheck(string variant)
    {
        using var fixture = new SecurityFixture();
        var state = fixture.Pair(0, [], minimumSchema: "2.0");
        Assert.True((await fixture.State.ApplyAsync(state.Checkpoint, state.Lease)).Applied);
        var snapshot = Legacy(fixture, "2.3");
        snapshot = variant switch
        {
            "missing-collection" => snapshot with { Clusters = null! },
            "null-cluster" => snapshot with { Clusters = [null!] },
            "null-policy" => snapshot with { Policies = [null!] },
            "null-destination" => snapshot with { Clusters = [new(Guid.NewGuid(), fixture.Env, "RoundRobin", false, "/health", 30, [null!])] },
            _ => throw new InvalidOperationException()
        };
        Assert.False(fixture.State.CanUseSnapshot(snapshot));
    }
}

using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Gateway;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Delivery;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed partial class PostgresWitnessedCredentialRevocationTests
{
    [Theory]
    [InlineData("isolated", "FullyApplied", true)]
    [InlineData("missing", "Partial", false)]
    [InlineData("incomplete", "Partial", false)]
    [InlineData("pending", "Partial", false)]
    [InlineData("issuance-open", "Partial", false)]
    [InlineData("missing-issuance", "Partial", false)]
    [InlineData("altered-issuance", "Partial", false)]
    [InlineData("wrong-operation", "Partial", false)]
    [InlineData("wrong-instance", "Partial", false)]
    [InlineData("wrong-challenge", "Partial", false)]
    [InlineData("wrong-head", "Partial", false)]
    [InlineData("live-without-isolation", "Partial", false)]
    [InlineData("expired-proof", "Partial", false)]
    [InlineData("changed-proof", "Partial", false)]
    [InlineData("missing-current-ack", "Partial", true)]
    public async Task RetiredOriginalTargetNeedsIndependentCompleteCoverageWithoutReplacingCurrentAck(
        string scenario, string expectedStatus, bool safelyRetired)
    {
        await using var fixture = await Fixture.StartAsync();
        const string secret = "retired-target-test-enrollment-0123456789";
        Guid nodeId, originalInstance;
        string nodeName;
        await using (var setup = fixture.Database.Context())
        {
            var node = await setup.Set<GatewayNode>().OrderBy(x => x.Id).FirstAsync();
            node.IdentityHash = NodeIdentityService.Hash(secret);
            nodeId = node.Id; nodeName = node.NodeName; originalInstance = Guid.Parse(node.InstanceId!);
            await setup.SaveChangesAsync();
        }
        var operation = (await fixture.RevokeAsync()).OperationId;
        await using var db = fixture.Database.Context();
        var original = await db.Set<SecurityOperationTarget>().AsNoTracking()
            .SingleAsync(x => x.OperationId == operation && x.NodeId == nodeId);
        var replacement = Guid.NewGuid();
        var identities = new NodeIdentityService(db, new([new(fixture.EnvironmentId, nodeName, NodeIdentityService.Hash(secret))]));
        var registry = new NodeRegistry(db, identities);
        var registered = await registry.RegisterAsync(new RegisterNodeRequest(fixture.EnvironmentId, nodeName, replacement,
            "coverage-test", ["2.4"], ["security-ledger-v1"]), secret);
        Assert.Equal(nodeId, registered.NodeId);
        await registry.HeartbeatAsync(new(nodeId, fixture.EnvironmentId, NodeIdentityService.Hash(secret)),
            new(replacement, 1, 1, "Ready", ["2.4"], ["security-ledger-v1"]));
        await fixture.ApplyAbortProofsAsync(operation, scenario == "missing-current-ack" ? 1 : 2, currentOnly: true);
        // A replacement must acknowledge the published configuration through the
        // runtime receipt path; neither a heartbeat nor its security ACK replaces
        // the original frozen release target/ACK.
        await RecordReplacementRuntimeAckAsync(fixture, nodeId, replacement, secret);
        var checkpoint = await ReceiptCheckpointAsync(db, fixture.EnvironmentId);
        var reservations = (await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().Where(row =>
                row.NodeId == nodeId && row.InstanceId == originalInstance).ToArrayAsync())
            .Select(row => new GovernanceLeaseReservation(row.LeaseId, row.OrganizationId, row.ProjectId, row.EnvironmentId,
                row.NodeId, row.InstanceId, row.SecurityGeneration, row.Epoch, row.SigningKeyId, row.CheckpointHash,
                row.PayloadHash, row.IssuedAt, row.FreshUntil)).OrderBy(row => row.LeaseId).ToArray();
        Assert.NotEmpty(reservations);
        Assert.True(reservations.Max(row => row.FreshUntil) > DateTimeOffset.UtcNow);
        var proofReads = 0;
        var independent = new RetiredReceiptWitness(scope =>
        {
            if (scenario == "missing") return null;
            Assert.Equal(operation, scope.Operation.OperationId);
            Assert.Equal(original.ChallengeNonce, scope.ChallengeNonce);
            Assert.Equal(original.LastLeaseUntil, scope.LastLeaseUntil);
            var now = DateTimeOffset.UtcNow;
            var proof = new GovernanceRetiredTargetCoverage(scope, new string('c', 64), true, false, true, true,
                now.AddMilliseconds(-100), now.AddMilliseconds(100), now.AddSeconds(8), reservations);
            proofReads++;
            return scenario switch
            {
                "incomplete" => proof with { Complete = false },
                "pending" => proof with { HasPendingWrite = true },
                "issuance-open" => proof with { IssuanceClosed = false },
                "missing-issuance" => proof with { Reservations = [] },
                "altered-issuance" => proof with { Reservations = reservations.Select(row => row with { LeasePayloadHash = new string('d', 64) }).ToArray() },
                "wrong-operation" => proof with { Scope = scope with { Operation = scope.Operation with { OperationId = Guid.NewGuid() } } },
                "wrong-instance" => proof with { Scope = scope with { InstanceId = replacement } },
                "wrong-challenge" => proof with { Scope = scope with { ChallengeNonce = Guid.NewGuid() } },
                "wrong-head" => proof with { Scope = scope with { HeadHash = new string('d', 64) } },
                "live-without-isolation" => proof with { IngressIsolated = false },
                "expired-proof" => proof with { Lower = now.AddSeconds(-10), Upper = now.AddSeconds(-9), ValidUntil = now.AddSeconds(-1) },
                "changed-proof" when proofReads > 1 => proof with { ProofHash = new string('d', 64) },
                _ => proof
            };
        });
        var ingress = new ReceiptIngress(DateTimeOffset.UtcNow.AddSeconds(20));
        var history = new ReceiptHistory(checkpoint);
        var missing = new PostgresRevocationReceiptEvidence(fixture.Journal, fixture.Witness, history, ingress);
        var before = await new SecurityOperationStatusReader(db, missing).ReadRevocationAsync(operation, fixture.Actor, (_, _) => Task.CompletedTask);
        Assert.Equal("Partial", before.Status);
        Assert.Equal("Pending", Assert.Single(before.Targets, target => target.InstanceGuid == originalInstance).State);
        var evidence = new PostgresRevocationReceiptEvidence(fixture.Journal, fixture.Witness, history, ingress, retiredWitness: independent);
        var after = await new SecurityOperationStatusReader(db, evidence).ReadRevocationAsync(operation, fixture.Actor, (_, _) => Task.CompletedTask);
        Assert.Equal(expectedStatus, after.Status);
        Assert.Equal(3, after.Targets.Count);
        Assert.Equal(safelyRetired ? "SafeSuperseded" : "Pending", Assert.Single(after.Targets,
            target => target.NodeId == nodeId && target.InstanceGuid == originalInstance).State);
        Assert.Equal(before.Targets.Select(target => (target.NodeId, target.InstanceGuid)),
            after.Targets.Select(target => (target.NodeId, target.InstanceGuid)));
        Assert.False(await db.Set<SecurityAckVerification>().AnyAsync(row => row.OperationId == operation && row.InstanceGuid == originalInstance));
        var retained = await db.Set<SecurityOperationTarget>().AsNoTracking().SingleAsync(row =>
            row.OperationId == operation && row.NodeId == nodeId && row.InstanceGuid == originalInstance);
        Assert.Equal(original.ChallengeNonce, retained.ChallengeNonce);
        Assert.Equal(original.CreatedAt, retained.CreatedAt);
        Assert.Equal("Pending", retained.TargetState);
        Assert.Equal(reservations.Length, await db.Set<GatewaySecurityLeaseIssuance>().CountAsync(row =>
            row.NodeId == nodeId && row.InstanceId == originalInstance));
    }

    private sealed class RetiredReceiptWitness(Func<GovernanceRetiredTargetScope, GovernanceRetiredTargetCoverage?> observe)
        : ICredentialRetiredTargetWitness
    {
        public GovernanceRetiredTargetCoverage? GetVerifiedCoverage(GovernanceRetiredTargetScope scope) => observe(scope);
    }

    private static async Task RecordReplacementRuntimeAckAsync(Fixture fixture, Guid nodeId, Guid instanceId, string secret)
    {
        await using var db = fixture.Database.Context();
        var config = await db.Set<GatewayConfigVersion>().SingleAsync();
        config.Status = "Published";
        await db.SaveChangesAsync();
        var release = await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync();
        Assert.NotNull(release.DeploymentSequence);
        // This ordinary release has no promotion chain. LockReleaseAsync only
        // uses the database-backed promotion lookup and lock coordinator.
        var reads = new PromotionReadService(db, null!, null!, null!, null!, null!, null!, null!, null!);
        var execution = new PromotionExecutionService(db, new DeliveryLockCoordinator(db), null!, reads, null!);
        var identities = new NodeIdentityService(db, new([]));
        var ack = new AckService(db, identities, execution, new IsolatedDeliveryContinuity(fixture.Journal), new(true));
        await ack.RecordAsync(nodeId, new(instanceId, release.Id, config.VersionNo, release.DeploymentSequence.Value,
                config.SnapshotHash!, DateTimeOffset.UtcNow, true, null),
            new(nodeId, fixture.EnvironmentId, NodeIdentityService.Hash(secret)));
        var original = await db.Set<ReleaseTarget>().AsNoTracking().SingleAsync(row => row.NodeId == nodeId);
        var frozenAck = await db.Set<GatewayAck>().AsNoTracking().SingleAsync(row => row.NodeId == nodeId);
        Assert.NotEqual(instanceId.ToString(), original.InstanceId);
        Assert.Equal(original.InstanceId, frozenAck.InstanceId);
    }
}

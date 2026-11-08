using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Gateway;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceSecurityCoverageIntegrationTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task MissingCurrentNodeOrEnvironmentCannotBeReportedFullyApplied(bool missingCurrentNode, bool prematureReceipt)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var organization = new Organization { Code = "ORG", Name = "Organization" };
        var project = new Project { OrganizationId = organization.Id, Code = "PRJ", Name = "Project" };
        var firstEnvironment = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test" };
        var secondEnvironment = new EnvironmentRecord { ProjectId = project.Id, Code = "PROD", Name = "Production" };
        db.AddRange(organization, project, firstEnvironment, secondEnvironment);
        await db.SaveChangesAsync();
        const string secret = "test-only-enrollment-secret-0123456789abcdef";
        var settings = new NodeEnrollmentSettings([
            new NodeEnrollment(firstEnvironment.Id, "first", NodeIdentityService.Hash(secret)),
            new NodeEnrollment(firstEnvironment.Id, "second", NodeIdentityService.Hash(secret))]);
        var registry = new NodeRegistry(db, new NodeIdentityService(db, settings));
        var instance = Guid.NewGuid();
        var schemas = new[] { "2.0", "2.1", "2.2", "2.3", "2.4" };
        var capabilities = new[] { "security-ledger-v1" };
        var first = await registry.RegisterAsync(new RegisterNodeRequest(firstEnvironment.Id, "first", instance, "test", schemas, capabilities), secret);
        var second = await registry.RegisterAsync(new RegisterNodeRequest(firstEnvironment.Id, "second", Guid.NewGuid(), "test", schemas, capabilities), secret);
        var firstNode = await db.Set<GatewayNode>().SingleAsync(x => x.Id == first.NodeId);
        var secondNode = await db.Set<GatewayNode>().SingleAsync(x => x.Id == second.NodeId);
        secondNode.Enabled = false;
        await db.SaveChangesAsync();
        var operation = Guid.NewGuid(); var generation = Guid.NewGuid(); var challenge = Guid.NewGuid(); var lease = Guid.NewGuid();
        var hash = new string('a', 64); var now = DateTimeOffset.UtcNow; now = now.AddTicks(-(now.Ticks % 10)); var expiry = now.AddSeconds(20);
        var receiptAt = prematureReceipt ? now.AddSeconds(-1) : now;

        // Synthetic, locally scoped facts test status coverage only; they are not
        // evidence of a verified signature, lease issuer or running gateway.
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
        try
        {
            db.Add(new EnvironmentSecurityState { EnvironmentId = firstEnvironment.Id, OrganizationId = organization.Id, ProjectId = project.Id,
                SecurityGeneration = generation, Epoch = 1, HeadHash = hash, MinimumSnapshotSchema = "2.4", UpdatedAt = now });
            db.Add(new SecurityOperation { Id = operation, OrganizationId = organization.Id, Kind = "EnvironmentPolicyCutover", RequestedBy = Guid.NewGuid(), Reason = "synthetic", CommittedAt = now });
            db.Add(new SecurityEvent { EnvironmentId = firstEnvironment.Id, OrganizationId = organization.Id, ProjectId = project.Id, SecurityGeneration = generation,
                Epoch = 1, OperationId = operation, EventType = "EnvironmentPolicyChanged", PolicyJson = "{\"requireEnvironmentCredentials\":false,\"minimumSnapshotSchema\":\"2.4\"}", PreviousHash = hash, EventHash = hash, OccurredAt = now });
            db.Add(new SecurityOperationTarget { OperationId = operation, EnvironmentId = firstEnvironment.Id, NodeId = first.NodeId, InstanceGuid = instance,
                ChallengeNonce = challenge, CreatedAt = now, ProtocolSupportedAtFreeze = true });
            db.Add(new GatewaySecurityLease { NodeId = first.NodeId, InstanceId = instance, EnvironmentId = firstEnvironment.Id, OrganizationId = organization.Id,
                ProjectId = project.Id, SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, LeaseId = lease, IssuedAt = now, FreshUntil = expiry,
                LastIssuedLeaseUntil = expiry, SigningKeyId = "synthetic", LeaseSeconds = 30 });
            db.Add(new GatewaySecurityCurrentReceipt { NodeId = first.NodeId, InstanceId = instance, EnvironmentId = firstEnvironment.Id, OrganizationId = organization.Id,
                ProjectId = project.Id, SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, HeadHash = hash, LeaseId = lease, FreshUntil = expiry, ReceivedAt = receiptAt });
            db.Add(new SecurityAckRecord { OperationId = operation, EnvironmentId = firstEnvironment.Id, NodeId = first.NodeId, InstanceGuid = instance,
                SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, ChallengeNonce = challenge, AppliedAt = now, ReceivedAt = now, Success = true, Durable = true, AdmissionEnforced = true });
            await db.SaveChangesAsync();
        }
        finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        firstNode.Metadata = SecurityApplicationReceipt.Merge(firstNode.Metadata, new(first.NodeId, firstEnvironment.Id, instance, generation, 1, hash, now, hash, expiry, receiptAt, lease));
        await db.SaveChangesAsync();
        var reader = new SecurityOperationStatusReader(db);
        // A legacy receipt with no exact issuance fact must not survive an
        // upgrade as proof of a fully applied operation.
        Assert.Equal("Unconfirmed", (await reader.ReadAsync(operation)).Status);
        db.Add(new GatewaySecurityLeaseIssuance { LeaseId = lease, NodeId = first.NodeId, InstanceId = instance, EnvironmentId = firstEnvironment.Id,
            OrganizationId = organization.Id, ProjectId = project.Id, SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash,
            PayloadHash = new string('b', 64), IssuedAt = now, FreshUntil = expiry, SigningKeyId = "synthetic" });
        await db.SaveChangesAsync();
        Assert.Equal("Unconfirmed", (await reader.ReadAsync(operation)).Status);
        db.Add(new SecurityAckVerification { OperationId = operation, EnvironmentId = firstEnvironment.Id, NodeId = first.NodeId, InstanceGuid = instance,
            ChallengeNonce = challenge, SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, LeaseId = lease, AppliedAt = now, ReceivedAt = now });
        await db.SaveChangesAsync();
        if (prematureReceipt)
        {
            Assert.Equal("Unconfirmed", (await reader.ReadAsync(operation)).Status);
            return;
        }
        Assert.Equal("FullyApplied", (await reader.ReadAsync(operation)).Status);

        if (missingCurrentNode)
        {
            // Re-enabling a registered node does not itself create a target.
            secondNode.Enabled = true;
            await db.SaveChangesAsync();
            var status = await reader.ReadAsync(operation);
            Assert.Equal("Partial", status.Status);
            Assert.Contains(status.CoverageGaps, gap => gap.EnvironmentId == firstEnvironment.Id && gap.NodeId == second.NodeId && gap.Reason == "current_node_target_missing");
            return;
        }

        var secondGeneration = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
        try
        {
            db.Add(new EnvironmentSecurityState { EnvironmentId = secondEnvironment.Id, OrganizationId = organization.Id, ProjectId = project.Id,
                SecurityGeneration = secondGeneration, Epoch = 1, HeadHash = hash, MinimumSnapshotSchema = "2.4", UpdatedAt = now });
            db.Add(new SecurityEvent { EnvironmentId = secondEnvironment.Id, OrganizationId = organization.Id, ProjectId = project.Id, SecurityGeneration = secondGeneration,
                Epoch = 1, OperationId = operation, EventType = "EnvironmentPolicyChanged", PolicyJson = "{\"requireEnvironmentCredentials\":false,\"minimumSnapshotSchema\":\"2.4\"}", PreviousHash = hash, EventHash = hash, OccurredAt = now });
            await db.SaveChangesAsync();
        }
        finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        var crossEnvironment = await reader.ReadAsync(operation);
        Assert.Equal("Partial", crossEnvironment.Status);
        Assert.Contains(crossEnvironment.CoverageGaps, gap => gap.EnvironmentId == secondEnvironment.Id && gap.Reason == "environment_has_no_targets");
    }
}

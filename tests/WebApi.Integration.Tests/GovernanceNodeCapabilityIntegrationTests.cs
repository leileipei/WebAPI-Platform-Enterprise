using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceNodeCapabilityIntegrationTests
{
    private const string Secret = "test-only-enrollment-secret-0123456789abcdef";
    private static readonly string[] Schemas = ["2.0", "2.1", "2.2", "2.3", "2.4"];
    private static readonly string[] Security = ["security-ledger-v1"];

    private static async Task<EnvironmentRecord> InitializeAsync(PostgresDatabase database)
    {
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var organization = new Organization { Code = "ORG", Name = "Organization" };
        var project = new Project { OrganizationId = organization.Id, Code = "PRJ", Name = "Project" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test" };
        db.AddRange(organization, project, environment);
        await db.SaveChangesAsync();
        return environment;
    }

    private static NodeRegistry Registry(WebApiDbContext db, Guid environmentId, string name)
    {
        var enrollment = new NodeEnrollment(environmentId, name, NodeIdentityService.Hash(Secret));
        return new NodeRegistry(db, new NodeIdentityService(db, new NodeEnrollmentSettings([enrollment])));
    }

    [Fact]
    public async Task RegistrationAndHeartbeatBindBothDeclarationsToTheAuthenticatedInstance()
    {
        await using var database = new PostgresDatabase();
        var environment = await InitializeAsync(database);
        await using var db = database.Context();
        var registry = Registry(db, environment.Id, "first");
        var instance = Guid.NewGuid();
        var registered = await registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", instance, "test", Schemas, Security), Secret);
        var node = await db.Set<GatewayNode>().SingleAsync(n => n.Id == registered.NodeId);
        SnapshotSchemaCapabilities.RequireSupported([node], "2.4");

        var identity = new NodeIdentity(node.Id, environment.Id, node.IdentityHash);
        await registry.HeartbeatAsync(identity, new NodeHeartbeat(instance, 0, 0, "NotReady", Schemas, []));
        Assert.False(SecurityProtocolCapabilities.SupportsLedger(node));
        Assert.Throws<ApiException>(() => SnapshotSchemaCapabilities.RequireSupported([node], "2.4"));
        await registry.HeartbeatAsync(identity, new NodeHeartbeat(instance, 0, 0, "NotReady", Schemas, Security));
        SnapshotSchemaCapabilities.RequireSupported([node], "2.4");
    }

    [Fact]
    public async Task ReplacementClearsRuntimeReceiptAndEndsExistingMainlineCollector()
    {
        await using var database = new PostgresDatabase();
        var environment = await InitializeAsync(database);
        await using var db = database.Context();
        var registry = Registry(db, environment.Id, "first");
        var instance = Guid.NewGuid();
        var registered = await registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", instance, "test", Schemas, Security), Secret);
        var node = await db.Set<GatewayNode>().SingleAsync(n => n.Id == registered.NodeId);
        var metadata = JsonNode.Parse(node.Metadata!)!.AsObject();
        metadata["runtimeApplication"] = new JsonObject { ["instanceId"] = instance.ToString() };
        metadata["securityApplication"] = new JsonObject { ["instanceId"] = instance.ToString() };
        node.Metadata = metadata.ToJsonString();
        var collectorId = Guid.NewGuid();
        db.Add(new CredentialUsageCollector { NodeId = node.Id, CollectorId = collectorId, EnvironmentId = environment.Id });
        db.Add(new CredentialUsageCollectorInstance { NodeId = node.Id, CollectorId = collectorId, InstanceId = instance });
        await db.SaveChangesAsync();

        var replacement = Guid.NewGuid();
        var identity = new NodeIdentity(node.Id, environment.Id, node.IdentityHash);
        await registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", replacement, "test"), Secret);
        db.ChangeTracker.Clear();
        var current = await db.Set<GatewayNode>().SingleAsync(n => n.Id == registered.NodeId);
        Assert.Equal(replacement.ToString(), current.InstanceId);
        Assert.Equal(new[] { "2.0" }, SnapshotSchemaCapabilities.Read(current));
        Assert.False(SecurityProtocolCapabilities.SupportsLedger(current));
        using var parsed = JsonDocument.Parse(current.Metadata!);
        Assert.False(parsed.RootElement.TryGetProperty("runtimeApplication", out _));
        Assert.False(parsed.RootElement.TryGetProperty("securityApplication", out _));
        Assert.NotNull((await db.Set<CredentialUsageCollectorInstance>().SingleAsync()).EndedAt);
        await Assert.ThrowsAsync<ApiException>(() => registry.HeartbeatAsync(identity, new NodeHeartbeat(instance, 0, 0, "NotReady", Schemas, Security)));
    }

    [Fact]
    public async Task RetiredInstanceCannotRegisterAgainEvenWithoutSecurityOperations()
    {
        await using var database = new PostgresDatabase();
        var environment = await InitializeAsync(database);
        await using var db = database.Context();
        var registry = Registry(db, environment.Id, "first");
        var first = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        var node = await registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", first, "test", Schemas, Security), Secret);
        await registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", replacement, "test", Schemas, Security), Secret);
        var error = await Assert.ThrowsAsync<ApiException>(() => registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", first, "test", Schemas, Security), Secret));
        Assert.Equal(409, error.Status);
        db.ChangeTracker.Clear();
        Assert.Equal(replacement.ToString(), (await db.Set<GatewayNode>().SingleAsync(x => x.Id == node.NodeId)).InstanceId);
    }

    [Fact]
    public async Task TwoOnlineNodesAreRequiredAndEveryEnabledNodeMustBeCapable()
    {
        await using var database = new PostgresDatabase();
        var environment = await InitializeAsync(database);
        await using var db = database.Context();
        var settings = new PublishSettings();
        var first = await Registry(db, environment.Id, "first").RegisterAsync(new RegisterNodeRequest(environment.Id, "first", Guid.NewGuid(), "test", Schemas, Security), Secret);
        await Assert.ThrowsAsync<ApiException>(() => SnapshotSchemaCapabilities.RequireOnlineSchemaAsync(db, settings, environment.Id, "2.4", default));
        await Assert.ThrowsAsync<ApiException>(() => SnapshotSchemaCapabilities.RequireOnlineSchemaAsync(db, new PublishSettings(MinimumNodes: 1), environment.Id, "2.4", default));
        await Registry(db, environment.Id, "second").RegisterAsync(new RegisterNodeRequest(environment.Id, "second", Guid.NewGuid(), "test", Schemas, Security), Secret);
        await SnapshotSchemaCapabilities.RequireOnlineSchemaAsync(db, settings, environment.Id, "2.4", default);

        var third = await Registry(db, environment.Id, "third").RegisterAsync(new RegisterNodeRequest(environment.Id, "third", Guid.NewGuid(), "test", ["2.0", "2.3"]), Secret);
        await Assert.ThrowsAsync<ApiException>(() => SnapshotSchemaCapabilities.RequireOnlineSchemaAsync(db, settings, environment.Id, "2.4", default));
        var thirdNode = await db.Set<GatewayNode>().SingleAsync(n => n.Id == third.NodeId);
        thirdNode.Enabled = false;
        await db.SaveChangesAsync();
        await SnapshotSchemaCapabilities.RequireOnlineSchemaAsync(db, settings, environment.Id, "2.4", default);

        var firstNode = await db.Set<GatewayNode>().SingleAsync(n => n.Id == first.NodeId);
        firstNode.LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ApiException>(() => SnapshotSchemaCapabilities.RequireOnlineSchemaAsync(db, settings, environment.Id, "2.4", default));
    }

    [Fact]
    public async Task SecurityOperationStatusOwnsAFreshTransaction()
    {
        await using var database = new PostgresDatabase();
        await InitializeAsync(database);
        await using var db = database.Context();
        var reader = new SecurityOperationStatusReader(db);
        Assert.Equal("Unconfirmed", (await reader.ReadAsync(Guid.NewGuid())).Status);
        Assert.Null(db.Database.CurrentTransaction);
        await using (var weak = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(Guid.NewGuid()));
            await weak.RollbackAsync();
        }
        await using (var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(Guid.NewGuid()));
            await snapshot.RollbackAsync();
        }
    }

    [Theory]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, true, true, true, true)]
    public async Task ReplacementRetargetsSecurityOperationWithoutDiscardingTheOldAckOrLeaseHorizon(bool supportsSecurity, bool hasLeaseHistory, bool secondSupportsSecurity, bool oldLeaseExpired, bool hasIssuanceEvidence)
    {
        await using var database = new PostgresDatabase();
        var environment = await InitializeAsync(database);
        await using var db = database.Context();
        var registry = Registry(db, environment.Id, "first");
        var instance = Guid.NewGuid();
        var registered = await registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", instance, "test", Schemas, Security), Secret);
        var organization = await db.Set<Project>().Where(p => p.Id == environment.ProjectId).Select(p => p.OrganizationId).SingleAsync();
        var generation = Guid.NewGuid(); var operation = Guid.NewGuid(); var challenge = Guid.NewGuid(); var lease = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10)); // PostgreSQL timestamps preserve microseconds.
        var expiry = now.AddSeconds(20); var hash = new string('a', 64);
        var oldExpiry = oldLeaseExpired ? now.AddSeconds(-10) : expiry;
        var oldIssued = oldLeaseExpired ? oldExpiry.AddSeconds(-20) : now;

        // Synthetic facts in this temporary database exercise target bookkeeping;
        // they are not a verified security ledger or a signed runtime lease.
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environment_security_states(environment_id,organization_id,project_id,security_generation,epoch,head_hash,require_environment_credentials,minimum_snapshot_schema,lease_seconds,lease_refresh_seconds,maximum_clock_skew_seconds,revision,updated_at) VALUES({environment.Id},{organization},{environment.ProjectId},{generation},1,{hash},false,'2.4',30,5,2,1,{now})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_operations(id,organization_id,kind,requested_by,reason,committed_at,revision) VALUES({operation},{organization},'EnvironmentPolicyCutover',{Guid.NewGuid()},'synthetic fixture',{now},1)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_events(environment_id,organization_id,project_id,security_generation,epoch,operation_id,event_type,policy_json,previous_hash,event_hash,occurred_at) VALUES({environment.Id},{organization},{environment.ProjectId},{generation},1,{operation},'EnvironmentPolicyChanged','{{\"requireEnvironmentCredentials\":false,\"minimumSnapshotSchema\":\"2.4\"}}'::jsonb,{hash},{hash},{now})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_operation_targets(operation_id,environment_id,node_id,instance_guid,challenge_nonce,created_at,target_state,protocol_supported_at_freeze) VALUES({operation},{environment.Id},{registered.NodeId},{instance},{challenge},{now},'Pending',true)");
            if (hasLeaseHistory)
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO gateway_security_leases(node_id,instance_id,environment_id,organization_id,project_id,security_generation,epoch,checkpoint_hash,lease_id,issued_at,fresh_until,last_issued_lease_until,signing_key_id,lease_seconds,revision) VALUES({registered.NodeId},{instance},{environment.Id},{organization},{environment.ProjectId},{generation},1,{hash},{lease},{oldIssued},{oldExpiry},{oldExpiry},'fixture',30,1)");
            if (hasLeaseHistory && hasIssuanceEvidence)
                db.Add(new GatewaySecurityLeaseIssuance { LeaseId = lease, NodeId = registered.NodeId, InstanceId = instance, EnvironmentId = environment.Id,
                    OrganizationId = organization, ProjectId = environment.ProjectId, SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash,
                    PayloadHash = new string('c', 64), IssuedAt = oldIssued, FreshUntil = oldExpiry, SigningKeyId = "fixture" });
            if (hasLeaseHistory && hasIssuanceEvidence) await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_acks(operation_id,environment_id,node_id,instance_guid,security_generation,epoch,checkpoint_hash,challenge_nonce,applied_at,received_at,success,durable,admission_enforced) VALUES({operation},{environment.Id},{registered.NodeId},{instance},{generation},1,{hash},{challenge},{now},{now},true,true,true)");
        }
        finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        var replacement = Guid.NewGuid();
        var advertisedSecurity = supportsSecurity ? Security : Array.Empty<string>();
        if (!supportsSecurity)
        {
            var rejected = await Assert.ThrowsAsync<ApiException>(() => registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", replacement, "test", Schemas, advertisedSecurity), Secret));
            Assert.Equal(409, rejected.Status);
            db.ChangeTracker.Clear();
            Assert.Equal(instance.ToString(), (await db.Set<GatewayNode>().SingleAsync(x => x.Id == registered.NodeId)).InstanceId);
            Assert.Equal(1, await db.Set<SecurityOperationTarget>().CountAsync(x => x.OperationId == operation));
            return;
        }
        await registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", replacement, "test", Schemas, advertisedSecurity), Secret);
        db.ChangeTracker.Clear();
        var replay = await Assert.ThrowsAsync<ApiException>(() => registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", instance, "test", Schemas, Security), Secret));
        Assert.Equal(409, replay.Status);
        db.ChangeTracker.Clear();
        var targets = await db.Set<SecurityOperationTarget>().Where(t => t.OperationId == operation).ToArrayAsync();
        Assert.Equal(2, targets.Length);
        var previous = Assert.Single(targets, t => t.InstanceGuid == instance);
        var next = Assert.Single(targets, t => t.InstanceGuid == replacement);
        Assert.Equal(replacement, previous.SupersededByInstanceGuid);
        var retainedLease = await db.Set<GatewaySecurityLease>().AsNoTracking().SingleOrDefaultAsync(l => l.NodeId == registered.NodeId && l.InstanceId == instance);
        if (hasLeaseHistory) Assert.Equal(retainedLease!.LastIssuedLeaseUntil, previous.LastLeaseUntil);
        else { Assert.Null(retainedLease); Assert.Null(previous.LastLeaseUntil); }
        Assert.Equal(supportsSecurity, next.ProtocolSupportedAtFreeze);
        Assert.NotEqual(challenge, next.ChallengeNonce);
        Assert.Single(await db.Set<SecurityAckRecord>().Where(a => a.OperationId == operation && a.InstanceGuid == instance).ToArrayAsync());
        var statusReader = new SecurityOperationStatusReader(db);
        Assert.Equal("Unconfirmed", (await statusReader.ReadAsync(Guid.NewGuid())).Status);
        Assert.Equal("Unconfirmed", (await statusReader.ReadAsync(operation)).Status);
        var current = await db.Set<GatewayNode>().SingleAsync(n => n.Id == registered.NodeId);
        current.Metadata = SecurityApplicationReceipt.Merge(current.Metadata, new(registered.NodeId, environment.Id, replacement, generation, 1, hash, now));
        await db.SaveChangesAsync();
        Assert.Equal("Unconfirmed", (await statusReader.ReadAsync(operation)).Status);
        if (supportsSecurity)
        {
            db.Add(new SecurityAckRecord { OperationId = operation, EnvironmentId = environment.Id, NodeId = registered.NodeId, InstanceGuid = replacement,
                SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, ChallengeNonce = next.ChallengeNonce,
                AppliedAt = now, ReceivedAt = now, Success = true, Durable = true, AdmissionEnforced = true });
            await db.SaveChangesAsync();
            // A target ACK and self-reported metadata do not independently prove
            // that an authenticated lease/checkpoint was ever accepted.
            Assert.Equal("Unconfirmed", (await statusReader.ReadAsync(operation)).Status);
            var replacementLease = Guid.NewGuid();
            db.Add(new GatewaySecurityLease { NodeId = registered.NodeId, InstanceId = replacement, EnvironmentId = environment.Id,
                OrganizationId = organization, ProjectId = environment.ProjectId, SecurityGeneration = generation, Epoch = 1,
                CheckpointHash = hash, LeaseId = replacementLease, IssuedAt = now, FreshUntil = expiry, LastIssuedLeaseUntil = expiry,
                SigningKeyId = "synthetic", LeaseSeconds = 30 });
            db.Add(new GatewaySecurityCurrentReceipt { NodeId = registered.NodeId, InstanceId = replacement, EnvironmentId = environment.Id,
                OrganizationId = organization, ProjectId = environment.ProjectId, SecurityGeneration = generation, Epoch = 1,
                CheckpointHash = hash, HeadHash = hash, LeaseId = replacementLease, FreshUntil = expiry, ReceivedAt = now });
            db.Add(new GatewaySecurityLeaseIssuance { LeaseId = replacementLease, NodeId = registered.NodeId, InstanceId = replacement, EnvironmentId = environment.Id,
                OrganizationId = organization, ProjectId = environment.ProjectId, SecurityGeneration = generation, Epoch = 1,
                CheckpointHash = hash, PayloadHash = new string('d', 64), IssuedAt = now, FreshUntil = expiry, SigningKeyId = "synthetic" });
            db.Add(new SecurityAckVerification { OperationId = operation, EnvironmentId = environment.Id, NodeId = registered.NodeId, InstanceGuid = replacement,
                ChallengeNonce = next.ChallengeNonce, SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, LeaseId = replacementLease,
                AppliedAt = now, ReceivedAt = now });
            current.Metadata = SecurityApplicationReceipt.Merge(current.Metadata, new(registered.NodeId, environment.Id, replacement, generation, 1, hash, now, hash, expiry, now, replacementLease));
            await db.SaveChangesAsync();
            var storedProof = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync(r => r.NodeId == registered.NodeId && r.InstanceId == replacement);
            Assert.True(SecurityApplicationReceipt.MatchesAccepted(SecurityApplicationReceipt.Read(current)!, storedProof));
            var confirmed = await statusReader.ReadAsync(operation);
            // One recorded issuance at the current horizon cannot prove that
            // an older restored or missing history has no longer-lived lease.
            Assert.Equal("Partial", confirmed.Status);
            Assert.DoesNotContain(confirmed.Targets, target => target.InstanceGuid == instance && target.State == "SafeSuperseded");
            current.Metadata = SnapshotSchemaCapabilities.Merge(current.Metadata, replacement, ["2.0", "2.3"]);
            await db.SaveChangesAsync();
            Assert.Equal("Unconfirmed", (await statusReader.ReadAsync(operation)).Status);
            current.Metadata = SnapshotSchemaCapabilities.Merge(current.Metadata, replacement, Schemas);
            await db.SaveChangesAsync();
            if (!hasLeaseHistory)
            {
                // Simulate damaged/restored history: an expired horizon without
                // a corresponding issued-lease record is not isolation proof.
                var forgedExpiry = now.AddSeconds(-30);
                await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
                try { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE security_operation_targets SET last_lease_until={forgedExpiry} WHERE operation_id={operation} AND node_id={registered.NodeId} AND instance_guid={instance}"); }
                finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
                var damaged = await statusReader.ReadAsync(operation);
                Assert.Equal("Partial", damaged.Status);
                Assert.Contains(damaged.Targets, target => target.InstanceGuid == instance && target.State != "SafeSuperseded");
            }
        }
        await registry.RegisterAsync(new RegisterNodeRequest(environment.Id, "first", replacement, "test", Schemas, advertisedSecurity), Secret);
        Assert.Equal(2, await db.Set<SecurityOperationTarget>().CountAsync(t => t.OperationId == operation));

        // A new enabled node cannot be silently omitted from an already
        // committed security operation for this environment.
        var secondInstance = Guid.NewGuid();
        if (!secondSupportsSecurity)
        {
            var rejected = await Assert.ThrowsAsync<ApiException>(() => Registry(db, environment.Id, "second").RegisterAsync(
                new RegisterNodeRequest(environment.Id, "second", secondInstance, "test", Schemas, []), Secret));
            Assert.Equal(409, rejected.Status);
            db.ChangeTracker.Clear();
            Assert.False(await db.Set<GatewayNode>().AnyAsync(node => node.EnvironmentId == environment.Id && node.NodeName == "second"));
            return;
        }
        var second = await Registry(db, environment.Id, "second").RegisterAsync(
            new RegisterNodeRequest(environment.Id, "second", secondInstance, "test", Schemas, secondSupportsSecurity ? Security : []), Secret);
        var secondTarget = await db.Set<SecurityOperationTarget>().SingleAsync(t =>
            t.OperationId == operation && t.NodeId == second.NodeId && t.InstanceGuid == secondInstance);
        Assert.Equal(secondSupportsSecurity, secondTarget.ProtocolSupportedAtFreeze);
        Assert.NotEqual(challenge, secondTarget.ChallengeNonce);
        Assert.Single(await db.Set<GatewayNodeEvent>().Where(e => e.GatewayNodeId == second.NodeId && e.EventType == "security_targets_added").ToArrayAsync());
        Assert.NotEqual("FullyApplied", (await statusReader.ReadAsync(operation)).Status);
        await Registry(db, environment.Id, "second").RegisterAsync(
            new RegisterNodeRequest(environment.Id, "second", secondInstance, "test", Schemas, secondSupportsSecurity ? Security : []), Secret);
        Assert.Equal(1, await db.Set<SecurityOperationTarget>().CountAsync(t => t.OperationId == operation && t.NodeId == second.NodeId));
        Assert.Equal(1, await db.Set<GatewayNodeEvent>().CountAsync(e => e.GatewayNodeId == second.NodeId && e.EventType == "security_targets_added"));
    }
}

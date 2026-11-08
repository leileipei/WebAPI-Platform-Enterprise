using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernanceCurrentSecurityProofTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("bad-signature")]
    [InlineData("wrong-instance")]
    [InlineData("no-issued-lease")]
    [InlineData("expired-lease")]
    [InlineData("not-durable")]
    [InlineData("unsupported-capability")]
    [InlineData("incorrect-ledger-head")]
    [InlineData("incorrect-ledger-floor")]
    [InlineData("lease-outlives-checkpoint")]
    [InlineData("checkpoint-expired-lease-fresh")]
    [InlineData("dirty-context")]
    [InlineData("stale-node")]
    [InlineData("unissued-lease-id")]
    [InlineData("altered-lease-claims")]
    [InlineData("no-issuance-evidence")]
    [InlineData("applied-at-expiry")]
    [InlineData("future-issued-lease")]
    [InlineData("public-clock-shadow")]
    public async Task CurrentProofNeedsSignedAuthorityAndCurrentIssuedLease(string scenario)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var clock = new FrozenSecurityClockFixture();
        await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(clock).Options);
        await db.Database.MigrateAsync();
        var organization = new Organization { Code = "PROOF-ORG", Name = "Organization" };
        var project = new Project { OrganizationId = organization.Id, Code = "PROOF-PRJ", Name = "Project" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test" };
        db.AddRange(organization, project, environment);
        db.Add(new EnvironmentSecurityInitialization { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
            State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var generation = Guid.NewGuid();
        db.Add(new EnvironmentSecurityState { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
            SecurityGeneration = generation, Epoch = 0, HeadHash = SecurityEventHash.Genesis, InitialRequireEnvironmentCredentials = false,
            RequireEnvironmentCredentials = false, MinimumSnapshotSchema = "2.0", UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        const string secret = "test-only-enrollment-secret-0123456789abcdef";
        var enrollment = new NodeEnrollmentSettings([new NodeEnrollment(environment.Id, "first", NodeIdentityService.Hash(secret))]);
        var identities = new NodeIdentityService(db, enrollment);
        var instance = Guid.NewGuid();
        var registered = await new NodeRegistry(db, identities).RegisterAsync(
            new RegisterNodeRequest(environment.Id, "first", instance, "test", ["2.0", "2.1", "2.2", "2.3", "2.4"], ["security-ledger-v1"]), secret);
        var identity = new NodeIdentity(registered.NodeId, environment.Id, NodeIdentityService.Hash(secret));
        if (scenario == "unsupported-capability")
        {
            var unsupportedNode = await db.Set<GatewayNode>().SingleAsync(x => x.Id == registered.NodeId);
            unsupportedNode.Metadata = SecurityProtocolCapabilities.Merge(unsupportedNode.Metadata, instance, []);
            await db.SaveChangesAsync();
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new SecurityCheckpointSigner("test-key", key.ExportPkcs8PrivateKeyPem());
        var settings = new SecuritySigningSettings(null, null, new Dictionary<string, string> { ["test-key"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        var now = DateTimeOffset.UtcNow; now = now.AddTicks(-(now.Ticks % 10));
        var issued = scenario == "expired-lease" ? now.AddSeconds(-40) : scenario == "applied-at-expiry" ? now.AddSeconds(-18) : scenario == "future-issued-lease" ? now.AddMilliseconds(1800) : scenario == "public-clock-shadow" ? now.AddMinutes(5) : now.AddSeconds(-1);
        var expires = issued.AddSeconds(20);
        var checkpointIssued = scenario == "checkpoint-expired-lease-fresh" ? now.AddSeconds(-25) : issued;
        var checkpointExpires = scenario switch
        {
            "lease-outlives-checkpoint" => now.AddSeconds(5),
            "checkpoint-expired-lease-fresh" => now.AddSeconds(-5),
            _ => expires
        };
        var checkpointPayload = new SecurityCheckpointPayload(SecuritySignedEncoding.Protocol, environment.Id, generation, 0,
            scenario == "incorrect-ledger-head" ? new string('f', 64) : SecurityEventHash.Genesis, [], false,
            scenario == "incorrect-ledger-floor" ? "2.4" : "2.0", checkpointIssued, checkpointExpires, "test-key");
        var checkpoint = signer.SignCheckpoint(checkpointPayload);
        var hash = SecuritySignedEncoding.CheckpointHash(checkpointPayload);
        var leaseId = Guid.NewGuid();
        var leasePayload = new SecurityNodeLeasePayload(leaseId, SecuritySignedEncoding.Protocol, environment.Id, generation, 0, hash,
            registered.NodeId, instance, SecuritySignedEncoding.Audience, issued, expires, "test-key");
        var signedLease = signer.SignNodeLease(leasePayload);
        if (scenario != "no-issued-lease")
        {
            var issuedPayload = scenario == "unissued-lease-id" ? leasePayload with { LeaseId = Guid.NewGuid() } : leasePayload;
            db.Add(new GatewaySecurityLease { NodeId = registered.NodeId, InstanceId = instance, EnvironmentId = environment.Id,
                OrganizationId = organization.Id, ProjectId = project.Id, SecurityGeneration = generation, Epoch = 0, CheckpointHash = hash,
                LeaseId = issuedPayload.LeaseId, IssuedAt = issued, FreshUntil = expires, LastIssuedLeaseUntil = expires, SigningKeyId = "test-key", LeaseSeconds = 30 });
            if (scenario != "no-issuance-evidence") db.Add(SecurityLeaseIssuanceEvidence.Create(issuedPayload, organization.Id, project.Id));
            if (scenario is "future-issued-lease" or "public-clock-shadow")
            {
                // Simulate a future-dated fact left by a legacy writer before
                // this guard existed. The regular insert guard is exercised
                // separately; bypass only that trigger in this isolated DB.
                await db.Database.OpenConnectionAsync();
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.gateway_security_lease_issuances DISABLE TRIGGER security_issuance_not_future");
                try { await db.SaveChangesAsync(); }
                finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.gateway_security_lease_issuances ENABLE TRIGGER security_issuance_not_future"); }
            }
            else await db.SaveChangesAsync();
        }
        if (scenario == "altered-lease-claims") signedLease = signer.SignNodeLease(leasePayload with { MaximumClockSkewSeconds = 1 });
        if (scenario == "bad-signature")
        {
            var body = JsonNode.Parse(checkpoint)!.AsObject();
            body["signature"] = Convert.ToBase64String(new byte[64]);
            checkpoint = Encoding.UTF8.GetBytes(body.ToJsonString());
        }
        if (scenario == "future-issued-lease") clock.Freeze(now);
        // An untrusted schema must not replace the database clock with a
        // future value and make a future lease appear valid.
        if (scenario == "public-clock-shadow") await FrozenSecurityClockFixture.InstallShadowAsync(db, issued.AddSeconds(1));
        var request = new CurrentSecurityProof(scenario == "wrong-instance" ? Guid.NewGuid() : instance, checkpoint, signedLease, scenario == "applied-at-expiry" ? expires : scenario == "public-clock-shadow" ? issued.AddSeconds(1) : now,
            scenario != "not-durable", true);
        var service = new CurrentSecurityProofService(db, identities, new SecurityLedgerReader(db), settings);
        if (scenario == "dirty-context")
        {
            var tracked = await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == environment.Id);
            tracked.Name = "unsaved caller change";
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync(registered.NodeId, request, identity));
            Assert.True(db.ChangeTracker.HasChanges());
            await using var observer = database.Context();
            Assert.Equal("Test", (await observer.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(x => x.Id == environment.Id)).Name);
            Assert.Empty(await observer.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario == "stale-node")
        {
            // Prime this context with a previously valid tracked node, then
            // revoke its security capability using an independent context.
            _ = await db.Set<GatewayNode>().SingleAsync(x => x.Id == registered.NodeId);
            await using var observer = database.Context();
            var current = await observer.Set<GatewayNode>().SingleAsync(x => x.Id == registered.NodeId);
            current.Metadata = SecurityProtocolCapabilities.Merge(current.Metadata, instance, []);
            await observer.SaveChangesAsync();
        }
        if (scenario == "valid") await service.RecordAsync(registered.NodeId, request, identity);
        else Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, request, identity))).Status);
        if (scenario == "future-issued-lease") Assert.True(clock.HitCount > 0);

        db.ChangeTracker.Clear();
        var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleOrDefaultAsync();
        var node = await db.Set<GatewayNode>().AsNoTracking().SingleAsync(x => x.Id == registered.NodeId);
        if (scenario == "valid")
        {
            Assert.NotNull(accepted);
            Assert.True(SecurityApplicationReceipt.MatchesAccepted(SecurityApplicationReceipt.Read(node)!, accepted));
        }
        else
        {
            Assert.Null(accepted);
            Assert.Null(SecurityApplicationReceipt.Read(node));
        }
    }
}

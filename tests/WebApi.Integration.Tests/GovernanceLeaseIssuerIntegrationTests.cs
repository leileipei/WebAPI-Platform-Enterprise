using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
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

public sealed class GovernanceLeaseIssuerIntegrationTests
{
    [Fact]
    public async Task PublicIssuerWithoutIndependentEvidenceDoesNotPersistSecurityStateOrLease()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var issuer = new SecurityLeaseIssuer(db, null!, new SecurityLedgerReader(db), new SecuritySigningSettings(null, null));
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            issuer.IssueAsync(new NodeIdentity(Guid.NewGuid(), Guid.NewGuid(), "test"), Guid.NewGuid()));

        Assert.Equal(503, error.Status);
        Assert.Equal("security_evidence_unavailable", error.Code);
        Assert.Empty(await db.Set<EnvironmentSecurityState>().AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.Set<GatewaySecurityLease>().AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task IssuanceCannotCombineNodeInstanceWithAnotherEnvironment()
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        await using var db = database.Context();
        await db.Database.MigrateAsync();
        var organization = new Organization { Code = "ISSUE-SCOPE-ORG", Name = "Organization" };
        var project = new Project { OrganizationId = organization.Id, Code = "ISSUE-SCOPE-P", Name = "Project" };
        var first = new EnvironmentRecord { ProjectId = project.Id, Code = "FIRST", Name = "First" };
        var second = new EnvironmentRecord { ProjectId = project.Id, Code = "SECOND", Name = "Second" };
        var instance = Guid.NewGuid(); var firstGeneration = Guid.NewGuid(); var secondGeneration = Guid.NewGuid();
        var node = new GatewayNode { EnvironmentId = first.Id, NodeName = "first", InstanceId = instance.ToString(), IdentityHash = new string('a', 64) };
        db.AddRange(organization, project, first, second, node,
            new EnvironmentSecurityInitialization { EnvironmentId = first.Id, ProjectId = project.Id, OrganizationId = organization.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() },
            new EnvironmentSecurityInitialization { EnvironmentId = second.Id, ProjectId = project.Id, OrganizationId = organization.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        var at = DateTimeOffset.UtcNow; at = at.AddTicks(-(at.Ticks % 10));
        db.AddRange(new EnvironmentSecurityState { EnvironmentId = first.Id, ProjectId = project.Id, OrganizationId = organization.Id, SecurityGeneration = firstGeneration,
                InitialRequireEnvironmentCredentials = false, RequireEnvironmentCredentials = false, UpdatedAt = at },
            new EnvironmentSecurityState { EnvironmentId = second.Id, ProjectId = project.Id, OrganizationId = organization.Id, SecurityGeneration = secondGeneration,
                InitialRequireEnvironmentCredentials = false, RequireEnvironmentCredentials = false, UpdatedAt = at });
        await db.SaveChangesAsync();
        db.Add(new GatewaySecurityLease { NodeId = node.Id, InstanceId = instance, EnvironmentId = first.Id, ProjectId = project.Id, OrganizationId = organization.Id,
            SecurityGeneration = firstGeneration, Epoch = 0, CheckpointHash = new string('b', 64), LeaseId = Guid.NewGuid(), IssuedAt = at,
            FreshUntil = at.AddSeconds(20), LastIssuedLeaseUntil = at.AddSeconds(20), SigningKeyId = "test-key", LeaseSeconds = 30 });
        await db.SaveChangesAsync();
        db.Add(new GatewaySecurityLeaseIssuance { LeaseId = Guid.NewGuid(), NodeId = node.Id, InstanceId = instance, EnvironmentId = second.Id,
            ProjectId = project.Id, OrganizationId = organization.Id, SecurityGeneration = secondGeneration, Epoch = 0, CheckpointHash = new string('b', 64),
            PayloadHash = new string('c', 64), IssuedAt = at, FreshUntil = at.AddSeconds(20), SigningKeyId = "test-key" });
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains(((PostgresException)failure.InnerException!).SqlState, new[] { "23503", "23514" });
        await using var observer = database.Context();
        Assert.Empty(await observer.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("public-unavailable")]
    [InlineData("valid")]
    [InlineData("duplicate-timestamp")]
    [InlineData("known-expiry-clock-regression")]
    [InlineData("cross-environment-clock-regression")]
    [InlineData("later-issuance-clock-regression")]
    [InlineData("issuer-cross-expiry-read")]
    [InlineData("future-issuance-forgery")]
    [InlineData("unknown-origin")]
    [InlineData("missing-origin")]
    [InlineData("governed-origin")]
    [InlineData("empty-proof-id")]
    [InlineData("missing-signing-key")]
    [InlineData("malformed-signing-key")]
    [InlineData("conflicting-trusted-key")]
    [InlineData("missing-capability")]
    [InlineData("wrong-instance")]
    [InlineData("disabled-environment")]
    [InlineData("inconsistent-ledger")]
    [InlineData("dirty-context")]
    [InlineData("issuance-persist-failure")]
    public async Task UntrustedGenesisAlgorithmCharacterizationRequiresOriginCurrentIdentityAndSigning(string scenario)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var clock = new FrozenSecurityClockFixture();
        var advanceClock = new AdvanceWhenSecurityStateRead(clock);
        await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(clock, advanceClock).Options);
        await db.Database.MigrateAsync();
        var organization = new Organization { Code = "LEASE-ORG", Name = "Organization" };
        var project = new Project { OrganizationId = organization.Id, Code = "LEASE-PRJ", Name = "Project" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test" };
        var originState = scenario == "unknown-origin" ? "Unknown" : "FreshNonStrict";
        var proofId = scenario == "unknown-origin" ? (Guid?)null : Guid.NewGuid();
        var initialization = new EnvironmentSecurityInitialization { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
            State = originState, FreshProofId = proofId };
        db.AddRange(organization, project, environment);
        if (scenario != "missing-origin") db.Add(initialization);
        await db.SaveChangesAsync();
        if (scenario == "governed-origin")
        {
            db.Add(new EnvironmentSecurityState { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
                InitialRequireEnvironmentCredentials = false, RequireEnvironmentCredentials = false, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            initialization.State = "Governed"; initialization.Revision++;
            await db.SaveChangesAsync();
        }
        if (scenario == "empty-proof-id")
        {
            // Deliberately corrupt only this disposable test database to exercise
            // the reader's independent defense against invalid restored data.
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try { await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE environment_security_initializations SET fresh_proof_id={Guid.Empty},revision=revision+1 WHERE environment_id={environment.Id}"); }
            finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
            db.ChangeTracker.Clear();
        }
        const string secret = "test-only-enrollment-secret-0123456789abcdef";
        var identities = new NodeIdentityService(db, new NodeEnrollmentSettings([new NodeEnrollment(environment.Id, "first", NodeIdentityService.Hash(secret))]));
        var instance = Guid.NewGuid();
        var registered = await new NodeRegistry(db, identities).RegisterAsync(new RegisterNodeRequest(environment.Id, "first", instance, "test",
            ["2.0", "2.1", "2.2", "2.3", "2.4"], scenario == "missing-capability" ? [] : ["security-ledger-v1"]), secret);
        var identity = new NodeIdentity(registered.NodeId, environment.Id, NodeIdentityService.Hash(secret));
        if (scenario == "issuance-persist-failure")
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_test_issuance() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test_issuance_failure'; END $$; CREATE TRIGGER reject_test_issuance BEFORE INSERT ON gateway_security_lease_issuances FOR EACH ROW EXECUTE FUNCTION reject_test_issuance();");
        if (scenario == "disabled-environment") { environment.Status = "Disabled"; await db.SaveChangesAsync(); }
        if (scenario == "inconsistent-ledger")
        {
            // Deliberately seed an impossible epoch without its event; issuance
            // must preserve the damaged data and fail closed.
            var generation = Guid.NewGuid(); var hash = new string('a', 64); var at = DateTimeOffset.UtcNow;
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO environment_security_states(environment_id,organization_id,project_id,security_generation,epoch,head_hash,initial_require_environment_credentials,require_environment_credentials,minimum_snapshot_schema,lease_seconds,lease_refresh_seconds,maximum_clock_skew_seconds,revision,updated_at) VALUES({environment.Id},{organization.Id},{project.Id},{generation},1,{hash},false,false,'2.0',30,5,2,1,{at})");
            }
            finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(Path.GetTempPath(), "gov24-signing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(keyFile, scenario == "malformed-signing-key" ? "not a private key" : key.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            var configuredPublic = scenario == "conflicting-trusted-key" ? Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()) : publicKey;
            var settings = scenario == "missing-signing-key" ? new SecuritySigningSettings(null, null)
                : new SecuritySigningSettings("test-key", keyFile, new Dictionary<string, string> { ["test-key"] = configuredPublic });
            if (scenario == "public-unavailable")
            {
                var publicIssuer = new SecurityLeaseIssuer(db, identities, new SecurityLedgerReader(db), settings);
                var error = await Assert.ThrowsAsync<ApiException>(() => publicIssuer.IssueAsync(identity, instance));
                Assert.Equal(503, error.Status);
                Assert.Equal("security_evidence_unavailable", error.Code);
                Assert.Empty(await db.Set<EnvironmentSecurityState>().AsNoTracking().ToArrayAsync());
                Assert.Empty(await db.Set<GatewaySecurityLease>().AsNoTracking().ToArrayAsync());
                Assert.Empty(await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().ToArrayAsync());
                return;
            }
            // Characterizes the isolated algorithm; it does not prove a trusted
            // time source, independent history or a usable public issuer.
            var issuer = new UntrustedGenesisLeaseMaterialBuilder(db, identities, new SecurityLedgerReader(db), settings);
            if (scenario == "issuance-persist-failure")
            {
                await Assert.ThrowsAsync<DbUpdateException>(() => issuer.IssueAsync(identity, instance));
                await using var observer = database.Context();
                Assert.Empty(await observer.Set<EnvironmentSecurityState>().AsNoTracking().ToArrayAsync());
                Assert.Empty(await observer.Set<GatewaySecurityLease>().AsNoTracking().ToArrayAsync());
                Assert.Empty(await observer.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().ToArrayAsync());
                return;
            }
            if (scenario == "dirty-context")
            {
                var trackedEnvironment = await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == environment.Id);
                trackedEnvironment.Name = "unsaved local change";
                await Assert.ThrowsAsync<InvalidOperationException>(() => issuer.IssueAsync(identity, instance));
                Assert.True(db.ChangeTracker.HasChanges());
                Assert.Equal("unsaved local change", trackedEnvironment.Name);
                Assert.Empty(await db.Set<EnvironmentSecurityState>().AsNoTracking().ToArrayAsync());
                return;
            }
            if (scenario is not ("valid" or "duplicate-timestamp" or "known-expiry-clock-regression" or "cross-environment-clock-regression" or "later-issuance-clock-regression" or "issuer-cross-expiry-read" or "future-issuance-forgery"))
            {
                var error = await Assert.ThrowsAsync<ApiException>(() => issuer.IssueAsync(identity, scenario == "wrong-instance" ? Guid.NewGuid() : instance));
                Assert.Equal(scenario is "wrong-instance" or "missing-capability" ? 409 : 503, error.Status);
                var states = await db.Set<EnvironmentSecurityState>().AsNoTracking().ToArrayAsync();
                Assert.Equal(scenario is "governed-origin" or "inconsistent-ledger" ? 1 : 0, states.Length);
                Assert.Empty(await db.Set<GatewaySecurityLease>().AsNoTracking().ToArrayAsync());
                return;
            }
            if (scenario == "future-issuance-forgery")
            {
                await issuer.IssueAsync(identity, instance);
                var current = await db.Set<GatewaySecurityLease>().AsNoTracking().SingleAsync();
                var future = DateTimeOffset.UtcNow.AddMinutes(5); future = future.AddTicks(-(future.Ticks % 10));
                db.Add(new GatewaySecurityLeaseIssuance { LeaseId = Guid.NewGuid(), NodeId = current.NodeId, InstanceId = current.InstanceId,
                    EnvironmentId = current.EnvironmentId, ProjectId = current.ProjectId, OrganizationId = current.OrganizationId,
                    SecurityGeneration = current.SecurityGeneration, Epoch = current.Epoch, CheckpointHash = current.CheckpointHash,
                    PayloadHash = new string('a', 64), IssuedAt = future, FreshUntil = future.AddSeconds(20), SigningKeyId = current.SigningKeyId });
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Equal("23514", ((PostgresException)error.InnerException!).SqlState);
                return;
            }
            if (scenario == "issuer-cross-expiry-read")
            {
                var initial = DateTimeOffset.UtcNow.AddMinutes(-2); initial = initial.AddTicks(-(initial.Ticks % 10));
                clock.Freeze(initial); advanceClock.AdvanceTo = initial.AddSeconds(31);
                Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => issuer.IssueAsync(identity, instance))).Status);
                Assert.Equal(1, advanceClock.HitCount);
                Assert.Empty(await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().ToArrayAsync());
                Assert.Empty(await db.Set<GatewaySecurityLease>().AsNoTracking().ToArrayAsync());
                return;
            }
            if (scenario is "known-expiry-clock-regression" or "cross-environment-clock-regression" or "later-issuance-clock-regression")
            {
                var initial = DateTimeOffset.UtcNow.AddMinutes(-2); initial = initial.AddTicks(-(initial.Ticks % 10));
                clock.Freeze(initial);
                var first = await issuer.IssueAsync(identity, instance);
                var trustedKeys = new TrustedSigningKeys(new Dictionary<string, string> { ["test-key"] = publicKey });
                var firstLease = SecurityCheckpointVerifier.VerifyNodeLease(new(first.Lease), trustedKeys, environment.Id, registered.NodeId, instance);
                if (scenario == "later-issuance-clock-regression")
                {
                    clock.Freeze(initial.AddSeconds(10));
                    await issuer.IssueAsync(identity, instance);
                }
                else
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO gateway_security_lease_expiry_observations(lease_id,observed_at) VALUES({firstLease.Payload.LeaseId},pg_catalog.clock_timestamp())");
                var secondIssuer = issuer; var secondIdentity = identity; var secondInstance = instance;
                if (scenario is "cross-environment-clock-regression" or "later-issuance-clock-regression")
                {
                    var otherEnvironment = new EnvironmentRecord { ProjectId = project.Id, Code = "OTHER", Name = "Other" };
                    db.AddRange(otherEnvironment, new EnvironmentSecurityInitialization { EnvironmentId = otherEnvironment.Id, ProjectId = project.Id,
                        OrganizationId = organization.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
                    await db.SaveChangesAsync();
                    var otherIdentities = new NodeIdentityService(db, new NodeEnrollmentSettings([new NodeEnrollment(otherEnvironment.Id, "second", NodeIdentityService.Hash(secret))]));
                    secondInstance = Guid.NewGuid();
                    var otherNode = await new NodeRegistry(db, otherIdentities).RegisterAsync(new RegisterNodeRequest(otherEnvironment.Id, "second", secondInstance,
                        "test", ["2.0", "2.1", "2.2", "2.3", "2.4"], ["security-ledger-v1"]), secret);
                    secondIdentity = new NodeIdentity(otherNode.NodeId, otherEnvironment.Id, NodeIdentityService.Hash(secret));
                    secondIssuer = new UntrustedGenesisLeaseMaterialBuilder(db, otherIdentities, new SecurityLedgerReader(db), settings);
                }
                clock.Freeze(initial.AddSeconds(1));
                var error = await Assert.ThrowsAsync<ApiException>(() => secondIssuer.IssueAsync(secondIdentity, secondInstance));
                Assert.Equal(503, error.Status);
                if (scenario != "later-issuance-clock-regression")
                    Assert.Equal(firstLease.Payload.LeaseId, (await db.Set<GatewaySecurityLease>().AsNoTracking().SingleAsync()).LeaseId);
                Assert.Equal(scenario == "later-issuance-clock-regression" ? 2 : 1, await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().CountAsync());
                return;
            }
            if (scenario == "duplicate-timestamp")
            {
                // Reproduce two issuance attempts at precisely the same trusted DB time.
                var instant = DateTimeOffset.UtcNow.AddSeconds(-5); instant = instant.AddTicks(-(instant.Ticks % 10)); clock.Freeze(instant);
                var first = await issuer.IssueAsync(identity, instance);
                var trustedKeys = new TrustedSigningKeys(new Dictionary<string, string> { ["test-key"] = publicKey });
                var firstLease = SecurityCheckpointVerifier.VerifyNodeLease(new(first.Lease), trustedKeys, environment.Id, registered.NodeId, instance);
                var error = await Assert.ThrowsAsync<ApiException>(() => issuer.IssueAsync(identity, instance));
                Assert.Equal(503, error.Status);Assert.True(clock.HitCount >= 2);
                Assert.Equal(firstLease.Payload.LeaseId, (await db.Set<GatewaySecurityLease>().AsNoTracking().SingleAsync()).LeaseId);
                Assert.Equal(1, await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().CountAsync());
                clock.Freeze(instant.AddSeconds(1));var later = await issuer.IssueAsync(identity, instance);
                var laterLease = SecurityCheckpointVerifier.VerifyNodeLease(new(later.Lease), trustedKeys, environment.Id, registered.NodeId, instance);
                Assert.True(laterLease.Payload.IssuedAt > firstLease.Payload.IssuedAt);Assert.Equal(2, await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().CountAsync());
                return;
            }
            var material = await issuer.IssueAsync(identity, instance);
            var trusted = new TrustedSigningKeys(new Dictionary<string, string> { ["test-key"] = publicKey });
            var checkpoint = SecurityCheckpointVerifier.VerifyCheckpoint(new(material.Checkpoint), trusted, environment.Id);
            var lease = SecurityCheckpointVerifier.VerifyNodeLease(new(material.Lease), trusted, environment.Id, registered.NodeId, instance);
            var databaseClock = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT observed_at AS \"Value\" FROM public.security_database_clock_watermark WHERE singleton").SingleAsync();
            Assert.True(databaseClock >= lease.Payload.IssuedAt, "A successful issuance must advance the durable database clock watermark.");
            Assert.Equal(checkpoint.Hash, lease.Payload.CheckpointHash);
            Assert.Equal(checkpoint.Payload.SecurityGeneration, lease.Payload.SecurityGeneration);
            Assert.True(lease.Payload.AllowAuthenticatedTraffic);
            Assert.True(lease.Payload.FreshUntil <= checkpoint.Payload.FreshUntil);
            Assert.Equal(0, checkpoint.Payload.Epoch);
            var renewal = await issuer.IssueAsync(identity, instance);
            var renewed = SecurityCheckpointVerifier.VerifyNodeLease(new(renewal.Lease), trusted, environment.Id, registered.NodeId, instance);
            Assert.NotEqual(lease.Payload.LeaseId, renewed.Payload.LeaseId);
            var storedLease = await db.Set<GatewaySecurityLease>().AsNoTracking().SingleAsync();
            Assert.True(storedLease.LastIssuedLeaseUntil >= lease.Payload.FreshUntil && storedLease.LastIssuedLeaseUntil >= renewed.Payload.FreshUntil);
            Assert.Equal(2, await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().CountAsync());
            // A legitimately issued earlier lease remains provable after a
            // renewal, as long as its own signed validity window is open.
            await new CurrentSecurityProofService(db, identities, new SecurityLedgerReader(db), settings).RecordAsync(registered.NodeId,
                new CurrentSecurityProof(instance, material.Checkpoint, material.Lease, DateTimeOffset.UtcNow, true, true), identity);
            await new CurrentSecurityProofService(db, identities, new SecurityLedgerReader(db), settings).RecordAsync(registered.NodeId,
                new CurrentSecurityProof(instance, renewal.Checkpoint, renewal.Lease, DateTimeOffset.UtcNow, true, true), identity);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            Assert.Equal(renewed.Payload.LeaseId, accepted.LeaseId);
            foreach (var (sql, expected) in new[] { ("UPDATE gateway_security_lease_issuances SET payload_hash=payload_hash", "23514"),
                ("DELETE FROM gateway_security_lease_issuances", "23514"), ("TRUNCATE gateway_security_lease_issuances", "0A000"),
                ("TRUNCATE gateway_security_lease_issuances CASCADE", "23514") })
                Assert.Equal(expected, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
            Assert.Equal(2, await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().CountAsync());
        }
        finally { File.Delete(keyFile); }
    }

    private sealed class AdvanceWhenSecurityStateRead(FrozenSecurityClockFixture clock) : DbCommandInterceptor
    {
        public DateTimeOffset? AdvanceTo { get; set; }
        public int HitCount { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (AdvanceTo is { } next && command.CommandText.Contains("environment_security_states", StringComparison.Ordinal))
            {
                AdvanceTo = null; HitCount++; clock.Freeze(next);
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

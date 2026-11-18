using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Cryptography;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class PostgresWitnessedCredentialRevocationTests
{
    [Theory]
    [InlineData("prepare-reply-lost")]
    [InlineData("commit-before-confirm")]
    [InlineData("confirmed-reply-lost")]
    public async Task RestartsReconcileTheSameDurableRevocation(string fault)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Witness.Fault = fault;
        await Assert.ThrowsAsync<IOException>(() => fixture.RevokeAsync());
        var committed = fault != "prepare-reply-lost";
        await fixture.AssertFactsAsync(committed ? 1 : 0);

        fixture.Witness.Fault = null;
        await fixture.Service().RecoverAsync(fixture.Actor);
        var retry = await fixture.RevokeAsync();
        await fixture.AssertFactsAsync(1);
        Assert.True(retry.Replayed);
        Assert.Equal("Unconfirmed", retry.Status);
        Assert.Equal(2, retry.Targets.Count);
        Assert.Equal(1, fixture.Witness.Preparations);
        Assert.Equal(1, fixture.Witness.Confirmations);
        Assert.Equal(fixture.Witness.Operation, retry.OperationId);
    }

    [Fact]
    public async Task ConcurrentRetriesCreateOneLedgerEventAndChangedRequestsConflict()
    {
        await using var fixture = await Fixture.StartAsync();
        var replies = await Task.WhenAll(fixture.RevokeAsync(), fixture.RevokeAsync());
        Assert.Equal(replies[0].OperationId, replies[1].OperationId);
        Assert.Single(replies, reply => !reply.Replayed);
        var changed = await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync("changed reason"));
        Assert.Equal(409, changed.Status);
        await fixture.AssertFactsAsync(1);
        Assert.Equal(1, fixture.Witness.Preparations);
    }

    [Fact]
    public async Task RevokedPermissionPreventsRecoveryFromApplyingPreparedIntent()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Witness.Fault = "prepare-reply-lost";
        await Assert.ThrowsAsync<IOException>(() => fixture.RevokeAsync());
        fixture.Witness.Fault = null;
        await using (var change = fixture.Database.Context())
            await change.Set<RolePermission>().Where(x => x.PermissionId == fixture.RevokePermission).ExecuteDeleteAsync();

        var retry = await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync());
        Assert.Equal(403, retry.Status);
        var recovery = await Assert.ThrowsAsync<ApiException>(() => fixture.Service().RecoverAsync(fixture.Actor));
        Assert.Equal(403, recovery.Status);
        Assert.NotNull(fixture.Witness.Pending);
        await fixture.AssertFactsAsync(0);
    }

    [Fact]
    public async Task UnknownCoverageWrongScopeOrStaleRevisionNeverPrepares()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Coverage.Approved = false;
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync())).Status);
        fixture.Coverage.Approved = true;
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync(environment: Guid.NewGuid()))).Status);
        Assert.Equal(412, (await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync(tag: "\"2\""))).Status);
        Assert.Equal(0, fixture.Witness.Preparations);
        await fixture.AssertFactsAsync(0);
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("legacy-snapshot")]
    public async Task NodeClaimsAloneCannotPrepareRevocation(string missing)
    {
        await using var fixture = await Fixture.StartAsync(withCurrentProof: missing != "missing-receipt", withGovernanceSnapshot: missing != "legacy-snapshot");
        var failure = await Assert.ThrowsAsync<ApiException>(() => fixture.RevokeAsync());
        Assert.Equal("credential_revocation_coverage_unknown", failure.Code);
        Assert.Equal(0, fixture.Witness.Preparations);
        await fixture.AssertFactsAsync(0);
    }

    [Fact]
    public async Task JournalInsertFailureRollsBackTheActualBusinessChangesBeforeRecovery()
    {
        await using var fixture = await Fixture.StartAsync();
        await using (var db = fixture.Database.Context())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION credential_governance.fixture_reject_append() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'fixture_journal_insert_failed' USING ERRCODE='23514'; END $$;
                CREATE TRIGGER fixture_reject_append BEFORE INSERT ON credential_governance.journal_entries
                    FOR EACH ROW EXECUTE FUNCTION credential_governance.fixture_reject_append();
                """);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => fixture.RevokeAsync());
        Assert.Equal("fixture_journal_insert_failed", failure.MessageText);
        Assert.NotNull(fixture.Witness.Pending);
        await fixture.AssertFactsAsync(0);
        await using (var db = fixture.Database.Context())
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fixture_reject_append ON credential_governance.journal_entries");

        await fixture.Service().RecoverAsync(fixture.Actor);
        await fixture.AssertFactsAsync(1);
        Assert.Equal(1, fixture.Witness.Confirmations);
    }

    [Fact]
    public async Task WitnessBehindDurablePostgresIsNeverRebuiltFromLocalHistory()
    {
        await using var fixture = await Fixture.StartAsync();
        await fixture.RevokeAsync();
        var restoredWitness = new TestWitness();
        var service = new PostgresCredentialRevocationService(fixture.Database.ConnectionString, fixture.Journal, restoredWitness, fixture.Coverage);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RecoverAsync(fixture.Actor));
        Assert.Equal(0, restoredWitness.Preparations);
        Assert.Equal(0, restoredWitness.Confirmations);
        await fixture.AssertFactsAsync(1);
    }

    [Fact]
    public async Task CommittedLedgerCanDriveTwoIsolatedGatewayStatesAndTheOldLeaseExpiresDuringLoss()
    {
        await using var fixture = await Fixture.StartAsync();
        await using var db = fixture.Database.Context();
        var reader = new SecurityLedgerReader(db);
        var previous = await reader.ReadCompleteAsync(fixture.EnvironmentId);
        var revoked = await fixture.RevokeAsync();
        var current = await reader.ReadCompleteAsync(fixture.EnvironmentId);
        Assert.Equal(revoked.OperationId, Assert.Single(current.Revocations).OperationId);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string fixtureKeyId = "fixture";
        var signer = new SecurityCheckpointSigner(fixtureKeyId, key.ExportPkcs8PrivateKeyPem());
        var keys = new TrustedSigningKeys(new Dictionary<string, string> { [fixtureKeyId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        var clock = new TestGatewayClock(revoked.CommittedAt.AddSeconds(-1));
        var directory = Path.Combine(Path.GetTempPath(), "witnessed-revoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var first = revoked.Targets[0];
        var second = revoked.Targets[1];
        try
        {
            await using var firstState = new SecurityStateStore(new(fixture.EnvironmentId, () => first.NodeId, first.InstanceGuid), new SecurityLkgStore(Path.Combine(directory, "first")), clock);
            await using var secondState = new SecurityStateStore(new(fixture.EnvironmentId, () => second.NodeId, second.InstanceGuid), new SecurityLkgStore(Path.Combine(directory, "second")), clock);
            await ApplyAsync(firstState, first, previous, lifetime: 5);
            await ApplyAsync(secondState, second, previous, lifetime: 5);
            Assert.Equal(SecurityAdmissionDecision.Allow, secondState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));

            clock.Advance(1);
            await ApplyAsync(firstState, first, current, lifetime: 20);
            Assert.Equal(SecurityAdmissionDecision.KnownRevoked, firstState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));
            Assert.Equal(SecurityAdmissionDecision.Allow, secondState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));

            clock.Advance(4);
            Assert.Equal(SecurityAdmissionDecision.Unavailable, secondState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));
            await ApplyAsync(secondState, second, current, lifetime: 20);
            Assert.Equal(SecurityAdmissionDecision.KnownRevoked, secondState.CanAdmit(fixture.CredentialId, clock.GetUtcNow()));
            Assert.Equal(SecurityAdmissionDecision.Allow, secondState.CanAdmit(Guid.NewGuid(), clock.GetUtcNow()));
        }
        finally { Directory.Delete(directory, recursive: true); }

        async Task ApplyAsync(SecurityStateStore state, RevocationTargetDto target, SecurityLedgerMaterial ledger, int lifetime)
        {
            // Keys and lease issuance exist solely inside this fixture. The
            // production issuer remains closed without independent activation.
            var now = clock.GetUtcNow();
            var until = now.AddSeconds(lifetime);
            var tombstones = ledger.Revocations.Select(r => new SecurityRevocationProof(r.CredentialId, r.OperationId,
                r.Epoch, r.RevokedAt, ledger.Events.Single(e => e.Epoch == r.Epoch).EventHash)).ToArray();
            var checkpoint = SecurityCheckpointVerifier.VerifyCheckpoint(new(signer.SignCheckpoint(new(SecuritySignedEncoding.Protocol,
                ledger.EnvironmentId, ledger.SecurityGeneration, ledger.Epoch, ledger.HeadHash, tombstones,
                ledger.RequireEnvironmentCredentials, ledger.MinimumSnapshotSchema, now, until, fixtureKeyId))), keys, ledger.EnvironmentId);
            var lease = SecurityCheckpointVerifier.VerifyNodeLease(new(signer.SignNodeLease(new(Guid.NewGuid(), SecuritySignedEncoding.Protocol,
                ledger.EnvironmentId, ledger.SecurityGeneration, ledger.Epoch, checkpoint.Hash, target.NodeId, target.InstanceGuid,
                SecuritySignedEncoding.Audience, now, until, fixtureKeyId))), keys, ledger.EnvironmentId, target.NodeId, target.InstanceGuid);
            Assert.True((await state.ApplyAsync(checkpoint, lease)).Applied);
        }
    }

    private sealed class TestGatewayClock(DateTimeOffset initial) : TimeProvider
    {
        private long ticks;
        public override DateTimeOffset GetUtcNow() => initial.AddTicks(ticks);
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public PostgresDatabase Database { get; } = new(ContractDatabaseGroup.GovernanceSecurityAck);
        public ActorContext Actor { get; } = new(Guid.NewGuid(), "isolated-revocation");
        public GovernanceJournalId Journal { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public TestWitness Witness { get; } = new();
        public TestCoverage Coverage { get; } = new();
        public Guid CredentialId { get; } = Guid.NewGuid();
        public Guid EnvironmentId { get; } = Guid.NewGuid();
        public Guid RevokePermission { get; } = Guid.NewGuid();

        public static async Task<Fixture> StartAsync(bool withCurrentProof = true, bool withGovernanceSnapshot = true)
        {
            var fixture = new Fixture();
            try { await fixture.InitializeAsync(withCurrentProof, withGovernanceSnapshot); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private async Task InitializeAsync(bool withCurrentProof, bool withGovernanceSnapshot)
        {
            await Database.InitializeAsync();
            await using var db = Database.Context();
            await db.Database.MigrateAsync();
            var org = new Organization { Code = "REVOKE", Name = "Isolated" };
            var project = new Project { OrganizationId = org.Id, Code = "REV-PROJECT", Name = "Isolated" };
            var environment = new EnvironmentRecord { Id = EnvironmentId, ProjectId = project.Id, Code = "TEST", Name = "Isolated", DeploymentSequence = 1, DesiredConfigVersion = 1 };
            var app = new ApplicationRecord { OrganizationId = org.Id, ProjectId = project.Id, Code = "APP", Name = "Isolated" };
            var role = new Role { OrganizationId = org.Id, Code = "SECURITY-FIXTURE", Name = "Isolated" };
            var permitRecovery = new Permission { Code = "credential.governance.recover", Module = "credential", Name = "Recover" };
            db.AddRange(org, project, environment, app, role, permitRecovery,
                new Permission { Id = RevokePermission, Code = "credential.revoke", Module = "credential", Name = "Revoke" },
                new UserRecord { Id = Actor.UserId, Username = "isolated-operator", DisplayName = "Isolated" },
                new UserRole { UserId = Actor.UserId, RoleId = role.Id },
                new RolePermission { RoleId = role.Id, PermissionId = RevokePermission },
                new RolePermission { RoleId = role.Id, PermissionId = permitRecovery.Id },
                new UserProjectScope { UserId = Actor.UserId, OrganizationId = org.Id, ProjectId = project.Id, EnvironmentId = EnvironmentId, AccessMode = "read_write" },
                new ApplicationCredential { Id = CredentialId, ApplicationId = app.Id, OrganizationId = org.Id, ProjectId = project.Id,
                    EnvironmentId = EnvironmentId, BindingMode = "EnvironmentBound", AccessKey = "isolated-access-key", SecretHash = "fixture-hash",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
                new EnvironmentSecurityInitialization { EnvironmentId = EnvironmentId, ProjectId = project.Id, OrganizationId = org.Id,
                    State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
            await db.SaveChangesAsync();
            // Dedicated permission for automated recovery of already prepared work is platform-scoped.
            var recoveryRole = new Role { Code = "PlatformAdmin", Name = "Recovery", IsSystem = true };
            var state = new EnvironmentSecurityState { EnvironmentId = EnvironmentId, ProjectId = project.Id, OrganizationId = org.Id };
            db.AddRange(recoveryRole, new UserRole { UserId = Actor.UserId, RoleId = recoveryRole.Id },
                new RolePermission { RoleId = recoveryRole.Id, PermissionId = permitRecovery.Id },
                state);
            var snapshot = withGovernanceSnapshot
                ? new RuntimeSnapshot("2.4", EnvironmentId, 1, DateTimeOffset.UtcNow, [], [], [], [], state.SecurityGeneration, 0, false, "2.0")
                : new RuntimeSnapshot("2.3", EnvironmentId, 1, DateTimeOffset.UtcNow, [], [], [], []);
            var bytes = CanonicalJson.Serialize(snapshot);
            var snapshotHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var version = new GatewayConfigVersion { EnvironmentId = EnvironmentId, VersionNo = 1, CreatedBy = Actor.UserId,
                SnapshotHash = snapshotHash };
            var release = new ReleaseRecord { EnvironmentId = EnvironmentId, ReleaseNo = "isolated-release", RequestedBy = Actor.UserId,
                Status = "Succeeded", ReleaseType = "publish", ToConfigVersion = 1, DeploymentSequence = 1 };
            db.AddRange(version, release, new GatewayConfigSnapshot { ConfigVersionId = version.Id, PayloadBytes = bytes, SizeBytes = bytes.Length });
            for (var number = 1; number <= 2; number++)
            {
                var instance = Guid.NewGuid();
                var node = new GatewayNode { EnvironmentId = EnvironmentId, NodeName = $"isolated-{number}", Status = "Ready", InstanceId = instance.ToString(),
                    IdentityHash = new string('a', 64), CurrentConfigVersion = 1, CurrentDeploymentSequence = 1, LastHeartbeatAt = DateTimeOffset.UtcNow,
                    Metadata = SecurityProtocolCapabilities.Merge(SnapshotSchemaCapabilities.Merge(null, instance, ["2.4"]), instance, ["security-ledger-v1"]) };
                var issuedAt = DateTimeOffset.UtcNow.AddMilliseconds(-100);
                var lease = new SecurityNodeLeasePayload(Guid.NewGuid(), SecuritySignedEncoding.Protocol, EnvironmentId, state.SecurityGeneration, 0,
                    new string('b', 64), node.Id, instance, SecuritySignedEncoding.Audience, issuedAt, issuedAt.AddSeconds(30), "fixture");
                db.AddRange(node, new ReleaseTarget { ReleaseId = release.Id, NodeId = node.Id, InstanceId = node.InstanceId },
                    new GatewayAck { ReleaseId = release.Id, NodeId = node.Id, InstanceId = node.InstanceId, ConfigVersion = 1,
                        DeploymentSequence = 1, PayloadHash = snapshotHash, Success = true },
                    new GatewaySecurityLease { LeaseId = lease.LeaseId, NodeId = node.Id, InstanceId = instance, EnvironmentId = EnvironmentId,
                        ProjectId = project.Id, OrganizationId = org.Id, SecurityGeneration = lease.SecurityGeneration, CheckpointHash = lease.CheckpointHash,
                        IssuedAt = lease.IssuedAt, FreshUntil = lease.FreshUntil, LastIssuedLeaseUntil = lease.FreshUntil, SigningKeyId = lease.KeyId },
                    SecurityLeaseIssuanceEvidence.Create(lease, org.Id, project.Id));
                if (withCurrentProof || number == 1)
                    db.Add(new GatewaySecurityCurrentReceipt { LeaseId = lease.LeaseId, NodeId = node.Id, InstanceId = instance, EnvironmentId = EnvironmentId,
                        ProjectId = project.Id, OrganizationId = org.Id, SecurityGeneration = lease.SecurityGeneration, CheckpointHash = lease.CheckpointHash,
                        HeadHash = state.HeadHash, FreshUntil = lease.FreshUntil, ReceivedAt = lease.IssuedAt });
            }
            await db.SaveChangesAsync();
            await IsolatedGovernanceJournalSchema.InstallAsync(db, Journal);
        }

        public PostgresCredentialRevocationService Service() => new(Database.ConnectionString, Journal, Witness, Coverage);

        public Task<RevocationOperationDto> RevokeAsync(string reason = "Retire isolated credential", Guid? environment = null, string tag = "\"1\"") =>
            Service().RevokeAsync(CredentialId, new(reason, environment ?? EnvironmentId), tag, new("isolated-request"), Actor);

        public async Task AssertFactsAsync(int count)
        {
            await using var db = Database.Context();
            var ledger = await new SecurityLedgerReader(db).ReadCompleteAsync(EnvironmentId);
            Assert.Equal(count, ledger.Epoch);
            Assert.Equal(count, ledger.Revocations.Count);
            Assert.Equal(count, await db.Set<SecurityOperation>().CountAsync());
            Assert.Equal(count, await db.Set<SecurityOutboxMessage>().CountAsync());
            Assert.Equal(count, await db.Set<AuditLog>().CountAsync(x => x.Action == "credential.revoke"));
            Assert.Equal(count * 2, await db.Set<SecurityOperationTarget>().CountAsync());
            Assert.Equal(count == 0 ? "Active" : "Revoked", (await db.Set<ApplicationCredential>().SingleAsync()).Status);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class TestCoverage : ICredentialRevocationCoverage
    {
        public bool Approved = true;
        public bool IsVerified(CredentialRevocationCoverageScope scope) => Approved && scope.Nodes.Count == 2 && scope.ReleaseId != Guid.Empty &&
            SecuritySignedEncoding.HexHash(scope.SnapshotHash) && scope.Nodes.All(x => x.LeaseId != Guid.Empty) &&
            scope.DeploymentSequence == 1 && scope.ConfigVersion == 1;
    }

    private sealed class TestWitness : IGovernanceJournalWitness
    {
        private GovernanceJournalPosition position = GovernanceJournalProtocol.Genesis;
        private GovernanceOperationConfirmation? confirmation;
        public GovernanceWriteIntent? Pending { get; private set; }
        public Guid? Operation => confirmation?.OperationId;
        public string? Fault { get; set; }
        public int Preparations { get; private set; }
        public int Confirmations { get; private set; }

        public Task<GovernanceWitnessState> ObserveAsync(GovernanceJournalId journal, Guid? operationId, CancellationToken ct) =>
            Task.FromResult(new GovernanceWitnessState(position, Pending, operationId == confirmation?.OperationId ? confirmation : null));

        public Task<GovernanceWitnessState> PrepareAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct)
        {
            Assert.Equal(position, intent.Previous);
            Assert.Null(Pending);
            Pending = intent;
            Preparations++;
            if (Fault == "prepare-reply-lost") throw new IOException("Fixture witness preparation response lost.");
            return ObserveAsync(journal, intent.OperationId, ct);
        }

        public Task<GovernanceWitnessState> ConfirmAsync(GovernanceJournalId journal, GovernanceWriteIntent intent, CancellationToken ct)
        {
            Assert.Equal(Pending, intent);
            if (Fault == "commit-before-confirm") throw new IOException("Fixture stopped before witness confirmation.");
            position = GovernanceJournalProtocol.NextPosition(journal, intent);
            confirmation = new(intent.OperationId, position);
            Pending = null;
            Confirmations++;
            if (Fault == "confirmed-reply-lost") throw new IOException("Fixture witness confirmation response lost.");
            return ObserveAsync(journal, intent.OperationId, ct);
        }
    }
}

using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Runtime;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class IndependentLeaseIssuanceTests
{
    private const string NodeSecret = "isolated-lease-enrollment-secret-0123456789";

    [Fact]
    public async Task MaterialRouteRequiresNodeAuthenticationAndUnavailableIssuerCreatesNoLease()
    {
        await using var api = new ApiFixture();
        await api.InitializeAsync();
        await api.SeedScopeAsync();
        await using var db = api.Context();
        var (node, instance) = await RegisterNodeAsync(db, api.Environment.Id);
        var url = $"/internal/v1/nodes/{node}/security/material?instanceId={instance}";
        using var unauthenticated = await api.Client.PostAsync(url, null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NodeSecret);
        using var response = await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(await db.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("exact", 200, true)]
    [InlineData("minimum-lease-window", 200, true)]
    [InlineData("complete-governed-ledger", 200, true)]
    [InlineData("wrong-live-config-epoch", 503, false)]
    [InlineData("missing-history", 503, false)]
    [InlineData("missing-continuity", 503, false)]
    [InlineData("wrong-instance", 409, false)]
    [InlineData("disabled-node", 401, false)]
    [InlineData("wrong-epoch", 503, false)]
    [InlineData("stale-local-authority", 503, false)]
    [InlineData("orphaned-desired-config", 503, false)]
    [InlineData("pending-write", 503, true)]
    [InlineData("wrong-reservation", 503, true)]
    [InlineData("lost-reservation-response", 503, true)]
    [InlineData("expired-after-reservation", 503, true)]
    [InlineData("local-rollback", 503, true)]
    [InlineData("expired-after-commit", 503, true)]
    [InlineData("rotation-without-worker", 200, true)]
    [InlineData("near-rotation-cutoff", 503, false)]
    public async Task SigningRequiresIndependentEvidenceAndDurableCurrentIssuance(string scenario, int expectedStatus, bool reserved)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var clock = new LeaseClock();
        var transactionObserver = new AdvanceAfterCommit(clock);
        await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(transactionObserver).Options);
        await db.Database.MigrateAsync();
        var organization = new Organization { Code = "LEASE", Name = "Test organization" };
        var project = new Project { OrganizationId = organization.Id, Code = "LEASE", Name = "Test project" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test environment" };
        var state = new EnvironmentSecurityState { EnvironmentId = environment.Id, ProjectId = project.Id,
            OrganizationId = organization.Id, InitialRequireEnvironmentCredentials = false };
        if (scenario == "minimum-lease-window")
        {
            state.LeaseSeconds = 5;
            state.LeaseRefreshSeconds = 1;
        }
        db.AddRange(organization, project, environment, state, new EnvironmentSecurityInitialization
        {
            EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
            State = "FreshNonStrict", FreshProofId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();
        SecurityEvent? revoked = null;
        if (scenario is "complete-governed-ledger" or "wrong-live-config-epoch")
            revoked = await AddGovernedDeploymentAsync(db, organization, project, environment, state, scenario == "wrong-live-config-epoch");
        var (node, instance) = await RegisterNodeAsync(db, environment.Id);
        var reader = new SecurityLedgerReader(db);
        var independentHistory = await reader.ReadCompleteAsync(environment.Id);
        if (scenario == "stale-local-authority")
            independentHistory = independentHistory with { SecurityGeneration = Guid.NewGuid() };
        if (scenario == "disabled-node")
        {
            var disabled = await db.Set<GatewayNode>().SingleAsync(item => item.Id == node);
            disabled.Enabled = false;
            await db.SaveChangesAsync();
        }
        if (scenario == "orphaned-desired-config")
        {
            environment.DeploymentSequence = 1;
            environment.DesiredConfigVersion = 1;
            await db.SaveChangesAsync();
        }
        if (scenario == "local-rollback")
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION public.reject_test_issuance() RETURNS trigger LANGUAGE plpgsql
                AS $$ BEGIN RAISE EXCEPTION 'test_issuance_failure'; END $$;
                CREATE TRIGGER reject_test_issuance BEFORE INSERT ON public.gateway_security_lease_issuances
                FOR EACH ROW EXECUTE FUNCTION public.reject_test_issuance();
                """);

        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var witness = new IsolatedEvidence(scenario, clock);
        var keyFile = Path.Combine(Path.GetTempPath(), $"test-lease-signing-{Guid.NewGuid():N}.pem");
        try
        {
            await File.WriteAllTextAsync(keyFile, signer.ExportPkcs8PrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var pins = new Dictionary<string, string> { ["issuer-key"] = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()) };
            var issuer = new SecurityLeaseIssuer(db, new NodeIdentityService(db, new([])), reader, new("issuer-key", keyFile, pins),
                new IsolatedContinuity(witness.Journal, independentHistory, scenario == "missing-continuity"), witness,
                new(witness, witness, witness.Trust), clock);
            DateTimeOffset? cutoff = null;
            if (scenario is "rotation-without-worker" or "near-rotation-cutoff")
                cutoff = await AddRotationAsync(db, organization, project, environment, scenario == "near-rotation-cutoff" ? 3 : 19);
            transactionObserver.Enabled = scenario == "expired-after-commit";
            var identity = new NodeIdentity(node, environment.Id, NodeIdentityService.Hash(NodeSecret));
            if (expectedStatus == 200)
            {
                var material = await issuer.IssueAsync(identity, instance);
                var checkpoint = SecurityCheckpointVerifier.VerifyCheckpoint(new(material.Checkpoint), new(pins), environment.Id);
                var lease = SecurityCheckpointVerifier.VerifyNodeLease(new(material.Lease), new(pins), environment.Id, node, instance);
                Assert.Equal(checkpoint.Hash, lease.Payload.CheckpointHash);
                Assert.Equal(state.SecurityGeneration, checkpoint.Payload.SecurityGeneration);
                Assert.Equal(revoked?.EventHash ?? SecurityEventHash.Genesis, checkpoint.Payload.HeadHash);
                Assert.Equal(revoked is null ? "2.0" : "2.4", checkpoint.Payload.MinimumSnapshotSchema);
                Assert.True(checkpoint.Payload.Full);
                Assert.True(lease.Payload.AllowAuthenticatedTraffic);
                Assert.Equal(checkpoint.Payload.FreshUntil, lease.Payload.FreshUntil);
                if (scenario == "minimum-lease-window")
                {
                    Assert.Equal(5, lease.Payload.LeaseSeconds);
                    Assert.Equal(TimeSpan.FromSeconds(5), lease.Payload.FreshUntil - lease.Payload.IssuedAt);
                }
                if (revoked is not null)
                {
                    Assert.True(checkpoint.Payload.RequireEnvironmentCredentials);
                    Assert.Equal(2, checkpoint.Payload.Epoch);
                    var tombstone = Assert.Single(checkpoint.Payload.Revocations);
                    Assert.Equal(revoked.CredentialId, tombstone.CredentialId);
                    Assert.Equal(revoked.OperationId, tombstone.OperationId);
                    Assert.Equal(revoked.EventHash, tombstone.EventHash);
                    var challenges = lease.Payload.AckChallenges!;
                    Assert.Equal(2, challenges.Count);
                    var revokeChallenge = Assert.Single(challenges, item => item.OperationId == revoked.OperationId);
                    Assert.Equal(2, revokeChallenge.RequiredEpoch);
                    Assert.Equal(tombstone.CredentialId, revokeChallenge.CredentialId);
                    var policyChallenge = Assert.Single(challenges, item => item.EventType == "EnvironmentPolicyChanged");
                    Assert.Equal(1, policyChallenge.RequiredEpoch);
                }
                if (cutoff.HasValue) Assert.Equal(cutoff, lease.Payload.FreshUntil);
                var reservation = Assert.IsType<GovernanceLeaseReservation>(witness.Reservation);
                Assert.Equal(lease.Payload.LeaseId, reservation.LeaseId);
                Assert.Equal(instance, reservation.InstanceId);
                Assert.Equal(organization.Id, reservation.OrganizationId);
                Assert.Equal(project.Id, reservation.ProjectId);
                Assert.Equal(SecuritySignedEncoding.LeaseHash(lease.Payload), reservation.LeasePayloadHash);
                Assert.Equal(lease.Payload.FreshUntil, reservation.FreshUntil);
                await using var observer = database.Context();
                Assert.True(await SecurityLeaseIssuanceEvidence.ContainsAsync(observer, lease.Payload, organization.Id, project.Id, default));
                var horizon = await observer.Set<GatewaySecurityLease>().AsNoTracking().SingleAsync();
                Assert.Equal(lease.Payload.LeaseId, horizon.LeaseId);
                Assert.Equal(lease.Payload.FreshUntil, horizon.LastIssuedLeaseUntil);
            }
            else
            {
                var error = await Assert.ThrowsAsync<ApiException>(() =>
                    issuer.IssueAsync(identity, scenario == "wrong-instance" ? Guid.NewGuid() : instance));
                Assert.Equal(expectedStatus, error.Status);
                await using var observer = database.Context();
                var count = scenario == "expired-after-commit" ? 1 : 0;
                Assert.Equal(count, await observer.Set<GatewaySecurityLeaseIssuance>().AsNoTracking().CountAsync());
                Assert.Equal(count, await observer.Set<GatewaySecurityLease>().AsNoTracking().CountAsync());
            }
            Assert.Equal(reserved, witness.Reservation is not null);
        }
        finally { File.Delete(keyFile); }
    }

    private static async Task<SecurityEvent> AddGovernedDeploymentAsync(WebApiDbContext db, Organization organization, Project project,
        EnvironmentRecord environment, EnvironmentSecurityState state, bool wrongDesiredEpoch)
    {
        var user = new UserRecord { Username = "isolated-lease-actor", DisplayName = "Fixture actor", PasswordHash = "fixture" };
        var application = new ApplicationRecord { Code = "ROTATING", Name = "Rotation fixture", OrganizationId = organization.Id, ProjectId = project.Id };
        var at = DateTimeOffset.UtcNow.AddSeconds(-1);
        at = at.AddTicks(-(at.Ticks % 10));
        var credential = new ApplicationCredential { ApplicationId = application.Id, OrganizationId = organization.Id, ProjectId = project.Id,
            EnvironmentId = environment.Id, BindingMode = "EnvironmentBound", AccessKey = "isolated-lease-revoked", SecretHash = new string('a', 64),
            SecretLast4 = "test", Status = "Revoked", RevokedAt = at, ExpiresAt = at.AddMinutes(5) };
        db.AddRange(user, application, credential);
        await db.SaveChangesAsync();
        var policyOperation = new SecurityOperation { OrganizationId = organization.Id, Kind = "EnvironmentPolicyCutover",
            RequestedBy = user.Id, CommittedAt = at };
        var revokeOperation = new SecurityOperation { OrganizationId = organization.Id, ApplicationId = application.Id,
            CredentialId = credential.Id, RequestedBy = user.Id, CommittedAt = at };
        var policy = new SecurityEvent
        {
            EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id, SecurityGeneration = state.SecurityGeneration,
            Epoch = 1, OperationId = policyOperation.Id, EventType = "EnvironmentPolicyChanged", OccurredAt = at,
            PreviousHash = SecurityEventHash.Genesis, PolicyJson = """{"requireEnvironmentCredentials":true,"minimumSnapshotSchema":"2.4"}"""
        };
        policy.EventHash = SecurityEventHash.Compute(policy);
        var revoked = new SecurityEvent
        {
            EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id, SecurityGeneration = state.SecurityGeneration,
            Epoch = 2, OperationId = revokeOperation.Id, CredentialId = credential.Id, OccurredAt = at, PreviousHash = policy.EventHash
        };
        revoked.EventHash = SecurityEventHash.Compute(revoked);
        db.AddRange(policyOperation, revokeOperation, policy, revoked,
            new CredentialRevocation { EnvironmentId = environment.Id, OrganizationId = organization.Id, ProjectId = project.Id,
                ApplicationId = application.Id, CredentialId = credential.Id, SecurityGeneration = state.SecurityGeneration, Epoch = 2,
                OperationId = revokeOperation.Id, RevokedAt = at });
        state.Epoch = 2;
        state.HeadHash = revoked.EventHash;
        state.RequireEnvironmentCredentials = true;
        state.MinimumSnapshotSchema = "2.4";
        state.Revision++;
        environment.RequireEnvironmentCredential = true;
        environment.DesiredConfigVersion = 1;
        environment.DeploymentSequence = 1;
        var initialization = await db.Set<EnvironmentSecurityInitialization>().SingleAsync(item => item.EnvironmentId == state.EnvironmentId);
        initialization.State = "Governed";
        initialization.Revision++;
        var snapshot = new RuntimeSnapshot("2.4", environment.Id, 1, at, [], [], [], [], state.SecurityGeneration,
            wrongDesiredEpoch ? 3 : 1, true, "2.4");
        var payload = CanonicalJson.Serialize(snapshot);
        var artifact = new GatewayConfigVersion { EnvironmentId = environment.Id, VersionNo = 1, CreatedBy = user.Id,
            SnapshotHash = Convert.ToHexStringLower(SHA256.HashData(payload)) };
        db.AddRange(artifact, new GatewayConfigSnapshot { ConfigVersionId = artifact.Id, PayloadBytes = payload, SizeBytes = payload.Length },
            new ReleaseRecord { EnvironmentId = environment.Id, ReleaseNo = "ISSUANCE-1", ReleaseType = "publish", Status = "Publishing",
                RequestedBy = user.Id, ToConfigVersion = 1, DeploymentSequence = 1 });
        await db.SaveChangesAsync();
        return revoked;
    }

    private static async Task<DateTimeOffset> AddRotationAsync(WebApiDbContext db, Organization organization, Project project,
        EnvironmentRecord environment, int secondsToDeadline)
    {
        var application = new ApplicationRecord { Code = "ROTATING", Name = "Rotation fixture", OrganizationId = organization.Id, ProjectId = project.Id };
        var old = NewCredential("synthetic-old");
        var replacement = NewCredential("synthetic-new");
        db.AddRange(application, old, replacement);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % 10));
        var hardDeadline = now.AddSeconds(secondsToDeadline);
        db.Add(new FormalCredentialRotation { Id = Guid.NewGuid(), CredentialId = old.Id, ReplacementCredentialId = replacement.Id,
            EnvironmentId = environment.Id, HardDeadline = hardDeadline, RecordedAt = now, IntentHash = new string('a', 64) });
        await db.SaveChangesAsync();
        return hardDeadline.AddSeconds(-2);

        ApplicationCredential NewCredential(string accessKey) => new()
        {
            ApplicationId = application.Id, OrganizationId = organization.Id, ProjectId = project.Id, EnvironmentId = environment.Id,
            BindingMode = "EnvironmentBound", AccessKey = accessKey, SecretHash = new string('b', 64), SecretLast4 = "test",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
    }

    private sealed class IsolatedContinuity(GovernanceJournalId journal, SecurityLedgerMaterial history, bool unavailable) : IGovernanceRecoveryContinuity
    {
        public async Task<GovernanceContinuity> RequireAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct)
        {
            Assert.Equal(history.EnvironmentId, environmentId);
            Assert.NotNull(db.Database.CurrentTransaction);
            Assert.True(await db.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks WHERE locktype='advisory'
                    AND pid=pg_catalog.pg_backend_pid() AND classid=0 AND objid=8901202 AND granted AND mode='ExclusiveLock') AS "Value"
                """).SingleAsync(ct));
            if (unavailable) throw new InvalidDataException();
            return new(journal, GovernanceJournalProtocol.Genesis, history);
        }
    }

    private sealed class IsolatedEvidence(string scenario, LeaseClock clock) :
        IIndependentTimeEvidenceSource, IIndependentSecurityHistoryWitness, IGovernanceLeaseReservationWitness, IDisposable
    {
        private readonly ECDsa timeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa historyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa journalKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public GovernanceJournalId Journal { get; } = new(Guid.NewGuid(), Guid.NewGuid());
        public IndependentEvidenceTrust Trust => new("test-time", "time-key", PublicKey(timeKey), "test-history", "history-key", PublicKey(historyKey));
        public GovernanceLeaseReservation? Reservation { get; private set; }

        Task<Stream?> IIndependentTimeEvidenceSource.RequestAsync(EvidenceChallenge challenge, CancellationToken ct)
        {
            var now = clock.GetUtcNow();
            var payload = new TimeEvidencePayload(challenge, "test-time", "time-key", now.AddMilliseconds(-500),
                now.AddMilliseconds(500), now.AddSeconds(25));
            var response = new SignedTimeEvidence(payload, Sign(timeKey, IndependentEvidenceSession.TimeDomain, payload));
            return Task.FromResult<Stream?>(new MemoryStream(CanonicalJson.Serialize(response)));
        }

        Task<Stream?> IIndependentSecurityHistoryWitness.RequestAsync(EvidenceChallenge challenge, CancellationToken ct)
        {
            if (scenario == "missing-history") return Task.FromResult<Stream?>(null);
            var payload = new HistoryEvidencePayload(scenario == "wrong-epoch" ? challenge with { Epoch = challenge.Epoch + 1 } : challenge,
                "test-history", "history-key", "Confirmed", 1);
            var response = new SignedHistoryEvidence(payload, Sign(historyKey, IndependentEvidenceSession.HistoryDomain, payload));
            return Task.FromResult<Stream?>(new MemoryStream(CanonicalJson.Serialize(response)));
        }

        public Task<VerifiedGovernanceLeaseReservation> ReserveAllowLeaseAsync(GovernanceJournalId journal, GovernanceJournalPosition position,
            GovernanceLeaseReservation reservation, CancellationToken ct)
        {
            Assert.Equal(Journal, journal);
            Assert.Equal(GovernanceJournalProtocol.Genesis, position);
            Assert.Null(Reservation);
            Reservation = reservation;
            if (scenario == "lost-reservation-response") throw new IOException();
            var acknowledged = scenario == "wrong-reservation" ? reservation with { InstanceId = Guid.NewGuid() } : reservation;
            var request = GovernanceLeaseReservationProtocol.Reserve(journal, position, acknowledged);
            var payload = new GovernanceLeaseReservationPayload(GovernanceLeaseReservationProtocol.RequestHash(request),
                "test-journal", "journal-key", position, scenario == "pending-write", acknowledged, true);
            var response = new SignedGovernanceLeaseReservationResponse(payload, Sign(journalKey, GovernanceLeaseReservationProtocol.ResponseDomain, payload));
            var verified = GovernanceLeaseReservationProtocol.VerifyResponse(request, CanonicalJson.Serialize(response),
                new("test-journal", "journal-key", PublicKey(journalKey)), clock.GetUtcNow());
            if (scenario == "expired-after-reservation") clock.Advance();
            return Task.FromResult(verified);
        }

        private static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        private static string Sign<T>(ECDsa key, string domain, T payload) => Convert.ToBase64String(key.SignData(
            SecuritySignedEncoding.Unsigned(domain, payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public void Dispose()
        {
            timeKey.Dispose();
            historyKey.Dispose();
            journalKey.Dispose();
        }
    }

    private sealed class LeaseClock : TimeProvider
    {
        private long advance;
        public override long GetTimestamp() => TimeProvider.System.GetTimestamp() + Volatile.Read(ref advance);
        public void Advance() => Interlocked.Exchange(ref advance, TimestampFrequency * 30);
    }

    private sealed class AdvanceAfterCommit(LeaseClock clock) : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Enabled) clock.Advance();
            return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
        }
    }

    private static async Task<(Guid Node, Guid Instance)> RegisterNodeAsync(WebApiDbContext db, Guid environment)
    {
        var identities = new NodeIdentityService(db, new([new(environment, "lease-node", NodeIdentityService.Hash(NodeSecret))]));
        var instance = Guid.NewGuid();
        var registered = await new NodeRegistry(db, identities).RegisterAsync(new RegisterNodeRequest(environment,
            "lease-node", instance, "test", ["2.0", "2.1", "2.2", "2.3", "2.4"], ["security-ledger-v1"]), NodeSecret);
        return (registered.NodeId, instance);
    }
}

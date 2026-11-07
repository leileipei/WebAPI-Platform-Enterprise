using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class GovernanceSecurityAckIntegrationTests
{
    [Theory]
    [InlineData("ack-first")]
    [InlineData("proof-first")]
    [InlineData("bad-signature")]
    [InlineData("wrong-challenge")]
    [InlineData("wrong-instance")]
    [InlineData("applied-after-expiry")]
    [InlineData("lease-outlives-checkpoint")]
    [InlineData("checkpoint-expired-lease-fresh")]
    [InlineData("dirty-context")]
    [InlineData("stale-node")]
    [InlineData("unissued-lease-id")]
    [InlineData("altered-lease-claims")]
    [InlineData("no-issuance-evidence")]
    [InlineData("applied-at-expiry")]
    [InlineData("legacy-ack-revalidated")]
    [InlineData("legacy-ack-expired-replay")]
    [InlineData("legacy-ack-slow-clock")]
    [InlineData("legacy-ack-concurrent")]
    [InlineData("legacy-ack-replaced-instance")]
    [InlineData("legacy-ack-expired-challenge")]
    [InlineData("legacy-ack-expired-challenge-watermark")]
    [InlineData("legacy-ack-expired-failure-retry")]
    [InlineData("legacy-ack-expiration-during-failure-save")]
    [InlineData("legacy-ack-challenge-clock-regression")]
    [InlineData("legacy-ack-challenge-slow-read")]
    [InlineData("legacy-ack-challenge-slow-save")]
    [InlineData("legacy-ack-challenge-slow-final-read")]
    [InlineData("expired-first-ack-replay")]
    [InlineData("future-latest-lease")]
    [InlineData("missing-material-failure")]
    [InlineData("future-failure-history")]
    [InlineData("future-failure-sequence")]
    [InlineData("conflicting-failure-history")]
    [InlineData("future-receipt-status")]
    [InlineData("future-verification-status")]
    [InlineData("expired-current-receipt-status")]
    [InlineData("advanced-authority-status")]
    [InlineData("stale-ambient-status")]
    [InlineData("authority-lock-wait-status")]
    [InlineData("expiry-during-status-read")]
    [InlineData("newer-lease-issued-status")]
    [InlineData("rollback-before-newer-lease-status")]
    [InlineData("status-clock-rollback-after-expiry")]
    [InlineData("ack-cross-environment-isolation")]
    [InlineData("premature-expiry-future-forgery")]
    [InlineData("expired-lease-reuse-first-ack")]
    [InlineData("expired-lease-duplicate-ack")]
    [InlineData("proof-expiry-before-reader")]
    [InlineData("ack-expiry-before-reader")]
    [InlineData("duplicate-ack-expiry-before-reader")]
    [InlineData("proof-expiry-client-cancel")]
    [InlineData("ack-expiry-client-cancel")]
    [InlineData("duplicate-ack-expiry-client-cancel")]
    [InlineData("status-expiry-client-cancel")]
    [InlineData("proof-cross-expiry-read")]
    [InlineData("ack-cross-expiry-read")]
    [InlineData("proof-expiry-commit-cancel")]
    [InlineData("ack-expiry-commit-cancel")]
    [InlineData("duplicate-ack-expiry-commit-cancel")]
    [InlineData("status-expiry-commit-cancel")]
    [InlineData("expiry-concurrent-status")]
    [InlineData("proof-expiry-validation-cancel")]
    [InlineData("ack-expiry-validation-cancel")]
    [InlineData("proof-cross-expiry-validation-cancel")]
    [InlineData("future-current-receipt-forgery")]
    [InlineData("future-ack-verification-forgery")]
    [InlineData("future-challenge-forgery")]
    [InlineData("unexpected-failure-constraint")]
    public async Task AckRequiresRealSignedMaterialAndIndependentCurrentProof(string scenario)
    {
        await using var database = new PostgresDatabase();
        await database.InitializeAsync();
        var clock = new FrozenSecurityClockFixture();
        var advanceClock = new AdvanceClockOnReceipt(clock);
        var advanceChallengeSave = new AdvanceClockOnChallengeSave(clock);
        var cancelOnExpiry = new CancelOnExpiryWrite();
        var cancelAtCommit = new CancelOnExpiryCommit();
        var cancelOnRead = new CancelOnSecurityRead(advanceClock);
        var delayedSave = new DelayedFailureSave(database.ConnectionString);
        await using var db = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(clock, delayedSave, advanceClock, advanceChallengeSave, cancelOnExpiry, cancelAtCommit, cancelOnRead).Options);
        await db.Database.MigrateAsync();
        var organization = new Organization { Code = "ACK-ORG", Name = "Organization" };
        var project = new Project { OrganizationId = organization.Id, Code = "ACK-PRJ", Name = "Project" };
        var environment = new EnvironmentRecord { ProjectId = project.Id, Code = "TEST", Name = "Test" };
        var user = new UserRecord { Username = "ack-fixture", DisplayName = "Ack fixture" };
        var initialization = new EnvironmentSecurityInitialization { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
            State = "FreshNonStrict", FreshProofId = Guid.NewGuid() };
        db.AddRange(organization, project, environment, user, initialization);
        await db.SaveChangesAsync();
        var generation = Guid.NewGuid();
        var state = new EnvironmentSecurityState { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
            SecurityGeneration = generation, Epoch = 0, HeadHash = SecurityEventHash.Genesis, InitialRequireEnvironmentCredentials = false,
            RequireEnvironmentCredentials = false, MinimumSnapshotSchema = "2.0", UpdatedAt = DateTimeOffset.UtcNow };
        db.Add(state);
        await db.SaveChangesAsync();
        const string secret = "test-only-enrollment-secret-0123456789abcdef";
        var identities = new NodeIdentityService(db, new NodeEnrollmentSettings([new NodeEnrollment(environment.Id, "first", NodeIdentityService.Hash(secret))]));
        var instance = Guid.NewGuid();
        var registered = await new NodeRegistry(db, identities).RegisterAsync(
            new RegisterNodeRequest(environment.Id, "first", instance, "test", ["2.0", "2.1", "2.2", "2.3", "2.4"], ["security-ledger-v1"]), secret);
        var identity = new NodeIdentity(registered.NodeId, environment.Id, NodeIdentityService.Hash(secret));

        // Expiry-observation cases use an entirely historical signed window:
        // application time can be advanced deterministically while the real
        // database clock still enforces that observations are not future-dated.
        var syntheticHistoricalClock = scenario is "status-clock-rollback-after-expiry" or "expired-current-receipt-status" or "expiry-during-status-read" or "expired-lease-reuse-first-ack" or "expired-lease-duplicate-ack" or "proof-expiry-before-reader" or "ack-expiry-before-reader" or "duplicate-ack-expiry-before-reader" or "proof-expiry-client-cancel" or "ack-expiry-client-cancel" or "duplicate-ack-expiry-client-cancel" or "status-expiry-client-cancel" or "proof-cross-expiry-read" or "ack-cross-expiry-read" or "proof-expiry-commit-cancel" or "ack-expiry-commit-cancel" or "duplicate-ack-expiry-commit-cancel" or "status-expiry-commit-cancel" or "expiry-concurrent-status" or "proof-expiry-validation-cancel" or "ack-expiry-validation-cancel" or "proof-cross-expiry-validation-cancel" or "legacy-ack-challenge-clock-regression" or "legacy-ack-challenge-slow-read" or "legacy-ack-challenge-slow-save" or "legacy-ack-challenge-slow-final-read" or "future-latest-lease" or "newer-lease-issued-status" or "rollback-before-newer-lease-status";
        var now = DateTimeOffset.UtcNow.AddSeconds(syntheticHistoricalClock ? -60 : 0); now = now.AddTicks(-(now.Ticks % 10));
        var eventAt = scenario is "legacy-ack-expired-replay" or "expired-first-ack-replay" ? now.AddMinutes(-1) : now;
        var operationId = Guid.NewGuid(); var challenge = Guid.NewGuid();
        var operation = new SecurityOperation { Id = operationId, OrganizationId = organization.Id, Kind = "EnvironmentPolicyCutover",
            RequestedBy = user.Id, Reason = "isolated test", CommittedAt = eventAt };
        var securityEvent = new SecurityEvent { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
            SecurityGeneration = generation, Epoch = 1, OperationId = operationId, EventType = "EnvironmentPolicyChanged",
            PolicyJson = "{\"requireEnvironmentCredentials\":false,\"minimumSnapshotSchema\":\"2.4\"}", OccurredAt = eventAt, PreviousHash = SecurityEventHash.Genesis };
        securityEvent.EventHash = SecurityEventHash.Compute(securityEvent);
        initialization.State = "Governed"; initialization.Revision++;
        state.Epoch = 1; state.HeadHash = securityEvent.EventHash; state.MinimumSnapshotSchema = "2.4"; state.Revision++; state.UpdatedAt = eventAt;
        db.Add(operation); db.Add(securityEvent);
        await db.SaveChangesAsync();
        db.Add(new SecurityOperationTarget { OperationId = operationId, EnvironmentId = environment.Id, NodeId = registered.NodeId,
            InstanceGuid = instance, ChallengeNonce = challenge, CreatedAt = eventAt, ProtocolSupportedAtFreeze = true });
        await db.SaveChangesAsync();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new SecurityCheckpointSigner("test-key", key.ExportPkcs8PrivateKeyPem());
        var settings = new SecuritySigningSettings(null, null, new Dictionary<string, string> { ["test-key"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        var issued = now.AddSeconds(scenario is "applied-after-expiry" or "legacy-ack-expired-replay" or "expired-first-ack-replay" ? -40 : scenario == "applied-at-expiry" ? -20 : -1);
        var expires = issued.AddSeconds(20); var leaseId = Guid.NewGuid();
        var checkpointIssued = scenario == "checkpoint-expired-lease-fresh" ? now.AddSeconds(-25) : issued;
        var checkpointExpires = scenario switch
        {
            "lease-outlives-checkpoint" => now.AddSeconds(5),
            "checkpoint-expired-lease-fresh" => now.AddSeconds(-5),
            _ => expires
        };
        var checkpointPayload = new SecurityCheckpointPayload(SecuritySignedEncoding.Protocol, environment.Id, generation, 1, securityEvent.EventHash,
            [], false, "2.4", checkpointIssued, checkpointExpires, "test-key");
        var checkpoint = signer.SignCheckpoint(checkpointPayload);
        var hash = SecuritySignedEncoding.CheckpointHash(checkpointPayload);
        var leasePayload = new SecurityNodeLeasePayload(leaseId, SecuritySignedEncoding.Protocol, environment.Id, generation, 1, hash,
            registered.NodeId, instance, SecuritySignedEncoding.Audience, issued, expires, "test-key", AckChallenges: [new SecurityAckChallenge(operationId, challenge, 1, null, "EnvironmentPolicyChanged")]);
        var signedLease = signer.SignNodeLease(leasePayload);
        var issuedPayload = scenario == "unissued-lease-id" ? leasePayload with { LeaseId = Guid.NewGuid() } : leasePayload;
        db.Add(new GatewaySecurityLease { NodeId = registered.NodeId, InstanceId = instance, EnvironmentId = environment.Id,
            OrganizationId = organization.Id, ProjectId = project.Id, SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash,
            LeaseId = issuedPayload.LeaseId, IssuedAt = issued, FreshUntil = expires, LastIssuedLeaseUntil = expires, SigningKeyId = "test-key", LeaseSeconds = 30 });
        if (scenario != "no-issuance-evidence") db.Add(SecurityLeaseIssuanceEvidence.Create(issuedPayload, organization.Id, project.Id));
        await db.SaveChangesAsync();
        if (scenario == "future-latest-lease")
        {
            // Simulate a known later issuance followed by database-clock
            // rollback, while presenting the older still-valid signed lease.
            var future = now.AddMilliseconds(1800);
            var futureCheckpoint = checkpointPayload with { IssuedAt = future, FreshUntil = future.AddSeconds(20) };
            var futureHash = SecuritySignedEncoding.CheckpointHash(futureCheckpoint);
            var futureLease = leasePayload with { LeaseId = Guid.NewGuid(), CheckpointHash = futureHash, IssuedAt = future, FreshUntil = future.AddSeconds(20) };
            var horizon = await db.Set<GatewaySecurityLease>().SingleAsync(x => x.NodeId == registered.NodeId && x.InstanceId == instance);
            horizon.LeaseId = futureLease.LeaseId; horizon.CheckpointHash = futureHash; horizon.IssuedAt = future; horizon.FreshUntil = futureLease.FreshUntil;
            horizon.LastIssuedLeaseUntil = futureLease.FreshUntil; horizon.Revision++;
            db.Add(SecurityLeaseIssuanceEvidence.Create(futureLease, organization.Id, project.Id));
            await db.SaveChangesAsync();
        }
        if (scenario == "altered-lease-claims") signedLease = signer.SignNodeLease(leasePayload with { MaximumClockSkewSeconds = 1 });
        if (scenario == "bad-signature")
        {
            var body = JsonNode.Parse(checkpoint)!.AsObject();
            body["signature"] = Convert.ToBase64String(new byte[64]);
            checkpoint = Encoding.UTF8.GetBytes(body.ToJsonString());
        }
        if (scenario == "future-latest-lease" || syntheticHistoricalClock) clock.Freeze(now);
        var reader = new SecurityLedgerReader(db);
        var proof = new CurrentSecurityProofService(db, identities, reader, settings);
        var proofRequest = new CurrentSecurityProof(instance, checkpoint, signedLease, now, true, true);
        var ack = new SecurityAck(operationId, registered.NodeId, scenario == "wrong-instance" ? Guid.NewGuid() : instance, environment.Id,
            generation, 1, hash, scenario == "applied-at-expiry" ? expires : scenario is "legacy-ack-expired-replay" or "expired-first-ack-replay" ? expires.AddSeconds(-1) : now, true, true, true, scenario == "wrong-challenge" ? Guid.NewGuid() : challenge, null, checkpoint, signedLease);
        var service = new SecurityAckService(db, identities, reader, settings);
        if (scenario is "future-failure-history" or "future-failure-sequence")
        {
            var futureAt = scenario == "future-failure-history" ? now.AddMinutes(5) : now.AddSeconds(-1); var hashForAttempt = new string('f', 64);
            long? historicalSequence = scenario == "future-failure-sequence" ? 100L : null;
            // Simulate a pre-existing restored fact after a clock regression;
            // bypass guards only in this disposable test database.
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try { await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_ack_attempts(id,operation_id,environment_id,node_id,instance_guid,challenge_nonce,reported_nonce,observation_sequence,request_hash,error_code,received_at) VALUES({Guid.NewGuid()},{operationId},{environment.Id},{registered.NodeId},{instance},{challenge},NULL,{historicalSequence},{hashForAttempt},'security_material_invalid',{futureAt})"); }
            finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
            var failure = new SecurityAckFailure(operationId, registered.NodeId, instance, environment.Id, challenge, "security_state_unavailable", 1);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, failure, identity))).Status);
            var target = (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Targets.Single();
            Assert.Equal(scenario == "future-failure-history" ? "failure_time_inconsistent" : "failure_sequence_inconsistent", target.Reason);
            Assert.Null(target.ErrorCode);
            Assert.Empty(await db.Set<SecurityAckFailureLatest>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario == "conflicting-failure-history")
        {
            var failure = new SecurityAckFailure(operationId, registered.NodeId, instance, environment.Id, challenge, "security_material_invalid", 1);
            Assert.False((await service.RecordFailureAsync(registered.NodeId, failure, identity)).Duplicate);
            var latest = await db.Set<SecurityAckFailureLatest>().AsNoTracking().SingleAsync();
            var differentHash = new string('e', 64);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET session_replication_role=replica");
            try { await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_ack_attempts(id,operation_id,environment_id,node_id,instance_guid,challenge_nonce,reported_nonce,observation_sequence,request_hash,error_code,received_at) VALUES({Guid.NewGuid()},{operationId},{environment.Id},{registered.NodeId},{instance},{challenge},{challenge},1,{differentHash},'security_state_unavailable',{latest.LastAcceptedAt})"); }
            finally { await db.Database.ExecuteSqlRawAsync("SET session_replication_role=origin"); }
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, failure, identity))).Status);
            var target = (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Targets.Single();
            Assert.Equal("failure_sequence_inconsistent", target.Reason);
            Assert.Null(target.ErrorCode);
            var direct = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE security_ack_failure_latest SET observation_count=observation_count+1,latest_sequence=latest_sequence+1"));
            Assert.Equal("23514", direct.SqlState);
            Assert.Equal("security_failure_sequence_invalid", direct.MessageText);
            return;
        }
        if (scenario == "unexpected-failure-constraint")
        {
            var failure = new SecurityAckFailure(operationId, registered.NodeId, instance, environment.Id, challenge, "security_material_invalid", 1);
            Assert.False((await service.RecordFailureAsync(registered.NodeId, failure, identity)).Duplicate);
            // Simulate a partially restored sequence after the service reads
            // it, retaining the concurrency token so the database guard is
            // reached. Its unrelated error must not become a nonce conflict.
            delayedSave.BumpOperation = operationId;
            var updateError = await Assert.ThrowsAsync<DbUpdateException>(() => service.RecordFailureAsync(registered.NodeId,
                failure with { ErrorCode = "security_state_unavailable", ObservationSequence = 2 }, identity));
            var postgresError = Assert.IsType<PostgresException>(updateError.InnerException);
            Assert.Equal("23514", postgresError.SqlState);
            Assert.Equal("security_failure_transition_invalid", postgresError.MessageText);
            Assert.Equal(1, delayedSave.HitCount);
            return;
        }
        if (scenario == "missing-material-failure")
        {
            var failure = new SecurityAckFailure(operationId, registered.NodeId, instance, environment.Id, challenge, "security_material_invalid", 1);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, failure with { ObservationSequence = long.MaxValue }, identity))).Status);
            var legacyFailure = ack with { Success = false, ErrorCode = "security_material_invalid" };
            foreach (var rejected in new[] { legacyFailure, legacyFailure with { AppliedAt = now.AddTicks(-10) } })
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, rejected, identity))).Status);
            var recorded = await service.RecordFailureAsync(registered.NodeId, failure, identity);
            Assert.False(recorded.Duplicate);
            Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            Assert.True((await service.RecordFailureAsync(registered.NodeId, failure, identity)).Duplicate);
            var secondFailure = failure with { ErrorCode = "security_state_unavailable", ObservationSequence = 2 };
            Assert.False((await service.RecordFailureAsync(registered.NodeId, secondFailure, identity)).Duplicate);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, failure, identity))).Status);
            var thirdFailure = failure with { ObservationSequence = 3 };
            Assert.False((await service.RecordFailureAsync(registered.NodeId, thirdFailure, identity)).Duplicate);
            Assert.True((await service.RecordFailureAsync(registered.NodeId, thirdFailure, identity)).Duplicate);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, thirdFailure with { ErrorCode = "security_state_unavailable" }, identity))).Status);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, thirdFailure with { ObservationSequence = 5 }, identity))).Status);
            var last = await db.Set<SecurityAckFailureLatest>().AsNoTracking().SingleAsync();
            Assert.Equal("security_material_invalid", last.ErrorCode);
            Assert.Equal(challenge, last.ReportedNonce);
            Assert.Equal(3, last.ObservationCount);
            Assert.Equal(3, last.LatestSequence);
            Assert.Equal(2, await db.Set<SecurityAckAttempt>().AsNoTracking().CountAsync());
            Assert.Equal("security_material_invalid", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Targets.Single().ErrorCode);
            var invalidNonce = Guid.NewGuid();
            var badLatest = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE security_ack_failure_latest SET reported_nonce={invalidNonce},observation_count=observation_count+1,latest_sequence=latest_sequence+1 WHERE operation_id={operationId}"));
            Assert.Equal("23514", badLatest.SqlState);
            var invalidTransition = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE security_ack_failure_latest SET observation_count=observation_count"));
            Assert.Equal("23514", invalidTransition.SqlState);
            var skippedSequence = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE security_ack_failure_latest SET observation_count=observation_count+1,latest_sequence=latest_sequence+2"));
            Assert.Equal("23514", skippedSequence.SqlState);
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE SCHEMA attack_path; CREATE FUNCTION attack_path.security_failure_nonce_valid(uuid,uuid,uuid,uuid,uuid,timestamptz) RETURNS boolean LANGUAGE sql AS 'SELECT true'; SET search_path=attack_path,public,pg_catalog");
            try
            {
                var shadowed = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.security_ack_failure_latest SET reported_nonce={invalidNonce},observation_count=observation_count+1,latest_sequence=latest_sequence+1 WHERE operation_id={operationId}"));
                Assert.Equal("23514", shadowed.SqlState);
            }
            finally { await db.Database.ExecuteSqlRawAsync("SET search_path=public,pg_catalog"); }
            var attemptId = Guid.NewGuid(); var missingNonceHash = new string('d', 64);
            var missingNonce = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_ack_attempts(id,operation_id,environment_id,node_id,instance_guid,challenge_nonce,reported_nonce,observation_sequence,request_hash,error_code,received_at) VALUES({attemptId},{operationId},{environment.Id},{registered.NodeId},{instance},{challenge},NULL,4,{missingNonceHash},'security_state_unavailable',{last.LastAcceptedAt})"));
            Assert.Equal("23514", missingNonce.SqlState);
            foreach (var sql in new[] { "DELETE FROM security_ack_failure_latest", "TRUNCATE security_ack_failure_latest", "TRUNCATE security_ack_attempts" })
                Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
            Assert.Empty(await db.Set<SecurityAckRecord>().AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            foreach (var invalid in new[] { failure with { ChallengeNonce = Guid.NewGuid() }, failure with { InstanceId = Guid.NewGuid() },
                failure with { ErrorCode = "untrusted arbitrary text" } })
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, invalid, identity))).Status);
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, failure, identity))).Status);
            return;
        }
        if (scenario is "legacy-ack-revalidated" or "legacy-ack-expired-replay" or "legacy-ack-slow-clock" or "legacy-ack-concurrent" or "legacy-ack-replaced-instance" or "legacy-ack-expired-challenge" or "legacy-ack-expired-challenge-watermark" or "legacy-ack-expired-failure-retry" or "legacy-ack-expiration-during-failure-save" or "legacy-ack-challenge-clock-regression" or "legacy-ack-challenge-slow-read" or "legacy-ack-challenge-slow-save" or "legacy-ack-challenge-slow-final-read")
        {
            var legacyApplied = scenario == "legacy-ack-expired-replay" ? expires.AddSeconds(-1) : now.AddSeconds(-2);
            db.Add(new SecurityAckRecord { OperationId = operationId, EnvironmentId = environment.Id, NodeId = registered.NodeId, InstanceGuid = instance,
                SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, ChallengeNonce = challenge,
                AppliedAt = legacyApplied, ReceivedAt = now, Success = true, Durable = true, AdmissionEnforced = true });
            await db.SaveChangesAsync();
            if (scenario == "legacy-ack-expired-replay")
            {
                // A newer valid current proof does not make replayed expired
                // ACK material evidence of a new application.
                var freshCheckpointPayload = checkpointPayload with { IssuedAt = now.AddSeconds(-1), FreshUntil = now.AddSeconds(19) };
                var freshCheckpoint = signer.SignCheckpoint(freshCheckpointPayload);
                var freshHash = SecuritySignedEncoding.CheckpointHash(freshCheckpointPayload);
                var freshLeasePayload = leasePayload with { LeaseId = Guid.NewGuid(), CheckpointHash = freshHash,
                    IssuedAt = freshCheckpointPayload.IssuedAt, FreshUntil = freshCheckpointPayload.FreshUntil };
                var freshLease = signer.SignNodeLease(freshLeasePayload);
                var trackedLease = await db.Set<GatewaySecurityLease>().SingleAsync(x => x.NodeId == registered.NodeId && x.InstanceId == instance);
                trackedLease.LeaseId = freshLeasePayload.LeaseId; trackedLease.CheckpointHash = freshHash; trackedLease.IssuedAt = freshLeasePayload.IssuedAt;
                trackedLease.FreshUntil = freshLeasePayload.FreshUntil; trackedLease.LastIssuedLeaseUntil = freshLeasePayload.FreshUntil; trackedLease.Revision++;
                db.Add(SecurityLeaseIssuanceEvidence.Create(freshLeasePayload, organization.Id, project.Id));
                await db.SaveChangesAsync();
                await proof.RecordAsync(registered.NodeId, new CurrentSecurityProof(instance, freshCheckpoint, freshLease, now, true, true), identity);
                Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, ack, identity))).Status);
                ack = ack with { CheckpointHash = freshHash, AppliedAt = now, Checkpoint = freshCheckpoint, Lease = freshLease };
            }
            else
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            // Reusing the frozen target challenge does not prove reapplication
            // after migration, even when the associated lease is still fresh.
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, ack, identity))).Status);
            Assert.Equal(legacyApplied, (await db.Set<SecurityAckRecord>().AsNoTracking().SingleAsync()).AppliedAt);
            Assert.Empty(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            var challengeService = new SecurityAckRevalidationChallengeService(db, identities, reader);
            if (scenario == "legacy-ack-challenge-slow-save")
            {
                advanceChallengeSave.AdvanceTo = now.AddSeconds(31);
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => challengeService.CreateAsync(identity, instance, operationId))).Status);
                Assert.Equal(1, advanceChallengeSave.HitCount);
                return;
            }
            SecurityAckRevalidationTicket revalidation;
            if (scenario == "legacy-ack-concurrent")
            {
                await using var other = database.Context();
                var otherIdentities = new NodeIdentityService(other, new NodeEnrollmentSettings([new NodeEnrollment(environment.Id, "first", NodeIdentityService.Hash(secret))]));
                var otherService = new SecurityAckRevalidationChallengeService(other, otherIdentities, new SecurityLedgerReader(other));
                var tickets = await Task.WhenAll(challengeService.CreateAsync(identity, instance, operationId), otherService.CreateAsync(identity, instance, operationId));
                Assert.Equal(tickets[0].Nonce, tickets[1].Nonce);
                revalidation = tickets[0];
            }
            else revalidation = await challengeService.CreateAsync(identity, instance, operationId);
            Assert.Equal(revalidation.Nonce, (await challengeService.CreateAsync(identity, instance, operationId)).Nonce);
            Assert.NotEqual(challenge, revalidation.Nonce);
            if (scenario == "legacy-ack-challenge-slow-read")
            {
                clock.Freeze(revalidation.CreatedAt.AddSeconds(1));
                advanceClock.TableName = "security_ack_revalidation_challenges";
                advanceClock.AdvanceTo = revalidation.ExpiresAt.AddSeconds(1);
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => challengeService.CreateAsync(identity, instance, operationId))).Status);
                Assert.Equal(1, advanceClock.HitCount);
                return;
            }
            if (scenario == "legacy-ack-challenge-slow-final-read")
            {
                clock.Freeze(revalidation.CreatedAt.AddSeconds(1));
                advanceClock.TableName = "gateway_security_current_receipts";
                advanceClock.MatchesUntilAdvance = 3;
                advanceClock.AdvanceTo = revalidation.ExpiresAt.AddSeconds(1);
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => challengeService.CreateAsync(identity, instance, operationId))).Status);
                Assert.Equal(1, advanceClock.HitCount);
                return;
            }
            if (scenario == "legacy-ack-challenge-clock-regression")
            {
                clock.Freeze(revalidation.ExpiresAt);
                var later = await challengeService.CreateAsync(identity, instance, operationId);
                Assert.NotEqual(revalidation.Nonce, later.Nonce);
                clock.Freeze(revalidation.CreatedAt.AddSeconds(20));
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => challengeService.CreateAsync(identity, instance, operationId))).Status);
                Assert.Equal(2, await db.Set<SecurityAckRevalidationChallenge>().AsNoTracking().CountAsync());
                return;
            }
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => challengeService.CreateAsync(identity, Guid.NewGuid(), operationId))).Status);
            var revalidationFailure = new SecurityAckFailure(operationId, registered.NodeId, instance, environment.Id, revalidation.Nonce, "security_material_invalid", 1);
            if (scenario == "legacy-ack-expiration-during-failure-save")
            {
                // Simulate passage of time at the exact save boundary rather
                // than sleeping and depending on scheduler timing.
                clock.Freeze(revalidation.CreatedAt.AddSeconds(1));
                delayedSave.ExpireNonce = revalidation.Nonce;
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, revalidationFailure, identity))).Status);
                Assert.Equal(1, delayedSave.HitCount);
                Assert.False(db.ChangeTracker.HasChanges());
                await using var observer = database.Context();
                Assert.Empty(await observer.Set<SecurityAckFailureLatest>().AsNoTracking().ToArrayAsync());
                Assert.Empty(await observer.Set<SecurityAckAttempt>().AsNoTracking().ToArrayAsync());
                return;
            }
            Assert.False((await service.RecordFailureAsync(registered.NodeId, revalidationFailure, identity)).Duplicate);
            Assert.Equal(revalidation.Nonce, (await db.Set<SecurityAckFailureLatest>().AsNoTracking().SingleAsync()).ReportedNonce);
            if (scenario == "legacy-ack-expired-failure-retry")
            {
                // Advance only the test service clock. The accepted report and
                // original immutable challenge retain their real timestamps.
                clock.Freeze(revalidation.ExpiresAt.AddSeconds(1));
                Assert.True((await service.RecordFailureAsync(registered.NodeId, revalidationFailure, identity)).Duplicate);
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, revalidationFailure with { ObservationSequence = 2 }, identity))).Status);
                Assert.Single(await db.Set<SecurityAckAttempt>().AsNoTracking().ToArrayAsync());
                return;
            }
            if (scenario == "legacy-ack-replaced-instance")
            {
                var replacement = Guid.NewGuid();
                await new NodeRegistry(db, identities).RegisterAsync(new RegisterNodeRequest(environment.Id, "first", replacement, "test",
                    ["2.0", "2.1", "2.2", "2.3", "2.4"], ["security-ledger-v1"]), secret);
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => challengeService.CreateAsync(identity, instance, operationId))).Status);
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, ack, identity))).Status);
                Assert.Empty(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
                return;
            }
            if (scenario is "legacy-ack-expired-challenge" or "legacy-ack-expired-challenge-watermark")
            {
                var expired = new SecurityAckRevalidationChallenge { Nonce = Guid.NewGuid(), OperationId = operationId, EnvironmentId = environment.Id,
                    NodeId = registered.NodeId, InstanceGuid = instance, CreatedAt = now.AddSeconds(-30), ExpiresAt = now.AddMilliseconds(-100) };
                db.Add(expired);
                var expiredCheckpointPayload = checkpointPayload with { IssuedAt = now.AddMilliseconds(-500), FreshUntil = now.AddMilliseconds(19500) };
                var expiredCheckpoint = signer.SignCheckpoint(expiredCheckpointPayload);
                var expiredHash = SecuritySignedEncoding.CheckpointHash(expiredCheckpointPayload);
                var expiredLeasePayload = leasePayload with { LeaseId = Guid.NewGuid(), CheckpointHash = expiredHash, IssuedAt = expiredCheckpointPayload.IssuedAt,
                    FreshUntil = expiredCheckpointPayload.FreshUntil,
                    AckChallenges = [new SecurityAckChallenge(operationId, expired.Nonce, 1, null, "EnvironmentPolicyChanged")] };
                var expiredLease = signer.SignNodeLease(expiredLeasePayload);
                var horizon = await db.Set<GatewaySecurityLease>().SingleAsync(x => x.NodeId == registered.NodeId && x.InstanceId == instance);
                horizon.LeaseId = expiredLeasePayload.LeaseId; horizon.CheckpointHash = expiredHash; horizon.IssuedAt = expiredLeasePayload.IssuedAt;
                horizon.FreshUntil = expiredLeasePayload.FreshUntil; horizon.LastIssuedLeaseUntil = expiredLeasePayload.FreshUntil; horizon.Revision++;
                db.Add(SecurityLeaseIssuanceEvidence.Create(expiredLeasePayload, organization.Id, project.Id));
                await db.SaveChangesAsync();
                var backdatedId = Guid.NewGuid(); var backdatedHash = new string('e', 64); var backdatedAt = expired.CreatedAt.AddSeconds(1);
                var backdated = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_ack_attempts(id,operation_id,environment_id,node_id,instance_guid,challenge_nonce,reported_nonce,observation_sequence,request_hash,error_code,received_at) VALUES({backdatedId},{operationId},{environment.Id},{registered.NodeId},{instance},{challenge},{expired.Nonce},2,{backdatedHash},'security_state_unavailable',{backdatedAt})"));
                Assert.Equal("23514", backdated.SqlState);
                var expiredAck = ack with { ChallengeNonce = expired.Nonce, CheckpointHash = expiredHash, AppliedAt = now.AddMilliseconds(-200),
                    Checkpoint = expiredCheckpoint, Lease = expiredLease };
                var clockBefore = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT observed_at AS \"Value\" FROM public.security_database_clock_watermark WHERE singleton").SingleAsync();
                if (scenario == "legacy-ack-expired-challenge-watermark")
                {
                    var expiredFailure = new SecurityAckFailure(operationId, registered.NodeId, instance, environment.Id, expired.Nonce, "security_material_invalid", 2);
                    Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordFailureAsync(registered.NodeId, expiredFailure, identity))).Status);
                    var afterFailure = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT observed_at AS \"Value\" FROM public.security_database_clock_watermark WHERE singleton").SingleAsync();
                    Assert.True(afterFailure > clockBefore, "Rejecting an expired failure report must retain the observed database time.");
                    clockBefore = afterFailure;
                }
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, expiredAck, identity))).Status);
                if (scenario == "legacy-ack-expired-challenge-watermark")
                {
                    var clockAfter = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT observed_at AS \"Value\" FROM public.security_database_clock_watermark WHERE singleton").SingleAsync();
                    Assert.True(clockAfter > clockBefore, "A trusted expired challenge must advance the committed clock watermark even when ACK is rejected.");
                }
                Assert.Empty(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
                return;
            }
            var newCheckpointPayload = checkpointPayload with { IssuedAt = revalidation.CreatedAt, FreshUntil = revalidation.CreatedAt.AddSeconds(20) };
            var newCheckpoint = signer.SignCheckpoint(newCheckpointPayload);
            var newHash = SecuritySignedEncoding.CheckpointHash(newCheckpointPayload);
            var newLeasePayload = leasePayload with { LeaseId = Guid.NewGuid(), CheckpointHash = newHash, IssuedAt = revalidation.CreatedAt,
                FreshUntil = newCheckpointPayload.FreshUntil,
                AckChallenges = [new SecurityAckChallenge(operationId, revalidation.Nonce, 1, null, "EnvironmentPolicyChanged")] };
            var newLease = signer.SignNodeLease(newLeasePayload);
            var issuedNow = await db.Set<GatewaySecurityLease>().SingleAsync(x => x.NodeId == registered.NodeId && x.InstanceId == instance);
            issuedNow.LeaseId = newLeasePayload.LeaseId; issuedNow.CheckpointHash = newHash; issuedNow.IssuedAt = newLeasePayload.IssuedAt;
            issuedNow.FreshUntil = newLeasePayload.FreshUntil;
            if (issuedNow.LastIssuedLeaseUntil < newLeasePayload.FreshUntil) issuedNow.LastIssuedLeaseUntil = newLeasePayload.FreshUntil;
            issuedNow.Revision++;
            db.Add(SecurityLeaseIssuanceEvidence.Create(newLeasePayload, organization.Id, project.Id));
            await db.SaveChangesAsync();
            await proof.RecordAsync(registered.NodeId, new CurrentSecurityProof(instance, newCheckpoint, newLease, revalidation.CreatedAt, true, true), identity);
            if (scenario == "legacy-ack-revalidated")
            {
                // Control-plane timestamps share one DB clock; a restored or
                // regressed received time may not precede challenge issuance.
                var receivedBeforeChallenge = revalidation.CreatedAt.AddTicks(-10);
                var timeError = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO security_ack_verifications(operation_id,environment_id,node_id,instance_guid,challenge_nonce,revalidation_nonce,security_generation,epoch,checkpoint_hash,lease_id,applied_at,received_at) VALUES({operationId},{environment.Id},{registered.NodeId},{instance},{challenge},{revalidation.Nonce},{generation},1,{newHash},{newLeasePayload.LeaseId},{revalidation.CreatedAt},{receivedBeforeChallenge})"));
                Assert.Equal("23514", timeError.SqlState);
            }
            var newAck = ack with { ChallengeNonce = revalidation.Nonce, CheckpointHash = newHash, AppliedAt = scenario == "legacy-ack-slow-clock" ? revalidation.CreatedAt.AddSeconds(-1) : revalidation.CreatedAt,
                Checkpoint = newCheckpoint, Lease = newLease };
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, newAck with { ChallengeNonce = Guid.NewGuid() }, identity))).Status);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, newAck with { AppliedAt = revalidation.CreatedAt.AddSeconds(-2).AddTicks(-10) }, identity))).Status);
            if (scenario == "legacy-ack-concurrent")
            {
                await using var other = database.Context();
                var otherIdentities = new NodeIdentityService(other, new NodeEnrollmentSettings([new NodeEnrollment(environment.Id, "first", NodeIdentityService.Hash(secret))]));
                var otherService = new SecurityAckService(other, otherIdentities, new SecurityLedgerReader(other), settings);
                var results = await Task.WhenAll(service.RecordAsync(registered.NodeId, newAck, identity), otherService.RecordAsync(registered.NodeId, newAck, identity));
                Assert.Single(results, x => x.Duplicate);
                Assert.Single(results, x => !x.Duplicate);
                Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            }
            else
            {
                var accepted = await service.RecordAsync(registered.NodeId, newAck, identity);
                Assert.False(accepted.Duplicate);
                Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            }
            Assert.Equal(legacyApplied, (await db.Set<SecurityAckRecord>().AsNoTracking().SingleAsync()).AppliedAt);
            Assert.Equal(revalidation.Nonce, (await db.Set<SecurityAckVerification>().AsNoTracking().SingleAsync()).RevalidationNonce);
            Assert.True((await service.RecordAsync(registered.NodeId, newAck, identity)).Duplicate);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => challengeService.CreateAsync(identity, instance, operationId))).Status);
            foreach (var sql in new[] { "UPDATE security_ack_revalidation_challenges SET created_at=created_at", "DELETE FROM security_ack_revalidation_challenges", "TRUNCATE security_ack_revalidation_challenges CASCADE" })
                Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
            return;
        }
        if (scenario == "dirty-context")
        {
            var tracked = await db.Set<EnvironmentRecord>().SingleAsync(x => x.Id == environment.Id);
            tracked.Name = "unsaved caller change";
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync(registered.NodeId, ack, identity));
            Assert.True(db.ChangeTracker.HasChanges());
            await using var observer = database.Context();
            Assert.Equal("Test", (await observer.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(x => x.Id == environment.Id)).Name);
            Assert.Empty(await observer.Set<SecurityAckRecord>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario == "stale-node")
        {
            _ = await db.Set<GatewayNode>().SingleAsync(x => x.Id == registered.NodeId);
            await using var observer = database.Context();
            var current = await observer.Set<GatewayNode>().SingleAsync(x => x.Id == registered.NodeId);
            current.Metadata = SecurityProtocolCapabilities.Merge(current.Metadata, instance, []);
            await observer.SaveChangesAsync();
        }
        if (scenario is "advanced-authority-status" or "stale-ambient-status" or "authority-lock-wait-status")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            await using var staleDb = database.Context();
            await using var staleSnapshot = await staleDb.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            Assert.Equal(1, await staleDb.Set<EnvironmentSecurityState>().AsNoTracking().CountAsync());
            await using var authorityTransaction = scenario == "authority-lock-wait-status" ? await db.Database.BeginTransactionAsync() : null;
            if (authorityTransaction is not null)
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={environment.Id} FOR UPDATE");
            }
            var nextAt = DateTimeOffset.UtcNow; nextAt = nextAt.AddTicks(-(nextAt.Ticks % 10));
            var nextOperation = new SecurityOperation { Id = Guid.NewGuid(), OrganizationId = organization.Id,
                Kind = "EnvironmentPolicyCutover", RequestedBy = user.Id, Reason = "later isolated test", CommittedAt = nextAt };
            var nextEvent = new SecurityEvent { EnvironmentId = environment.Id, ProjectId = project.Id, OrganizationId = organization.Id,
                SecurityGeneration = generation, Epoch = 2, OperationId = nextOperation.Id, EventType = "EnvironmentPolicyChanged",
                PolicyJson = "{\"requireEnvironmentCredentials\":false,\"minimumSnapshotSchema\":\"2.4\"}", OccurredAt = nextAt,
                PreviousHash = securityEvent.EventHash };
            nextEvent.EventHash = SecurityEventHash.Compute(nextEvent);
            var currentState = await db.Set<EnvironmentSecurityState>().SingleAsync(x => x.EnvironmentId == environment.Id);
            currentState.Epoch = 2; currentState.HeadHash = nextEvent.EventHash; currentState.Revision++; currentState.UpdatedAt = nextAt;
            db.AddRange(nextOperation, nextEvent);
            await db.SaveChangesAsync();
            if (scenario == "authority-lock-wait-status")
            {
                var lockObserved = new AdvisoryLockObserved();
                await using var pendingReader = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>()
                    .UseNpgsql(database.ConnectionString).AddInterceptors(lockObserved).Options);
                await pendingReader.Database.OpenConnectionAsync();
                var readerPid = await pendingReader.Database.SqlQueryRaw<int>("SELECT pg_catalog.pg_backend_pid() AS \"Value\"").SingleAsync();
                var pending = new SecurityOperationStatusReader(pendingReader).ReadAsync(operationId);
                var timeout = Task.Delay(TimeSpan.FromSeconds(10));
                var firstCompleted = await Task.WhenAny(lockObserved.Started.Task, pending, timeout);
                var serverWaiting = false;
                if (ReferenceEquals(firstCompleted, lockObserved.Started.Task))
                {
                    await using var lockInspector = database.Context();
                    for (var check = 0; check < 100 && !serverWaiting && !pending.IsCompleted; check++)
                    {
                        serverWaiting = await lockInspector.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_locks WHERE locktype='advisory' AND pid={readerPid} AND classid=0 AND objid=8901202 AND objsubid=1 AND NOT granted) AS \"Value\"").SingleAsync();
                        if (!serverWaiting) await Task.Delay(20);
                    }
                }
                await authorityTransaction!.CommitAsync();
                var statusAfterRelease = await pending;
                Assert.Same(lockObserved.Started.Task, firstCompleted);
                Assert.True(serverWaiting, "The reader must actually wait for the authority lock in PostgreSQL.");
                Assert.Equal("Unconfirmed", statusAfterRelease.Status);
            }
            else if (scenario == "stale-ambient-status")
                await Assert.ThrowsAsync<InvalidOperationException>(() => new SecurityOperationStatusReader(staleDb).ReadAsync(operationId));
            else
                Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            await staleSnapshot.RollbackAsync();
            Assert.Single(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            Assert.Single(await db.Set<SecurityAckRecord>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario is "future-current-receipt-forgery" or "future-ack-verification-forgery" or "future-challenge-forgery")
        {
            var future = DateTimeOffset.UtcNow.AddSeconds(3); future = future.AddTicks(-(future.Ticks % 10));
            Assert.True(future < expires);
            if (scenario == "future-current-receipt-forgery")
            {
                await proof.RecordAsync(registered.NodeId, proofRequest, identity);
                var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE gateway_security_current_receipts SET received_at={future} WHERE node_id={registered.NodeId} AND instance_id={instance}"));
                Assert.Equal("23514", error.SqlState);
            }
            else if (scenario == "future-ack-verification-forgery")
            {
                db.Add(new SecurityAckRecord { OperationId = operationId, EnvironmentId = environment.Id, NodeId = registered.NodeId, InstanceGuid = instance,
                    SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, ChallengeNonce = challenge,
                    AppliedAt = now, ReceivedAt = now, Success = true, Durable = true, AdmissionEnforced = true });
                await db.SaveChangesAsync();
                db.Add(new SecurityAckVerification { OperationId = operationId, EnvironmentId = environment.Id, NodeId = registered.NodeId, InstanceGuid = instance,
                    SecurityGeneration = generation, Epoch = 1, CheckpointHash = hash, ChallengeNonce = challenge, LeaseId = leaseId,
                    AppliedAt = now, ReceivedAt = future });
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Equal("23514", ((PostgresException)error.InnerException!).SqlState);
            }
            else
            {
                db.Add(new SecurityAckRevalidationChallenge { Nonce = Guid.NewGuid(), OperationId = operationId, EnvironmentId = environment.Id,
                    NodeId = registered.NodeId, InstanceGuid = instance, CreatedAt = future, ExpiresAt = future.AddSeconds(30) });
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Equal("23514", ((PostgresException)error.InnerException!).SqlState);
            }
            return;
        }
        if (scenario is "proof-expiry-validation-cancel" or "ack-expiry-validation-cancel" or "proof-cross-expiry-validation-cancel")
        {
            if (scenario != "proof-cross-expiry-validation-cancel") await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            using var requestCancellation = new CancellationTokenSource();
            cancelOnRead.Request = requestCancellation;
            cancelOnRead.TableName = scenario == "ack-expiry-validation-cancel" ? "gateway_security_lease_issuances" : "gateway_security_lease_expiry_observations";
            if (scenario == "proof-cross-expiry-validation-cancel")
            {
                advanceClock.TableName = "gateway_security_lease_issuances"; advanceClock.AdvanceTo = expires;
                cancelOnRead.OnlyAfterClockAdvance = true;
            }
            else clock.Freeze(expires);
            try
            {
                if (scenario == "ack-expiry-validation-cancel") await service.RecordAsync(registered.NodeId, ack, identity, requestCancellation.Token);
                else await proof.RecordAsync(registered.NodeId, proofRequest, identity, requestCancellation.Token);
            }
            catch (ApiException error) { Assert.Equal(409, error.Status); }
            catch (OperationCanceledException) { }
            Assert.True(requestCancellation.IsCancellationRequested);
            await using var observer = database.Context();
            Assert.Single(await observer.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario is "proof-cross-expiry-read" or "ack-cross-expiry-read")
        {
            if (scenario == "ack-cross-expiry-read") await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            advanceClock.TableName = "gateway_security_lease_issuances";
            advanceClock.AdvanceTo = expires;
            if (scenario == "proof-cross-expiry-read")
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => proof.RecordAsync(registered.NodeId, proofRequest, identity))).Status);
            else
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, ack, identity))).Status);
            Assert.Equal(1, advanceClock.HitCount);
            Assert.Single(await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().ToArrayAsync());
            Assert.Empty(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            if (scenario == "proof-cross-expiry-read") Assert.Empty(await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario == "expiry-concurrent-status")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            clock.Freeze(accepted.FreshUntil);
            var otherClock = new FrozenSecurityClockFixture(); otherClock.Freeze(accepted.FreshUntil);
            await using var other = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>()
                .UseNpgsql(database.ConnectionString).AddInterceptors(otherClock).Options);
            var results = await Task.WhenAll(new SecurityOperationStatusReader(db).ReadAsync(operationId), new SecurityOperationStatusReader(other).ReadAsync(operationId));
            Assert.All(results, x => Assert.Equal("Unconfirmed", x.Status));
            Assert.Single(await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario is "proof-expiry-client-cancel" or "ack-expiry-client-cancel" or "duplicate-ack-expiry-client-cancel" or "status-expiry-client-cancel" or
            "proof-expiry-commit-cancel" or "ack-expiry-commit-cancel" or "duplicate-ack-expiry-commit-cancel" or "status-expiry-commit-cancel")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            if (scenario.StartsWith("proof-", StringComparison.Ordinal) || scenario.StartsWith("duplicate-", StringComparison.Ordinal) || scenario.StartsWith("status-", StringComparison.Ordinal))
                Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            clock.Freeze(accepted.FreshUntil);
            using var requestCancellation = new CancellationTokenSource();
            if (scenario.EndsWith("commit-cancel", StringComparison.Ordinal)) cancelAtCommit.Request = requestCancellation;
            else cancelOnExpiry.Request = requestCancellation;
            try
            {
                if (scenario.StartsWith("proof-", StringComparison.Ordinal))
                    await proof.RecordAsync(registered.NodeId, proofRequest, identity, requestCancellation.Token);
                else if (scenario.StartsWith("status-", StringComparison.Ordinal))
                    await new SecurityOperationStatusReader(db).ReadAsync(operationId, requestCancellation.Token);
                else
                    await service.RecordAsync(registered.NodeId, ack, identity, requestCancellation.Token);
            }
            catch (ApiException error) { Assert.Equal(409, error.Status); }
            catch (OperationCanceledException) { }
            Assert.True(requestCancellation.IsCancellationRequested);
            await using var observer = database.Context();
            Assert.Single(await observer.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario is "proof-expiry-before-reader" or "ack-expiry-before-reader" or "duplicate-ack-expiry-before-reader")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            if (scenario is "proof-expiry-before-reader" or "duplicate-ack-expiry-before-reader")
                Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            clock.Freeze(accepted.FreshUntil);
            if (scenario == "proof-expiry-before-reader")
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => proof.RecordAsync(registered.NodeId, proofRequest, identity))).Status);
            else if (scenario == "ack-expiry-before-reader")
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, ack, identity))).Status);
            else
                Assert.True((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            // The proof/ACK endpoint itself observed expiration before any
            // status read; rolling the DB clock back must not revive that lease.
            Assert.Single(await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().ToArrayAsync());
            clock.Freeze(now);
            Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            return;
        }
        if (scenario is "expired-lease-reuse-first-ack" or "expired-lease-duplicate-ack")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            if (scenario == "expired-lease-duplicate-ack")
                Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            clock.Freeze(accepted.FreshUntil);
            Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            Assert.Single(await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().ToArrayAsync());
            clock.Freeze(now);
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => proof.RecordAsync(registered.NodeId, proofRequest, identity))).Status);
            if (scenario == "expired-lease-reuse-first-ack")
            {
                Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, ack, identity))).Status);
                Assert.Empty(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            }
            else
            {
                Assert.True((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
                Assert.Single(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            }
            return;
        }
        if (scenario == "premature-expiry-future-forgery")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            await service.RecordAsync(registered.NodeId, ack, identity);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            var actualDbNow = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT pg_catalog.clock_timestamp() AS \"Value\"").SingleAsync();
            Assert.True(actualDbNow < accepted.FreshUntil);
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO gateway_security_lease_expiry_observations(lease_id,observed_at) VALUES({accepted.LeaseId},{accepted.FreshUntil})"));
            Assert.Equal("23514", error.SqlState);
            return;
        }
        if (scenario == "ack-cross-environment-isolation")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            await service.RecordAsync(registered.NodeId, ack, identity);
            var before = await service.RecordAsync(registered.NodeId, ack, identity);
            Assert.True(before.Duplicate);
            Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            var second = new EnvironmentRecord { ProjectId = project.Id, Code = "PROD", Name = "Other environment" };
            var secondGeneration = Guid.NewGuid();
            var secondOrigin = new EnvironmentSecurityInitialization { EnvironmentId = second.Id, ProjectId = project.Id,
                OrganizationId = organization.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() };
            db.AddRange(second, secondOrigin);
            await db.SaveChangesAsync();
            var otherState = new EnvironmentSecurityState { EnvironmentId = second.Id, ProjectId = project.Id, OrganizationId = organization.Id,
                SecurityGeneration = secondGeneration, Epoch = 0, MinimumSnapshotSchema = "2.0", UpdatedAt = now };
            db.Add(otherState);
            await db.SaveChangesAsync();
            var otherEvent = new SecurityEvent { EnvironmentId = second.Id, ProjectId = project.Id, OrganizationId = organization.Id,
                SecurityGeneration = secondGeneration, Epoch = 1, OperationId = operationId, EventType = "EnvironmentPolicyChanged",
                PolicyJson = "{\"requireEnvironmentCredentials\":false,\"minimumSnapshotSchema\":\"2.4\"}", OccurredAt = now,
                PreviousHash = SecurityEventHash.Genesis };
            otherEvent.EventHash = SecurityEventHash.Compute(otherEvent);
            secondOrigin.State = "Governed"; secondOrigin.Revision++;
            otherState.Epoch = 1; otherState.HeadHash = otherEvent.EventHash; otherState.MinimumSnapshotSchema = "2.4"; otherState.Revision++;
            db.Add(otherEvent);
            await db.SaveChangesAsync();
            Assert.Equal("Partial", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            var after = await service.RecordAsync(registered.NodeId, ack, identity);
            Assert.True(after.Duplicate);
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
            return;
        }
        if (scenario == "status-clock-rollback-after-expiry")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            Assert.Empty(await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().ToArrayAsync());
            clock.Freeze(accepted.FreshUntil);
            Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            var expiryFact = await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().SingleAsync();
            Assert.Equal(accepted.LeaseId, expiryFact.LeaseId);
            Assert.True(expiryFact.ObservedAt > accepted.FreshUntil.AddSeconds(30), "The database must stamp observation time instead of accepting a backdated caller value.");
            clock.Freeze(accepted.FreshUntil.AddSeconds(-1));
            // A lease observed expired cannot be revived by a later DB-clock rollback.
            // A fresh context prevents a merely process-local observation cache
            // from masquerading as durable cross-instance evidence.
            await using var restarted = new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>()
                .UseNpgsql(database.ConnectionString).AddInterceptors(clock).Options);
            Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(restarted).ReadAsync(operationId)).Status);
            Assert.Single(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            foreach (var sql in new[] { "UPDATE gateway_security_lease_expiry_observations SET observed_at=observed_at", "DELETE FROM gateway_security_lease_expiry_observations", "TRUNCATE gateway_security_lease_expiry_observations" })
                Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
            return;
        }
        if (scenario is "newer-lease-issued-status" or "rollback-before-newer-lease-status")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            var newIssued = accepted.ReceivedAt.AddSeconds(2);
            var newCheckpointPayload = checkpointPayload with { IssuedAt = newIssued, FreshUntil = newIssued.AddSeconds(5) };
            var newCheckpointHash = SecuritySignedEncoding.CheckpointHash(newCheckpointPayload);
            var newLease = leasePayload with { LeaseId = Guid.NewGuid(), CheckpointHash = newCheckpointHash,
                IssuedAt = newIssued, FreshUntil = newCheckpointPayload.FreshUntil };
            var signedNewCheckpoint = signer.SignCheckpoint(newCheckpointPayload);
            var signedNewLease = signer.SignNodeLease(newLease);
            Assert.Equal(newCheckpointHash, SecurityCheckpointVerifier.VerifyCheckpoint(new(signedNewCheckpoint), settings.LoadTrustedKeys(), environment.Id).Hash);
            Assert.Equal(newLease.LeaseId, SecurityCheckpointVerifier.VerifyNodeLease(new(signedNewLease), settings.LoadTrustedKeys(), environment.Id, registered.NodeId, instance).Payload.LeaseId);
            var horizon = await db.Set<GatewaySecurityLease>().SingleAsync(x => x.NodeId == registered.NodeId && x.InstanceId == instance);
            horizon.LeaseId = newLease.LeaseId; horizon.CheckpointHash = newCheckpointHash; horizon.IssuedAt = newIssued;
            horizon.FreshUntil = newLease.FreshUntil; horizon.Revision++;
            db.Add(SecurityLeaseIssuanceEvidence.Create(newLease, organization.Id, project.Id));
            await db.SaveChangesAsync();
            clock.Freeze(scenario == "newer-lease-issued-status" ? newIssued.AddSeconds(1) : newIssued.AddSeconds(-1));
            // A newer signed lease has not been independently applied yet.
            // Neither its issuance nor the still-unexpired older receipt proves
            // the current node is enforcing the latest security restriction.
            Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            Assert.Single(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario == "expiry-during-status-read")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            clock.Freeze(accepted.FreshUntil.AddTicks(-10));
            advanceClock.AdvanceTo = accepted.FreshUntil;
            Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            Assert.Equal(1, advanceClock.HitCount);
            Assert.Single(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario == "expired-current-receipt-status")
        {
            await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            var readerForOperation = new SecurityOperationStatusReader(db);
            clock.Freeze(accepted.FreshUntil.AddTicks(-10));
            var beforeExpiry = await readerForOperation.ReadAsync(operationId);
            Assert.Equal("FullyApplied", beforeExpiry.Status);
            Assert.Equal(accepted.FreshUntil.AddTicks(-10), beforeExpiry.ObservedAt);
            Assert.Equal(accepted.FreshUntil, beforeExpiry.EarliestConfirmedLeaseExpiry);
            clock.Freeze(accepted.FreshUntil);
            var atExpiry = await readerForOperation.ReadAsync(operationId);
            Assert.Equal("Unconfirmed", atExpiry.Status);
            Assert.Equal(accepted.FreshUntil, atExpiry.ObservedAt);
            Assert.Null(atExpiry.EarliestConfirmedLeaseExpiry);
            clock.Freeze(accepted.FreshUntil.AddSeconds(1));
            Assert.Equal("Unconfirmed", (await readerForOperation.ReadAsync(operationId)).Status);
            // Losing current freshness must not erase or rewrite the historical ACK.
            Assert.Single(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            Assert.Single(await db.Set<SecurityAckRecord>().AsNoTracking().ToArrayAsync());

            // A newly issued and independently verified current lease restores
            // current coverage without fabricating a second operation ACK.
            var expiryObservation = await db.Set<GatewaySecurityLeaseExpiryObservation>().AsNoTracking().SingleAsync();
            var renewalIssued = expiryObservation.ObservedAt.AddTicks(10);
            clock.Freeze(renewalIssued);
            var renewedCheckpointPayload = checkpointPayload with { IssuedAt = renewalIssued, FreshUntil = renewalIssued.AddSeconds(20) };
            var renewedCheckpoint = signer.SignCheckpoint(renewedCheckpointPayload);
            var renewedHash = SecuritySignedEncoding.CheckpointHash(renewedCheckpointPayload);
            var renewedLeasePayload = leasePayload with { LeaseId = Guid.NewGuid(), CheckpointHash = renewedHash,
                IssuedAt = renewalIssued, FreshUntil = renewedCheckpointPayload.FreshUntil };
            var renewedLease = signer.SignNodeLease(renewedLeasePayload);
            var horizon = await db.Set<GatewaySecurityLease>().SingleAsync(x => x.NodeId == registered.NodeId && x.InstanceId == instance);
            horizon.LeaseId = renewedLeasePayload.LeaseId; horizon.CheckpointHash = renewedHash; horizon.IssuedAt = renewalIssued;
            horizon.FreshUntil = renewedLeasePayload.FreshUntil; horizon.LastIssuedLeaseUntil = renewedLeasePayload.FreshUntil; horizon.Revision++;
            db.Add(SecurityLeaseIssuanceEvidence.Create(renewedLeasePayload, organization.Id, project.Id));
            await db.SaveChangesAsync();
            await proof.RecordAsync(registered.NodeId, new CurrentSecurityProof(instance, renewedCheckpoint, renewedLease, renewalIssued, true, true), identity);
            Assert.Equal("FullyApplied", (await readerForOperation.ReadAsync(operationId)).Status);
            Assert.Single(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
            Assert.Single(await db.Set<SecurityAckRecord>().AsNoTracking().ToArrayAsync());
            Assert.True(clock.HitCount > 0);
            return;
        }
        if (scenario is "future-receipt-status" or "future-verification-status")
        {
            if (scenario == "future-receipt-status")
            {
                Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
                Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
                await proof.RecordAsync(registered.NodeId, proofRequest, identity);
            }
            else
            {
                await proof.RecordAsync(registered.NodeId, proofRequest, identity);
                Assert.False((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
            Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            }
            Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            var accepted = await db.Set<GatewaySecurityCurrentReceipt>().AsNoTracking().SingleAsync();
            var verified = await db.Set<SecurityAckVerification>().AsNoTracking().SingleAsync();
            if (scenario == "future-receipt-status") Assert.True(accepted.ReceivedAt > verified.ReceivedAt);
            else Assert.True(verified.ReceivedAt > accepted.ReceivedAt);
            clock.Freeze((scenario == "future-receipt-status" ? accepted.ReceivedAt : verified.ReceivedAt).AddTicks(-10));
            Assert.Equal("Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
            Assert.True(clock.HitCount > 0);
            return;
        }
        if (scenario is "bad-signature" or "wrong-challenge" or "wrong-instance" or "applied-after-expiry" or "lease-outlives-checkpoint" or "checkpoint-expired-lease-fresh" or "stale-node" or "unissued-lease-id" or "altered-lease-claims" or "no-issuance-evidence" or "applied-at-expiry" or "expired-first-ack-replay" or "future-latest-lease")
        {
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => service.RecordAsync(registered.NodeId, ack, identity))).Status);
            if (scenario == "future-latest-lease") Assert.True(clock.HitCount > 0);
            Assert.Empty(await db.Set<SecurityAckRecord>().AsNoTracking().ToArrayAsync());
            return;
        }
        if (scenario == "proof-first") await proof.RecordAsync(registered.NodeId, proofRequest, identity);
        var first = await service.RecordAsync(registered.NodeId, ack, identity);
        Assert.False(first.Duplicate);
        Assert.Equal(scenario == "proof-first" ? "FullyApplied" : "Unconfirmed", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
        if (scenario == "ack-first") await proof.RecordAsync(registered.NodeId, proofRequest, identity);
        Assert.Equal("FullyApplied", (await new SecurityOperationStatusReader(db).ReadAsync(operationId)).Status);
        Assert.True((await service.RecordAsync(registered.NodeId, ack, identity)).Duplicate);
        Assert.Single(await db.Set<SecurityAckRecord>().AsNoTracking().ToArrayAsync());
        Assert.Single(await db.Set<SecurityAckVerification>().AsNoTracking().ToArrayAsync());
        foreach (var sql in new[] { "UPDATE security_ack_verifications SET applied_at=applied_at", "DELETE FROM security_ack_verifications", "TRUNCATE security_ack_verifications" })
            Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
    }

    private sealed class CancelOnSecurityRead(AdvanceClockOnReceipt advanced) : DbCommandInterceptor
    {
        public CancellationTokenSource? Request { get; set; }
        public string TableName { get; set; } = "";
        public bool OnlyAfterClockAdvance { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Request is { } request && command.CommandText.Contains(TableName, StringComparison.Ordinal) && (!OnlyAfterClockAdvance || advanced.HitCount > 0))
            {
                Request = null; request.Cancel();
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CancelOnExpiryCommit : DbTransactionInterceptor
    {
        public CancellationTokenSource? Request { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Request is { } request) { Request = null; request.Cancel(); }
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    private sealed class CancelOnExpiryWrite : DbCommandInterceptor
    {
        public CancellationTokenSource? Request { get; set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Request is { } request && command.CommandText.Contains("INSERT INTO public.gateway_security_lease_expiry_observations", StringComparison.Ordinal))
            {
                Request = null;
                request.Cancel();
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class AdvisoryLockObserved : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock(8901202)", StringComparison.Ordinal)) Started.TrySetResult();
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class AdvanceClockOnReceipt(FrozenSecurityClockFixture clock) : DbCommandInterceptor
    {
        public DateTimeOffset? AdvanceTo { get; set; }
        public string TableName { get; set; } = "gateway_security_current_receipts";
        public int MatchesUntilAdvance { get; set; } = 1;
        public int HitCount { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (AdvanceTo is { } next && command.CommandText.Contains(TableName, StringComparison.Ordinal) && --MatchesUntilAdvance == 0)
            {
                AdvanceTo = null;
                HitCount++;
                clock.Freeze(next);
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class AdvanceClockOnChallengeSave(FrozenSecurityClockFixture clock) : SaveChangesInterceptor
    {
        public DateTimeOffset? AdvanceTo { get; set; }
        public int HitCount { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (AdvanceTo is { } next && eventData.Context!.ChangeTracker.Entries<SecurityAckRevalidationChallenge>().Any(e => e.State == EntityState.Added))
            {
                AdvanceTo = null;
                HitCount++;
                clock.Freeze(next);
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class DelayedFailureSave(string connectionString) : SaveChangesInterceptor
    {
        public Guid? ExpireNonce { get; set; }
        public Guid? BumpOperation { get; set; }
        public int HitCount { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (ExpireNonce is { } nonce && eventData.Context!.ChangeTracker.Entries<SecurityAckFailureLatest>().Any())
            {
                ExpireNonce = null;
                HitCount++;
                await using var observer = new NpgsqlConnection(connectionString);
                await observer.OpenAsync(cancellationToken);
                await using var command = observer.CreateCommand();
                command.CommandText = "SET session_replication_role=replica; UPDATE security_ack_revalidation_challenges SET created_at=pg_catalog.clock_timestamp()-interval '10 seconds',expires_at=pg_catalog.clock_timestamp()-interval '1 second' WHERE nonce=@nonce; SET session_replication_role=origin";
                command.Parameters.AddWithValue("nonce", nonce);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
            }
            else if (BumpOperation is { } operation && eventData.Context!.ChangeTracker.Entries<SecurityAckFailureLatest>().Any())
            {
                BumpOperation = null;
                HitCount++;
                await using var observer = new NpgsqlConnection(connectionString);
                await observer.OpenAsync(cancellationToken);
                await using var command = observer.CreateCommand();
                command.CommandText = "SET session_replication_role=replica; UPDATE security_ack_failure_latest SET latest_sequence=latest_sequence+1 WHERE operation_id=@operation; SET session_replication_role=origin";
                command.Parameters.AddWithValue("operation", operation);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}

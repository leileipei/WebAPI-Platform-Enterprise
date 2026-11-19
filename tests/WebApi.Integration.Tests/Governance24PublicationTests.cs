using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Security;
using WebApi.Domain.Applications;
using WebApi.Domain.CredentialSecurity;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Messaging;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Runtime;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class Governance24PublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPublishDoesNotProjectEnvironmentBoundCredential(bool sameEnvironment)
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var environment = scenario.Api.Environment;
        Guid boundCredentialId;
        await using (var db = scenario.Api.Context())
        {
            var boundEnvironment = environment.Id;
            if (!sameEnvironment)
            {
                var sibling = new EnvironmentRecord { ProjectId = environment.ProjectId, Code = "SIBLING", Name = "Sibling environment" };
                db.Add(sibling);
                await db.SaveChangesAsync();
                boundEnvironment = sibling.Id;
            }

            var legacy = await db.Set<ApplicationCredential>().AsNoTracking().SingleAsync();
            var bound = new ApplicationCredential { ApplicationId = legacy.ApplicationId, OrganizationId = legacy.OrganizationId,
                ProjectId = environment.ProjectId, AccessKey = "ak_bound_fixture", SecretHash = legacy.SecretHash,
                SecretLast4 = legacy.SecretLast4, ValidFrom = legacy.ValidFrom, ExpiresAt = legacy.ExpiresAt,
                BindingMode = "EnvironmentBound", EnvironmentId = boundEnvironment };
            boundCredentialId = bound.Id;
            db.Add(bound);
            await db.SaveChangesAsync();
        }

        var release = await scenario.PublishAsync(acknowledge: false);
        var artifact = await scenario.DesiredAsync(release);
        var runtime = SnapshotValidator.ParsePayload(artifact.Payload, environment.Id);
        Assert.Equal("2.0", runtime.SchemaVersion);
        var application = Assert.Single(runtime.Applications);
        Assert.DoesNotContain(application.Credentials, credential => credential.Id == boundCredentialId);
        Assert.Single(application.Credentials);
    }

    [Fact]
    public async Task LegacyPublishDoesNotProjectRouteMethodAsApiWideGrant()
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        await using (var db = scenario.Api.Context())
        {
            var permission = await db.Set<ApplicationApiPermission>().SingleAsync();
            var route = await db.Set<ApiRoute>().SingleAsync();
            permission.Mode = "RouteMethod";
            db.Add(new ApplicationRouteMethodGrant { PermissionId = permission.Id, RouteId = route.Id, ApiId = permission.ApiId,
                EnvironmentId = permission.EnvironmentId, Method = "GET", AuthorizationRevision = route.AuthorizationRevision });
            await db.SaveChangesAsync();
        }

        var release = await scenario.PublishAsync(acknowledge: false);
        var artifact = await scenario.DesiredAsync(release);
        var runtime = SnapshotValidator.ParsePayload(artifact.Payload, scenario.Api.Environment.Id);
        Assert.Empty(runtime.Applications);
    }

    [Fact]
    public async Task OptedInPublicationFreezesAndBuildsThroughNormalApproval()
    {
        await using var scenario = new DeploymentScenario();
        var continuity = new IsolatedContinuity();
        var (generation, boundId) = await PrepareGovernedAsync(scenario, continuity);

        var id = await scenario.ReadyAsync();
        await scenario.BuildAsync(id);
        var artifact = await scenario.DesiredAsync(id);
        var runtime = SnapshotValidator.ParsePayload(artifact.Payload, scenario.Api.Environment.Id);
        Assert.Equal("2.4", runtime.SchemaVersion);
        Assert.Equal(generation, runtime.RequiredSecurityGeneration);
        Assert.Equal(0L, runtime.RequiredSecurityEpoch);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(artifact.Payload)), artifact.Envelope.PayloadHash);
        var application = Assert.Single(runtime.Applications);
        var route = Assert.Single(runtime.Routes);
        var grant = Assert.Single(Assert.Single(application.Permissions).RouteMethods!);
        Assert.Equal(route.AuthorizationRevision, grant.AuthorizationRevision);
        Assert.True(PermissionEvaluator.IsAllowed(application, route, "GET", DateTimeOffset.UtcNow));
        Assert.Contains(application.Credentials, key => key.Id == boundId && key.BindingMode == "EnvironmentBound" && key.EnvironmentId == runtime.EnvironmentId);
        using var json = JsonDocument.Parse(artifact.Payload);
        var credentials = json.RootElement.GetProperty("applications")[0].GetProperty("credentials");
        var legacy = Assert.Single(credentials.EnumerateArray(), key => key.GetProperty("bindingMode").GetString() == "LegacyGlobal");
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("environmentId").ValueKind);

        await using var db = scenario.Api.Context();
        var release = await db.Set<ReleaseRecord>().SingleAsync(row => row.Id == id);
        Assert.Equal("Publishing", release.Status);
        Assert.Equal(2, await db.Set<ApprovalTask>().CountAsync(task => task.ReleaseId == id && task.Status == "Approved"));
        var targets = await db.Set<ReleaseTarget>().Where(target => target.ReleaseId == id).Select(target => target.InstanceId).ToArrayAsync();
        Assert.Equal(scenario.Instances.Select(instance => instance.ToString()).Order(), targets.Order());
        Assert.Single(await db.Set<OutboxMessage>().Where(item => item.ReleaseId == id).ToArrayAsync());
    }

    [Fact]
    public async Task GovernedReleaseRequiresVerifiedCurrentProofBeforeBothAcknowledgementsCanSucceed()
    {
        await using var scenario = new DeploymentScenario();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string signerId = "isolated-release";
        var signer = new SecurityCheckpointSigner(signerId, key.ExportPkcs8PrivateKeyPem());
        var verification = new SecuritySigningSettings(null, null, new Dictionary<string, string>
        {
            [signerId] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())
        });
        var continuity = new IsolatedContinuity();
        await PrepareGovernedAsync(scenario, continuity, configure: builder =>
        {
            builder.Services.AddSingleton(verification);
            builder.Services.AddScoped<SecurityLedgerReader>();
            builder.Services.AddScoped<CurrentSecurityProofService>();
        });
        var id = await scenario.PublishAsync(acknowledge: false);
        var desired = await scenario.DesiredAsync(id);
        using var redis = new RedisSnapshotStore("redis:6379", "governance-delivery:" + Guid.NewGuid());
        await using (var dispatch = scenario.Api.Context())
            Assert.True(await new OutboxDispatcher(dispatch, redis, continuity: continuity).DispatchNextAsync());

        using var premature = await scenario.NodeAsync(0, $"/internal/v1/nodes/{scenario.NodeIds[0]}/ack",
            new NodeAck(scenario.Instances[0], id, desired.Envelope.ConfigVersion, desired.Envelope.DeploymentSequence,
                desired.Envelope.PayloadHash, DateTimeOffset.UtcNow, true, null));
        Assert.Equal(HttpStatusCode.Conflict, premature.StatusCode);
        Assert.Contains("security_current_proof_conflict", await premature.Content.ReadAsStringAsync());

        await RecordCurrentProofsAsync(scenario, signer, signerId);
        await scenario.AckAllAsync(id);
        await using var verified = scenario.Api.Context();
        Assert.Equal("Succeeded", await verified.Set<ReleaseRecord>().Where(item => item.Id == id).Select(item => item.Status).SingleAsync());
        Assert.Equal(2, await verified.Set<GatewayAck>().CountAsync(item => item.ReleaseId == id && item.Success));
        Assert.NotNull(await verified.Set<OutboxMessage>().Where(item => item.ReleaseId == id).Select(item => item.ProcessedAt).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DestinationEditFencesPendingGovernedDeliveryAndAllowsPublishedRecovery(bool published)
    {
        await using var scenario = new DeploymentScenario();
        var continuity = new IsolatedContinuity();
        const string keyId = "isolated-delivery-revision";
        var (_, _, signer) = await PrepareRollbackAsync(scenario, continuity, keyId);
        var release = await scenario.PublishAsync(acknowledge: false);
        var frozen = await scenario.DesiredAsync(release);
        var nodeId = scenario.NodeIds[0];
        var instance = scenario.Instances[0];
        var desiredPath = $"/internal/v1/nodes/{nodeId}/desired?instanceId={instance}";
        using var original = await scenario.NodeGetAsync(0, desiredPath);
        Assert.True(original.IsSuccessStatusCode, await original.Content.ReadAsStringAsync());
        Assert.Equal(frozen.Envelope, (await original.Content.ReadFromJsonAsync<DesiredConfigResponse>())?.Envelope);
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        if (published) await scenario.AckAllAsync(release);

        var destination = scenario.Api.Destination;
        using var changed = await scenario.Api.WriteAsync(HttpMethod.Put, $"/api/v1/destinations/{destination.Id}",
            new SaveDestinationRequest(destination.Name, destination.Address, destination.Weight + 1, destination.Enabled, destination.Metadata),
            RevisionTag.Format(destination.Revision));
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        Assert.Equal(RevisionTag.Format(destination.Revision + 1), changed.Headers.ETag?.ToString());

        using var redis = new RedisSnapshotStore("redis:6379", "governance-revision:" + Guid.NewGuid());
        var written = false;
        bool dispatched;
        await using (var dispatch = scenario.Api.Context())
        {
            var dispatcher = new OutboxDispatcher(dispatch, redis, afterWrite: () =>
            {
                written = true;
                return Task.CompletedTask;
            }, continuity: continuity);
            dispatched = await dispatcher.DispatchNextAsync();
        }
        using var desired = await scenario.NodeGetAsync(0, desiredPath);
        using var ack = await scenario.NodeAsync(0, $"/internal/v1/nodes/{nodeId}/ack",
            new NodeAck(instance, release, frozen.Envelope.ConfigVersion, frozen.Envelope.DeploymentSequence,
                frozen.Envelope.PayloadHash, DateTimeOffset.UtcNow, true, null));
        var status = published ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
        Assert.Equal((status, published, published, status), (desired.StatusCode, dispatched, written, ack.StatusCode));
        if (published)
        {
            var reload = (await desired.Content.ReadFromJsonAsync<DesiredConfigResponse>())!;
            Assert.Equal(frozen.Envelope, reload.Envelope);
            Assert.Equal(frozen.Payload, reload.Payload);
        }
        else
        {
            Assert.Contains("credential_governance_delivery_unavailable", await desired.Content.ReadAsStringAsync());
            Assert.Contains("credential_governance_delivery_unavailable", await ack.Content.ReadAsStringAsync());
        }

        await using var check = scenario.Api.Context();
        Assert.Equal(published ? "Succeeded" : "Publishing", (await check.Set<ReleaseRecord>().SingleAsync(item => item.Id == release)).Status);
        Assert.Equal(published ? 2 : 0, await check.Set<GatewayAck>().CountAsync(item => item.ReleaseId == release && item.Success));
        Assert.Equal(published, (await check.Set<OutboxMessage>().SingleAsync(item => item.ReleaseId == release)).ProcessedAt.HasValue);
    }

    private static async Task RecordCurrentProofsAsync(DeploymentScenario scenario, SecurityCheckpointSigner signer, string signerId)
    {
        var proofs = new List<CurrentSecurityProof>();
        await using (var db = scenario.Api.Context())
        {
            var authority = await new SecurityLedgerReader(db).ReadCompleteAsync(scenario.Api.Environment.Id);
            var now = DateTimeOffset.UtcNow;
            var issuedAt = now.AddTicks(-(now.Ticks % 10));
            var freshUntil = issuedAt.AddSeconds(30);
            var revoked = authority.Revocations.Select(item => new SecurityRevocationProof(item.CredentialId, item.OperationId,
                item.Epoch, item.RevokedAt, authority.Events.Single(e => e.Epoch == item.Epoch).EventHash)).ToArray();
            var checkpoint = new SecurityCheckpointPayload(SecuritySignedEncoding.Protocol, authority.EnvironmentId,
                authority.SecurityGeneration, authority.Epoch, authority.HeadHash, revoked, authority.RequireEnvironmentCredentials,
                authority.MinimumSnapshotSchema, issuedAt, freshUntil, signerId);
            var checkpointHash = SecuritySignedEncoding.CheckpointHash(checkpoint);
            var signedCheckpoint = signer.SignCheckpoint(checkpoint);
            for (var index = 0; index < scenario.NodeIds.Length; index++)
            {
                var lease = new SecurityNodeLeasePayload(Guid.NewGuid(), SecuritySignedEncoding.Protocol, authority.EnvironmentId,
                    authority.SecurityGeneration, authority.Epoch, checkpointHash, scenario.NodeIds[index], scenario.Instances[index],
                    SecuritySignedEncoding.Audience, issuedAt, freshUntil, signerId);
                var current = await db.Set<GatewaySecurityLease>().SingleOrDefaultAsync(item =>
                    item.NodeId == lease.NodeId && item.InstanceId == lease.InstanceId);
                if (current is null)
                {
                    current = new() { NodeId = lease.NodeId, InstanceId = lease.InstanceId, EnvironmentId = lease.EnvironmentId,
                        OrganizationId = authority.OrganizationId, ProjectId = authority.ProjectId, SecurityGeneration = lease.SecurityGeneration };
                    db.Add(current);
                }
                else current.Revision++;
                current.LeaseId = lease.LeaseId;
                current.Epoch = lease.Epoch;
                current.CheckpointHash = checkpointHash;
                current.SigningKeyId = signerId;
                current.IssuedAt = issuedAt;
                current.FreshUntil = current.LastIssuedLeaseUntil = freshUntil;
                db.Add(SecurityLeaseIssuanceEvidence.Create(lease, authority.OrganizationId, authority.ProjectId));
                proofs.Add(new(lease.InstanceId, signedCheckpoint, signer.SignNodeLease(lease), issuedAt, true, true));
            }
            await db.SaveChangesAsync();
        }
        for (var index = 0; index < proofs.Count; index++)
        {
            using var response = await scenario.NodeAsync(index, $"/internal/v1/nodes/{scenario.NodeIds[index]}/security/current-proof", proofs[index]);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task GovernedRollbackPublishesHistoricalBusinessWithCurrentAuthorityAndFreshApproval()
    {
        await using var scenario = new DeploymentScenario();
        const string keyId = "isolated-rollback";
        var continuity = new IsolatedContinuity();
        var (securityGeneration, boundKey, signer) = await PrepareRollbackAsync(scenario, continuity, keyId);
        await using (var db = scenario.Api.Context())
        {
            var policy = new Policy { OrganizationId = scenario.Api.Organization.Id, ProjectId = scenario.Api.Project.Id,
                Name = "rollback fixture timeout", Type = "timeout", Config = """{"timeoutMs":4000}""" };
            db.Add(policy);
            db.Add(new RoutePolicyBinding { RouteId = (await db.Set<ApiRoute>().SingleAsync()).Id, PolicyId = policy.Id });
            await db.SaveChangesAsync();
        }
        var historyId = await scenario.PublishAsync(acknowledge: false);
        var historical = await scenario.DesiredAsync(historyId);
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        await scenario.AckAllAsync(historyId);
        var (operation, eventHash) = await ChangeRollbackBusinessAndAuthorityAsync(scenario, boundKey);

        var currentId = await PublishSameVersionAsync(scenario, historical.Envelope.ConfigVersion);
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        await scenario.AckAllAsync(currentId);
        var current = await scenario.DesiredAsync(currentId);
        using var created = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{currentId}/rollback",
            new CreateRollbackRequest(historical.Envelope.ConfigVersion));
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var rollbackId = (await created.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
        using var preview = await scenario.Api.Client.GetAsync($"/api/v1/releases/{rollbackId}/preview");
        Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
        using var impact = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.True(impact.RootElement.TryGetProperty("governedRollback", out var rollbackImpact));
        Assert.Equal(historical.Envelope.PayloadHash, rollbackImpact.GetProperty("sourceSnapshotHash").GetString());
        var routeImpact = Assert.Single(rollbackImpact.GetProperty("changedRoutes").EnumerateArray());
        Assert.True(routeImpact.GetProperty("clusterChanged").GetBoolean());
        Assert.True(routeImpact.GetProperty("timeoutChanged").GetBoolean());
        Assert.True(routeImpact.GetProperty("policiesChanged").GetBoolean());
        Assert.DoesNotContain("secretHash", await preview.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var submit = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{rollbackId}/submit");
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());
        using var early = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{rollbackId}/publish");
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        await scenario.ApproveAsync(rollbackId);
        await scenario.BuildAsync(rollbackId);
        var rebuilt = await scenario.DesiredAsync(rollbackId);
        Assert.True(rebuilt.Envelope.ConfigVersion > current.Envelope.ConfigVersion);
        Assert.True(rebuilt.Envelope.DeploymentSequence > current.Envelope.DeploymentSequence);
        var originalRuntime = SnapshotValidator.ParsePayload(historical.Payload, scenario.Api.Environment.Id);
        var restored = SnapshotValidator.ParsePayload(rebuilt.Payload, scenario.Api.Environment.Id);
        Assert.Equal("2.4", restored.SchemaVersion);
        Assert.Equal(CanonicalJson.Serialize(originalRuntime.Routes), CanonicalJson.Serialize(restored.Routes));
        Assert.Equal(CanonicalJson.Serialize(originalRuntime.Clusters), CanonicalJson.Serialize(restored.Clusters));
        Assert.Equal(CanonicalJson.Serialize(originalRuntime.Policies), CanonicalJson.Serialize(restored.Policies));
        Assert.Equal(securityGeneration, restored.RequiredSecurityGeneration);
        Assert.Equal(1L, restored.RequiredSecurityEpoch);
        var application = Assert.Single(restored.Applications);
        var key = Assert.Single(application.Credentials, credential => credential.Id == boundKey);
        Assert.Equal("Revoked", key.Status);
        var oldPermission = Assert.Single(Assert.Single(originalRuntime.Applications).Permissions);
        var currentPermission = Assert.Single(application.Permissions);
        Assert.Equal(oldPermission.ExpiresAt!.Value.AddMinutes(-10), currentPermission.ExpiresAt);
        using var redis = new RedisSnapshotStore("redis:6379", "governance-rollback:" + Guid.NewGuid());
        await using (var dispatch = scenario.Api.Context())
        {
            var dispatcher = new OutboxDispatcher(dispatch, redis, continuity: continuity);
            while (await dispatcher.DispatchNextAsync()) { }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => redis.GetDesiredAsync(scenario.Api.Environment.Id));
        for (var index = 0; index < scenario.NodeIds.Length; index++)
        {
            using var desired = await scenario.NodeGetAsync(index,
                $"/internal/v1/nodes/{scenario.NodeIds[index]}/desired?instanceId={scenario.Instances[index]}");
            Assert.True(desired.IsSuccessStatusCode, await desired.Content.ReadAsStringAsync());
            var delivered = (await desired.Content.ReadFromJsonAsync<DesiredConfigResponse>())!;
            Assert.Equal(rebuilt.Envelope, delivered.Envelope);
            Assert.Equal(rebuilt.Payload, delivered.Payload);
        }
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        await scenario.AckAllAsync(rollbackId);

        Assert.Equal(historical.Payload, (await scenario.DesiredAsync(historyId)).Payload);
        await using var final = scenario.Api.Context();
        var ledger = await final.Set<EnvironmentSecurityState>().SingleAsync();
        Assert.Equal((1L, eventHash), (ledger.Epoch, ledger.HeadHash));
        Assert.Equal(operation, (await final.Set<CredentialRevocation>().SingleAsync()).OperationId);
        Assert.Equal("Succeeded", (await final.Set<ReleaseRecord>().SingleAsync(row => row.Id == rollbackId)).Status);
        Assert.Equal(2, await final.Set<ApprovalTask>().CountAsync(task => task.ReleaseId == rollbackId && task.Status == "Approved"));
        Assert.Equal(2, await final.Set<GatewayAck>().CountAsync(ack => ack.ReleaseId == rollbackId && ack.Success));
        Assert.Equal(rebuilt.Envelope.ConfigVersion, (await final.Set<ReleaseFrozenContract>().SingleAsync(c => c.ReleaseId == rollbackId)).ConfigVersion);
        Assert.NotNull((await final.Set<OutboxMessage>().SingleAsync(item => item.ReleaseId == rollbackId)).ProcessedAt);
    }

    [Theory]
    [InlineData("missing-history", HttpStatusCode.Conflict, "governance_history_not_rebuildable")]
    [InlineData("authorization-change", HttpStatusCode.Conflict, "governance_rollback_authorization_changed")]
    [InlineData("continuity-unavailable", HttpStatusCode.ServiceUnavailable, "credential_governance_recovery_required")]
    public async Task GovernedRollbackRejectsUnavailableOrIncompatibleEvidence(string change, HttpStatusCode status, string code)
    {
        await using var scenario = new DeploymentScenario();
        const string keyId = "isolated-rollback-rejection";
        var continuity = new IsolatedContinuity();
        var (_, _, signer) = await PrepareRollbackAsync(scenario, continuity, keyId);
        var historicalId = await scenario.PublishAsync(acknowledge: false);
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        await scenario.AckAllAsync(historicalId);
        var historical = await scenario.DesiredAsync(historicalId);
        var currentId = await PublishSameVersionAsync(scenario, historical.Envelope.ConfigVersion);
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        await scenario.AckAllAsync(currentId);
        var current = await scenario.DesiredAsync(currentId);

        await using (var db = scenario.Api.Context())
        {
            if (change == "missing-history")
                await db.Set<GatewayAck>().Where(ack => ack.ReleaseId == historicalId && ack.NodeId == scenario.NodeIds[1])
                    .ExecuteDeleteAsync();
            else if (change == "authorization-change")
            {
                var route = await db.Set<ApiRoute>().SingleAsync();
                route.Path = route.NormalizedPath = "/changed";
                route.Revision++;
                await db.SaveChangesAsync();
            }
            else continuity.Available = false;
        }

        using var create = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{currentId}/rollback",
            new CreateRollbackRequest(historical.Envelope.ConfigVersion));
        Assert.Equal(status, create.StatusCode);
        Assert.Contains(code, await create.Content.ReadAsStringAsync());
        await using var final = scenario.Api.Context();
        Assert.False(await final.Set<ReleaseRecord>().AnyAsync(row => row.ReleaseType == "rollback"));
        var environment = await final.Set<EnvironmentRecord>().SingleAsync();
        Assert.Equal(current.Envelope.ConfigVersion, environment.DesiredConfigVersion);
        Assert.Equal(current.Envelope.DeploymentSequence, environment.DeploymentSequence);
    }

    [Theory]
    [InlineData(true, "governance_rollback_preconditions_changed")]
    [InlineData(false, "permission_denied")]
    public async Task GovernedRollbackRechecksInstanceAndPublisherAuthorityAfterQueueing(bool replaceInstance, string failureCode)
    {
        await using var scenario = new DeploymentScenario();
        const string keyId = "isolated-rollback-cohort";
        var (_, _, signer) = await PrepareRollbackAsync(scenario, new IsolatedContinuity(), keyId);
        var historicalId = await scenario.PublishAsync(acknowledge: false);
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        await scenario.AckAllAsync(historicalId);
        var historical = await scenario.DesiredAsync(historicalId);
        var currentId = await PublishSameVersionAsync(scenario, historical.Envelope.ConfigVersion);
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        await scenario.AckAllAsync(currentId);
        var current = await scenario.DesiredAsync(currentId);
        using var create = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{currentId}/rollback",
            new CreateRollbackRequest(historical.Envelope.ConfigVersion));
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var rollbackId = (await create.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
        using var submit = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{rollbackId}/submit");
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());
        await scenario.ApproveAsync(rollbackId);
        using var publish = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{rollbackId}/publish");
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());

        if (replaceInstance) await RegisterGovernedNodeAsync(scenario, 1, Guid.NewGuid());
        else
        {
            await using var db = scenario.Api.Context();
            var roles = db.Set<UserRole>().Where(role => role.UserId == scenario.Api.User.Id).Select(role => role.RoleId);
            var permission = await db.Set<Permission>().Where(row => row.Code == "release.rollback").Select(row => row.Id).SingleAsync();
            await db.Set<RolePermission>().Where(row => row.PermissionId == permission && roles.Contains(row.RoleId)).ExecuteDeleteAsync();
        }
        await scenario.BuildQueuedAsync();
        await using var final = scenario.Api.Context();
        var record = await final.Set<ReleaseRecord>().SingleAsync(row => row.Id == rollbackId);
        Assert.Equal("Failed", record.Status);
        Assert.Equal(failureCode, record.FailureCode);
        Assert.False(await final.Set<OutboxMessage>().AnyAsync(row => row.ReleaseId == rollbackId));
        var environment = await final.Set<EnvironmentRecord>().SingleAsync();
        Assert.Equal(current.Envelope.ConfigVersion, environment.DesiredConfigVersion);
        Assert.Equal(current.Envelope.DeploymentSequence, environment.DeploymentSequence);
    }

    private static async Task<(Guid Generation, Guid BoundKey, SecurityCheckpointSigner Signer)> PrepareRollbackAsync(
        DeploymentScenario scenario, IsolatedContinuity continuity, string keyId)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new SecurityCheckpointSigner(keyId, signingKey.ExportPkcs8PrivateKeyPem());
        var verification = new SecuritySigningSettings(null, null, new Dictionary<string, string>
        {
            [keyId] = Convert.ToBase64String(signingKey.ExportSubjectPublicKeyInfo())
        });
        var (generation, boundKey) = await PrepareGovernedAsync(scenario, continuity, configure: builder =>
        {
            builder.Services.AddSingleton(verification);
            builder.Services.AddScoped<SecurityLedgerReader>();
            builder.Services.AddScoped<CurrentSecurityProofService>();
        });
        return (generation, boundKey, signer);
    }

    [Fact]
    public async Task AuthorizationChangingBetweenApprovalsBlocksApprovalButAllowsRejection()
    {
        await using var scenario = new DeploymentScenario();
        const string keyId = "isolated-rollback-approval";
        var (_, _, signer) = await PrepareRollbackAsync(scenario, new IsolatedContinuity(), keyId);
        var historicalId = await scenario.PublishAsync(acknowledge: false);
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        await scenario.AckAllAsync(historicalId);
        var historical = await scenario.DesiredAsync(historicalId);
        var currentId = await PublishSameVersionAsync(scenario, historical.Envelope.ConfigVersion);
        await RecordCurrentProofsAsync(scenario, signer, keyId);
        await scenario.AckAllAsync(currentId);
        using var create = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{currentId}/rollback",
            new CreateRollbackRequest(historical.Envelope.ConfigVersion));
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        var rollbackId = (await create.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
        using var submit = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{rollbackId}/submit");
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());
        var frozen = (await submit.Content.ReadFromJsonAsync<ReleaseDto>())!;
        var apiReviewer = await scenario.Api.NewReviewerAsync("ApiApprover");
        using var first = await ApiFixture.CommandAsync(apiReviewer.Client, $"/api/v1/releases/{rollbackId}/approve",
            new ApprovalActionRequest("一级审批", 1, frozen.CandidateHash));
        Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());

        await using (var db = scenario.Api.Context())
        {
            var permission = await db.Set<ApplicationApiPermission>().SingleAsync();
            permission.ExpiresAt = permission.ExpiresAt!.Value.AddMinutes(-8);
            permission.Revision++;
            await db.SaveChangesAsync();
        }

        var securityReviewer = await scenario.Api.NewReviewerAsync("SecurityReviewer");
        using var second = await ApiFixture.CommandAsync(securityReviewer.Client, $"/api/v1/releases/{rollbackId}/approve",
            new ApprovalActionRequest("二级审批", 2, frozen.CandidateHash));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("governance_rollback_preconditions_changed", await second.Content.ReadAsStringAsync());
        await using (var db = scenario.Api.Context())
        {
            Assert.Equal("WaitingApproval", (await db.Set<ReleaseRecord>().SingleAsync(row => row.Id == rollbackId)).Status);
            var tasks = await db.Set<ApprovalTask>().Where(row => row.ReleaseId == rollbackId).OrderBy(row => row.StepOrder).ToArrayAsync();
            Assert.Equal(new[] { "Approved", "Pending" }, tasks.Select(task => task.Status));
        }

        using var rejected = await ApiFixture.CommandAsync(securityReviewer.Client, $"/api/v1/releases/{rollbackId}/reject",
            new ApprovalActionRequest("当前授权有变化", 2, frozen.CandidateHash));
        Assert.True(rejected.IsSuccessStatusCode, await rejected.Content.ReadAsStringAsync());
        Assert.Equal("Rejected", (await rejected.Content.ReadFromJsonAsync<ReleaseDto>())!.State);
    }

    private static async Task<Guid> PublishSameVersionAsync(DeploymentScenario scenario, long baseline)
    {
        var request = await scenario.Api.PreviewReleaseRequestAsync(baseline, scenario.Api.Version.Id);
        using var created = await ApiFixture.CommandAsync(scenario.Api.Client,
            $"/api/v1/environments/{scenario.Api.Environment.Id}/releases", request);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<ReleaseDto>())!.Id;
        using var submit = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{id}/submit");
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());
        await scenario.ApproveAsync(id);
        await scenario.BuildAsync(id);
        return id;
    }

    private static async Task<(Guid OperationId, string EventHash)> ChangeRollbackBusinessAndAuthorityAsync(
        DeploymentScenario scenario, Guid boundKey)
    {
        await using var db = scenario.Api.Context();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
        var destination = await db.Set<UpstreamDestination>().SingleAsync();
        destination.Address = "http://test-backend:8080/current/";
        destination.Revision++;
        var route = await db.Set<ApiRoute>().SingleAsync();
        route.TimeoutMs += 1000;
        route.Revision++;
        var policy = await db.Set<Policy>().SingleAsync(item => item.Type == "timeout");
        policy.Config = """{"timeoutMs":8000}""";
        policy.VersionNo++;
        var permission = await db.Set<ApplicationApiPermission>().SingleAsync();
        permission.ExpiresAt = permission.ExpiresAt!.Value.AddMinutes(-10);
        permission.Revision++;
        var credential = await db.Set<ApplicationCredential>().SingleAsync(item => item.Id == boundKey);
        var authority = await db.Set<EnvironmentSecurityState>().SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var occurred = now.AddTicks(-(now.Ticks % 10));
        credential.Status = "Revoked";
        credential.RevokedAt = occurred;
        credential.Revision++;
        var operation = new SecurityOperation { OrganizationId = authority.OrganizationId, ApplicationId = credential.ApplicationId,
            CredentialId = credential.Id, RequestedBy = scenario.Api.User.Id, Reason = "isolated rollback continuity", CommittedAt = occurred };
        var item = new SecurityEvent { EnvironmentId = authority.EnvironmentId, OrganizationId = authority.OrganizationId,
            ProjectId = authority.ProjectId, SecurityGeneration = authority.SecurityGeneration, Epoch = 1,
            OperationId = operation.Id, CredentialId = credential.Id, PreviousHash = authority.HeadHash, OccurredAt = occurred };
        item.EventHash = SecurityEventHash.Compute(item);
        db.AddRange(operation, item, new CredentialRevocation { EnvironmentId = authority.EnvironmentId,
            OrganizationId = authority.OrganizationId, ProjectId = authority.ProjectId, CredentialId = credential.Id,
            ApplicationId = credential.ApplicationId, SecurityGeneration = authority.SecurityGeneration, Epoch = item.Epoch,
            OperationId = operation.Id, RevokedAt = occurred });
        authority.Epoch = item.Epoch;
        authority.HeadHash = item.EventHash;
        authority.Revision++;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return (operation.Id, item.EventHash);
    }

    [Fact]
    public async Task GovernancePublicationKeepsOnlyCurrentMethodWhenAnotherMethodIsStale()
    {
        await using var scenario = new DeploymentScenario();
        await PrepareGovernedAsync(scenario, new IsolatedContinuity());
        await using (var db = scenario.Api.Context())
        {
            var route = await db.Set<ApiRoute>().SingleAsync();
            route.Methods = ["GET", "POST"];
            route.Revision++;
            await db.SaveChangesAsync();
            await db.Entry(route).ReloadAsync();
            var permission = await db.Set<ApplicationApiPermission>().SingleAsync();
            permission.Revision++;
            db.Add(new ApplicationRouteMethodGrant { PermissionId = permission.Id, RouteId = route.Id, ApiId = permission.ApiId,
                EnvironmentId = permission.EnvironmentId, Method = "POST", AuthorizationRevision = route.AuthorizationRevision });
            await db.SaveChangesAsync();
        }

        var id = await scenario.PublishAsync(acknowledge: false);
        var artifact = await scenario.DesiredAsync(id);
        var runtime = SnapshotValidator.ParsePayload(artifact.Payload, scenario.Api.Environment.Id);
        var application = Assert.Single(runtime.Applications);
        var publishedRoute = Assert.Single(runtime.Routes);
        Assert.True(PermissionEvaluator.IsAllowed(application, publishedRoute, "POST", DateTimeOffset.UtcNow));
        Assert.False(PermissionEvaluator.IsAllowed(application, publishedRoute, "GET", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("instance", "governance_snapshot_preconditions_changed")]
    [InlineData("continuity", "credential_governance_recovery_required")]
    [InlineData("approval_policy", "governance_snapshot_preconditions_changed")]
    public async Task GovernanceBuildRejectsAChangeAfterQueueWithoutMovingDesired(string change, string failureCode)
    {
        await using var scenario = new DeploymentScenario();
        var continuity = new IsolatedContinuity();
        await PrepareGovernedAsync(scenario, continuity);
        var id = await scenario.ReadyAsync();
        using var publish = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{id}/publish");
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());

        if (change == "instance") await RegisterGovernedNodeAsync(scenario, 1, Guid.NewGuid());
        else if (change == "continuity") continuity.Available = false;
        else
        {
            await using var changed = scenario.Api.Context();
            await changed.Set<ApprovalFlow>().Where(flow => flow.Id == scenario.Api.ApprovalFlow.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(flow => flow.Revision, flow => flow.Revision + 1));
        }
        await scenario.BuildQueuedAsync();

        await using var db = scenario.Api.Context();
        var release = await db.Set<ReleaseRecord>().SingleAsync(row => row.Id == id);
        Assert.Equal("Failed", release.Status);
        Assert.Equal(failureCode, release.FailureCode);
        Assert.Null(await db.Set<EnvironmentRecord>().Where(env => env.Id == release.EnvironmentId).Select(env => env.DesiredConfigVersion).SingleAsync());
        Assert.Empty(await db.Set<GatewayConfigVersion>().ToArrayAsync());
        Assert.Empty(await db.Set<ReleaseTarget>().ToArrayAsync());
        Assert.Empty(await db.Set<OutboxMessage>().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Conflict, "gateway_schema_unsupported")]
    [InlineData(true, HttpStatusCode.ServiceUnavailable, "credential_governance_recovery_required")]
    public async Task GovernancePreviewRequiresCurrentContinuityAndEveryEnabledNode(bool missingContinuity, HttpStatusCode status, string error)
    {
        await using var scenario = new DeploymentScenario();
        await PrepareGovernedAsync(scenario, missingContinuity ? null : new IsolatedContinuity(), registerBoth: false);
        using var response = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/environments/{scenario.Api.Environment.Id}/releases/preview",
            new PreviewReleaseRequest(0, [scenario.Api.Version.Id]));
        var problem = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, problem);
        Assert.Contains(error, problem);
    }

    [Fact]
    public async Task DisablingOptInCannotRollbackAGovernanceDeploymentToLegacyBytes()
    {
        await using var scenario = new DeploymentScenario();
        HashSet<Guid> enabledEnvironments = [];
        await PrepareGovernedAsync(scenario, new IsolatedContinuity(), enabledEnvironments: enabledEnvironments);
        var legacyId = await scenario.PublishAsync();
        var legacy = await scenario.DesiredAsync(legacyId);
        Assert.Equal("2.0", SnapshotValidator.ParsePayload(legacy.Payload, scenario.Api.Environment.Id).SchemaVersion);

        enabledEnvironments.Add(scenario.Api.Environment.Id);
        var governedId = await scenario.PublishAsync(acknowledge: false);
        var governed = await scenario.DesiredAsync(governedId);
        Assert.Equal("2.4", SnapshotValidator.ParsePayload(governed.Payload, scenario.Api.Environment.Id).SchemaVersion);
        await using (var db = scenario.Api.Context())
        {
            var release = await db.Set<ReleaseRecord>().SingleAsync(row => row.Id == governedId);
            release.Status = "Failed";
            release.FailureCode = "ack_timeout";
            await db.SaveChangesAsync();
        }

        enabledEnvironments.Clear();
        using var create = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{governedId}/rollback",
            new CreateRollbackRequest(legacy.Envelope.ConfigVersion));
        Assert.True(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());
        using var rollback = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var rollbackId = rollback.RootElement.GetProperty("id").GetGuid();
        using var submit = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{rollbackId}/submit");
        Assert.True(submit.IsSuccessStatusCode, await submit.Content.ReadAsStringAsync());
        await scenario.ApproveAsync(rollbackId);

        using var publish = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{rollbackId}/publish");
        var problem = await publish.Content.ReadAsStringAsync();
        Assert.True(publish.StatusCode == HttpStatusCode.Conflict, problem);
        Assert.Contains("governance_history_rebuild_required", problem);
        Assert.Equal(legacy.Payload, (await scenario.DesiredAsync(legacyId)).Payload);
        await using var check = scenario.Api.Context();
        var desired = await check.Set<EnvironmentRecord>().SingleAsync(env => env.Id == scenario.Api.Environment.Id);
        Assert.Equal(governed.Envelope.ConfigVersion, desired.DesiredConfigVersion);
        Assert.Equal(governed.Envelope.DeploymentSequence, desired.DeploymentSequence);
    }

    private static async Task<(Guid Generation, Guid BoundId)> PrepareGovernedAsync(DeploymentScenario scenario,
        IsolatedContinuity? continuity, bool registerBoth = true, HashSet<Guid>? enabledEnvironments = null,
        Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configure = null)
    {
        var api = scenario.Api;
        await scenario.InitializeAsync(builder =>
        {
            builder.Services.AddSingleton(new PublishSettings(GovernanceSnapshot24Environments: enabledEnvironments ?? new HashSet<Guid> { api.Environment.Id }));
            if (continuity is not null) builder.Services.AddSingleton<IGovernanceRecoveryContinuity>(continuity);
            configure?.Invoke(builder);
        });
        Guid generation;
        Guid boundId;
        await using (var db = api.Context())
        {
            var state = new EnvironmentSecurityState { EnvironmentId = api.Environment.Id, OrganizationId = api.Organization.Id,
                ProjectId = api.Project.Id, UpdatedAt = DateTimeOffset.UtcNow };
            generation = state.SecurityGeneration;
            db.Add(state);

            var legacy = await db.Set<ApplicationCredential>().AsNoTracking().SingleAsync();
            var bound = new ApplicationCredential { ApplicationId = legacy.ApplicationId, OrganizationId = legacy.OrganizationId,
                ProjectId = api.Project.Id, AccessKey = "ak_governed_isolated", SecretHash = legacy.SecretHash,
                SecretLast4 = legacy.SecretLast4, ValidFrom = legacy.ValidFrom, ExpiresAt = legacy.ExpiresAt,
                BindingMode = "EnvironmentBound", EnvironmentId = api.Environment.Id };
            boundId = bound.Id;
            db.Add(bound);

            var parent = await db.Set<ApplicationApiPermission>().SingleAsync();
            var route = await db.Set<ApiRoute>().SingleAsync();
            parent.Mode = "RouteMethod";
            db.Add(new ApplicationRouteMethodGrant { PermissionId = parent.Id, RouteId = route.Id, ApiId = parent.ApiId,
                EnvironmentId = parent.EnvironmentId, Method = "GET", AuthorizationRevision = route.AuthorizationRevision });
            await db.SaveChangesAsync();
        }
        await RegisterGovernedNodeAsync(scenario, 0, scenario.Instances[0]);
        if (registerBoth) await RegisterGovernedNodeAsync(scenario, 1, scenario.Instances[1]);
        return (generation, boundId);
    }

    private static async Task RegisterGovernedNodeAsync(DeploymentScenario scenario, int index, Guid instance)
    {
        using var response = await scenario.NodeAsync(index, "/internal/v1/nodes/register",
            new RegisterNodeRequest(scenario.Api.Environment.Id, $"node-{index}", instance, "isolated", ["2.0", "2.4"], ["security-ledger-v1"]));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed class IsolatedContinuity : IGovernanceRecoveryContinuity
    {
        private readonly GovernanceJournalId journal = new(Guid.NewGuid(), Guid.NewGuid());
        public bool Available { get; set; } = true;

        public async Task<GovernanceContinuity> RequireAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct = default)
        {
            if (!Available) throw new InvalidDataException("credential_governance_recovery_continuity_unavailable");
            var ledger = await new SecurityLedgerReader(db).ReadCompleteLockedAsync(environmentId, ct);
            return new(journal, GovernanceJournalProtocol.Genesis, ledger);
        }
    }
}

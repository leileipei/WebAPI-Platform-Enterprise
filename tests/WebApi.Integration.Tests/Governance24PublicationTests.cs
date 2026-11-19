using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Releases;
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

        var proofs = new List<CurrentSecurityProof>();
        await using (var db = scenario.Api.Context())
        {
            var authority = await new SecurityLedgerReader(db).ReadCompleteAsync(scenario.Api.Environment.Id);
            var now = DateTimeOffset.UtcNow;
            var issuedAt = now.AddTicks(-(now.Ticks % 10));
            var freshUntil = issuedAt.AddSeconds(30);
            var checkpoint = new SecurityCheckpointPayload(SecuritySignedEncoding.Protocol, authority.EnvironmentId,
                authority.SecurityGeneration, authority.Epoch, authority.HeadHash, [], authority.RequireEnvironmentCredentials,
                authority.MinimumSnapshotSchema, issuedAt, freshUntil, signerId);
            var checkpointHash = SecuritySignedEncoding.CheckpointHash(checkpoint);
            var signedCheckpoint = signer.SignCheckpoint(checkpoint);
            for (var index = 0; index < scenario.NodeIds.Length; index++)
            {
                var lease = new SecurityNodeLeasePayload(Guid.NewGuid(), SecuritySignedEncoding.Protocol, authority.EnvironmentId,
                    authority.SecurityGeneration, authority.Epoch, checkpointHash, scenario.NodeIds[index], scenario.Instances[index],
                    SecuritySignedEncoding.Audience, issuedAt, freshUntil, signerId);
                db.Add(new GatewaySecurityLease
                {
                    LeaseId = lease.LeaseId, NodeId = lease.NodeId, InstanceId = lease.InstanceId, EnvironmentId = lease.EnvironmentId,
                    OrganizationId = authority.OrganizationId, ProjectId = authority.ProjectId, SecurityGeneration = lease.SecurityGeneration,
                    Epoch = lease.Epoch, CheckpointHash = checkpointHash, SigningKeyId = signerId,
                    IssuedAt = issuedAt, FreshUntil = freshUntil, LastIssuedLeaseUntil = freshUntil
                });
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
        await scenario.AckAllAsync(id);
        await using var verified = scenario.Api.Context();
        Assert.Equal("Succeeded", await verified.Set<ReleaseRecord>().Where(item => item.Id == id).Select(item => item.Status).SingleAsync());
        Assert.Equal(2, await verified.Set<GatewayAck>().CountAsync(item => item.ReleaseId == id && item.Success));
        Assert.NotNull(await verified.Set<OutboxMessage>().Where(item => item.ReleaseId == id).Select(item => item.ProcessedAt).SingleAsync());
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
            db.Add(new EnvironmentSecurityInitialization { EnvironmentId = api.Environment.Id, OrganizationId = api.Organization.Id,
                ProjectId = api.Project.Id, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
            await db.SaveChangesAsync();
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

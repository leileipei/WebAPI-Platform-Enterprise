using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GlobalCredentialPublicationTests
{
    [Theory]
    [InlineData("rollback", true)]
    [InlineData("retry", true)]
    [InlineData("publish", true)]
    [InlineData("rollback", false)]
    public async Task QueuedPublicationChecksTheGlobalFactBeforeMovingDesired(string releaseType, bool immediateRevocation)
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var (releaseId, firstReleaseId) = await QueueAsync(scenario, releaseType);
        var before = await ReadDeploymentAsync(scenario);

        await RevokeAsync(scenario, immediateRevocation);
        await scenario.BuildQueuedAsync();

        await using var db = scenario.Api.Context();
        var release = await db.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r => r.Id == releaseId);
        var after = await ReadDeploymentAsync(scenario);
        if (!immediateRevocation)
        {
            Assert.Equal("Publishing", release.Status);
            Assert.Equal(before.Sequence + 1, after.Sequence);
            Assert.Equal(1L, after.Version);
            Assert.Equal(before.Configs, after.Configs);
            Assert.Equal(before.Deliveries + 1, after.Deliveries);
            Assert.Equal((await scenario.DesiredAsync(firstReleaseId)).Payload,
                (await scenario.DesiredAsync(releaseId)).Payload);
            return;
        }

        Assert.Equal("Failed", release.Status);
        Assert.Equal("global_revocation_inheritance_required", release.FailureCode);
        Assert.Equal(before, after);
        Assert.False(await db.Set<ReleaseTarget>().AnyAsync(target => target.ReleaseId == releaseId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SiblingFactRequiresInheritanceWhenTheNewSnapshotUsesTheApplication(bool usesApplication)
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var origin = new EnvironmentRecord { ProjectId = scenario.Api.Project.Id, Code = "ORIGIN", Name = "Revocation origin" };
        await using (var db = scenario.Api.Context())
        {
            db.Add(origin);
            await db.SaveChangesAsync();
            if (!usesApplication)
                await db.Set<ApplicationApiPermission>().Where(grant => grant.EnvironmentId == scenario.Api.Environment.Id).ExecuteDeleteAsync();
        }
        var releaseId = await scenario.ReadyAsync();
        using var request = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{releaseId}/publish");
        request.EnsureSuccessStatusCode();
        var before = await ReadDeploymentAsync(scenario);

        await RevokeAsync(scenario, immediate: true, originalEnvironmentId: origin.Id);
        await scenario.BuildQueuedAsync();

        await using var check = scenario.Api.Context();
        var tombstone = await check.Set<CredentialRevocation>().AsNoTracking().SingleAsync();
        Assert.Equal(origin.Id, tombstone.EnvironmentId);
        var release = await check.Set<ReleaseRecord>().AsNoTracking().SingleAsync(r => r.Id == releaseId);
        if (usesApplication)
        {
            Assert.Equal("Failed", release.Status);
            Assert.Equal("global_revocation_inheritance_required", release.FailureCode);
            Assert.Equal(before, await ReadDeploymentAsync(scenario));
        }
        else
        {
            Assert.Equal("Publishing", release.Status);
            var desired = await scenario.DesiredAsync(releaseId);
            var snapshot = SnapshotValidator.ParsePayload(desired.Payload, scenario.Api.Environment.Id);
            Assert.Empty(snapshot.Applications);
        }
    }

    [Fact]
    public async Task GovernanceSnapshotMustMatchTheLocalLedgerAndInheritTheGlobalRevocation()
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var app = scenario.Api.Application;
        var origin = scenario.Api.Environment;
        await RevokeAsync(scenario, immediate: true);

        await using var db = scenario.Api.Context();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
        var sibling = new EnvironmentRecord { ProjectId = origin.ProjectId, Code = "SIBLING", Name = "No inheritance" };
        db.Add(sibling);
        await db.SaveChangesAsync();
        var siblingLedger = await InitializeLedgerAsync(db, sibling.Id, app.OrganizationId, origin.ProjectId, DateTimeOffset.UtcNow);
        var originLedger = await db.Set<EnvironmentSecurityState>().AsNoTracking().SingleAsync(s => s.EnvironmentId == origin.Id);

        RuntimeSnapshot Snapshot(EnvironmentSecurityState state) => new("2.4", state.EnvironmentId, 1, DateTimeOffset.UtcNow,
            [], [], [], [new RuntimeApplication(app.Id, "Active", [], [], state.EnvironmentId)],
            state.SecurityGeneration, state.Epoch, state.RequireEnvironmentCredentials, state.MinimumSnapshotSchema);

        Task<string> Validate(EnvironmentRecord environment, RuntimeSnapshot snapshot) =>
            GlobalCredentialPublicationGuard.ValidateAsync(db, environment, app.OrganizationId, CanonicalJson.Serialize(snapshot), default);

        var current = Snapshot(originLedger);
        Assert.Equal("2.4", await Validate(origin, current));
        var staleEpoch = await Assert.ThrowsAsync<ApiException>(() => Validate(origin, current with { RequiredSecurityEpoch = 0 }));
        var wrongGeneration = await Assert.ThrowsAsync<ApiException>(() => Validate(origin, current with { RequiredSecurityGeneration = Guid.NewGuid() }));
        var missingTombstone = await Assert.ThrowsAsync<ApiException>(() => Validate(sibling, Snapshot(siblingLedger)));
        Assert.All(new[] { staleEpoch, wrongGeneration, missingTombstone }, failure =>
            Assert.Equal("global_revocation_inheritance_required", failure.Code));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("renew")]
    [InlineData("move")]
    [InlineData("stale")]
    public async Task LegacyGrantCannotBeSavedToAnEnvironmentMissingTheApplicationsRevocation(string change)
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var api = scenario.Api;
        var origin = new EnvironmentRecord { ProjectId = api.Project.Id, Code = "ORIGIN", Name = "Revocation origin" };
        var sibling = new EnvironmentRecord { ProjectId = api.Project.Id, Code = "TARGET", Name = "No inheritance" };
        ApplicationApiPermission grant;
        long appRevision;
        await using (var db = api.Context())
        {
            db.AddRange(origin, sibling);
            await db.SaveChangesAsync();
            grant = await db.Set<ApplicationApiPermission>().AsNoTracking().SingleAsync();
            appRevision = await db.Set<ApplicationRecord>().Where(a => a.Id == api.Application.Id).Select(a => a.Revision).SingleAsync();
        }
        await RevokeAsync(scenario, immediate: true, originalEnvironmentId: origin.Id);

        var path = $"/api/v1/applications/{api.Application.Id}/permissions";
        var target = change is "renew" or "stale" ? grant.EnvironmentId : sibling.Id;
        var values = new { environmentId = target, apiId = api.Api.Id, grant.ValidFrom, expiresAt = grant.ExpiresAt!.Value.AddDays(1) };
        using var response = change == "create" ? await api.WriteAsync(HttpMethod.Post, path, values) :
            await api.WriteAsync(HttpMethod.Put, $"{path}/{grant.Id}", values,
                RevisionTag.Format(change == "stale" ? grant.Revision + 1 : grant.Revision));

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(change == "stale" ? HttpStatusCode.PreconditionFailed : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(change == "stale" ? "stale_revision" : "global_revocation_inheritance_required", problem.GetProperty("code").GetString());
        await using var check = api.Context();
        var actual = await check.Set<ApplicationApiPermission>().AsNoTracking().SingleAsync();
        Assert.Equal((grant.Id, grant.EnvironmentId, grant.ExpiresAt, grant.Revision),
            (actual.Id, actual.EnvironmentId, actual.ExpiresAt, actual.Revision));
        Assert.Equal(appRevision, await check.Set<ApplicationRecord>().Where(a => a.Id == api.Application.Id).Select(a => a.Revision).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyGrantAllowsInheritedOrOrdinaryRevocationAndAllowsRemoval(bool immediate)
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var api = scenario.Api;
        var origin = new EnvironmentRecord { ProjectId = api.Project.Id, Code = "ORIGIN", Name = "Revocation origin" };
        var otherApp = new ApplicationRecord { OrganizationId = api.Organization.Id, ProjectId = api.Project.Id,
            Code = "OTHER", Name = "Unrelated application", Owner = "Test" };
        ApplicationApiPermission grant;
        await using (var db = api.Context())
        {
            db.AddRange(origin, otherApp);
            await db.SaveChangesAsync();
            grant = await db.Set<ApplicationApiPermission>().AsNoTracking().SingleAsync();
        }
        await RevokeAsync(scenario, immediate, origin.Id);

        object Values(Guid environmentId) => new { environmentId, apiId = api.Api.Id, grant.ValidFrom, grant.ExpiresAt };
        using var inherited = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{api.Application.Id}/permissions", Values(origin.Id));
        inherited.EnsureSuccessStatusCode();
        using var unrelated = await api.WriteAsync(HttpMethod.Post, $"/api/v1/applications/{otherApp.Id}/permissions", Values(grant.EnvironmentId));
        unrelated.EnsureSuccessStatusCode();
        using var removed = await api.WriteAsync(HttpMethod.Delete, $"/api/v1/application-permissions/{grant.Id}", etag: RevisionTag.Format(grant.Revision));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        await using var check = api.Context();
        var remaining = await check.Set<ApplicationApiPermission>().AsNoTracking().ToArrayAsync();
        Assert.Equal(2, remaining.Length);
        Assert.Single(remaining, item => item.ApplicationId == api.Application.Id && item.EnvironmentId == origin.Id);
        Assert.Single(remaining, item => item.ApplicationId == otherApp.Id && item.EnvironmentId == grant.EnvironmentId);
    }

    private static async Task<(Guid Queued, Guid First)> QueueAsync(DeploymentScenario scenario, string releaseType)
    {
        var first = await scenario.PublishAsync();
        var id = releaseType switch
        {
            "publish" => await scenario.ReadyAsync(),
            "rollback" => await CreateRollbackAsync(scenario),
            "retry" => await CreateRetryAsync(scenario),
            _ => throw new ArgumentOutOfRangeException(nameof(releaseType))
        };
        if (releaseType != "retry")
        {
            using var publish = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{id}/publish");
            publish.EnsureSuccessStatusCode();
        }
        return (id, first);
    }

    private static async Task<Guid> CreateRollbackAsync(DeploymentScenario scenario)
    {
        var current = await scenario.PublishAsync();
        using var response = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{current}/rollback",
            new { targetConfigVersion = 1L });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var submit = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{id}/submit");
        submit.EnsureSuccessStatusCode();
        await scenario.ApproveAsync(id);
        return id;
    }

    private static async Task<Guid> CreateRetryAsync(DeploymentScenario scenario)
    {
        var current = await scenario.PublishAsync(acknowledge: false);
        await using (var db = scenario.Api.Context())
        {
            var failed = await db.Set<ReleaseRecord>().SingleAsync(r => r.Id == current);
            failed.Status = "Failed";
            failed.FailureCode = "ack_timeout";
            await db.SaveChangesAsync();
        }
        using var response = await ApiFixture.CommandAsync(scenario.Api.Client, $"/api/v1/releases/{current}/retry");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task RevokeAsync(DeploymentScenario scenario, bool immediate, Guid? originalEnvironmentId = null)
    {
        await using var db = scenario.Api.Context();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_catalog.pg_advisory_xact_lock(8901202)");
        var credential = await db.Set<ApplicationCredential>().SingleAsync();
        Assert.Equal("LegacyGlobal", credential.BindingMode);
        var now = DateTimeOffset.UtcNow;
        var revokedAt = now.AddTicks(-(now.Ticks % 10));
        credential.Status = "Revoked";
        credential.RevokedAt = revokedAt;
        credential.Revision++;
        if (immediate)
        {
            // Model committed authority facts so the normal worker runs with the same old bytes and a later ledger.
            var environmentId = originalEnvironmentId ?? scenario.Api.Environment.Id;
            var organizationId = scenario.Api.Organization.Id;
            var projectId = scenario.Api.Project.Id;
            var state = await InitializeLedgerAsync(db, environmentId, organizationId, projectId, revokedAt);
            var operation = new SecurityOperation { OrganizationId = organizationId, ApplicationId = credential.ApplicationId,
                CredentialId = credential.Id, RequestedBy = scenario.Api.User.Id, Reason = "isolated-test", CommittedAt = revokedAt };
            var item = new SecurityEvent { EnvironmentId = environmentId, OrganizationId = organizationId, ProjectId = projectId,
                SecurityGeneration = state.SecurityGeneration, Epoch = 1, OperationId = operation.Id, CredentialId = credential.Id,
                PreviousHash = state.HeadHash, OccurredAt = revokedAt };
            item.EventHash = SecurityEventHash.Compute(item);
            db.AddRange(operation, item, new CredentialRevocation { EnvironmentId = environmentId, OrganizationId = organizationId,
                ProjectId = projectId, CredentialId = credential.Id, ApplicationId = credential.ApplicationId,
                SecurityGeneration = state.SecurityGeneration, Epoch = item.Epoch, OperationId = operation.Id, RevokedAt = revokedAt });
            state.Epoch = item.Epoch;
            state.HeadHash = item.EventHash;
            state.Revision++;
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static async Task<EnvironmentSecurityState> InitializeLedgerAsync(WebApiDbContext db, Guid environmentId,
        Guid organizationId, Guid projectId, DateTimeOffset initializedAt)
    {
        if (!await db.Set<EnvironmentSecurityInitialization>().AnyAsync(i => i.EnvironmentId == environmentId))
        {
            db.Add(new EnvironmentSecurityInitialization { EnvironmentId = environmentId, OrganizationId = organizationId,
                ProjectId = projectId, State = "FreshNonStrict", FreshProofId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }
        var state = new EnvironmentSecurityState { EnvironmentId = environmentId, OrganizationId = organizationId,
            ProjectId = projectId, UpdatedAt = initializedAt };
        db.Add(state);
        await db.SaveChangesAsync();
        return state;
    }

    private static async Task<(long Sequence, long? Version, int Configs, int Deliveries)> ReadDeploymentAsync(DeploymentScenario scenario)
    {
        await using var db = scenario.Api.Context();
        var env = await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e => e.Id == scenario.Api.Environment.Id);
        return (env.DeploymentSequence, env.DesiredConfigVersion, await db.Set<GatewayConfigVersion>().CountAsync(),
            await db.Set<OutboxMessage>().CountAsync());
    }
}

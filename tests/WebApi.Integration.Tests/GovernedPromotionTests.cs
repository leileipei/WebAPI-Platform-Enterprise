using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Releases;
using WebApi.Domain.CredentialSecurity;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Gateway;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class GovernedPromotionTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("global-key")]
    [InlineData("foreign-key")]
    [InlineData("missing-target-route")]
    [InlineData("missing-route-grant")]
    [InlineData("grant-changed-after-approval")]
    public async Task OnlyCurrentApprovedTargetAuthorizationCanBuildGovernedPromotion(string scenario)
    {
        await using var fixture = new PromotionScenario();
        var enabled = new HashSet<Guid>();
        await fixture.InitializeAsync(configureSourceAuthentication: async api =>
        {
            await using var db = api.Context();
            await AddFreshLedgerAsync(db, api.Environment.Id, api.Organization.Id, api.Project.Id);
        }, configureControlPlane: builder =>
        {
            builder.Services.AddSingleton(new PublishSettings(GovernanceSnapshot24Environments: enabled));
            builder.Services.AddSingleton<IGovernanceRecoveryContinuity>(new IsolatedContinuity());
        });
        await fixture.SeedProductionBaselineAsync();
        var target = fixture.TargetEnvironmentId;
        var (routeId, selectedKey) = await PrepareTargetAsync(fixture, scenario);
        enabled.Add(target);
        await PromotionSubmissionTests.Create(fixture);
        fixture.TargetCredentialId = selectedKey;
        using var optionsResponse = await fixture.Api.Client.GetAsync($"/api/v1/release-promotions/{fixture.PromotionId}/mapping-options");
        optionsResponse.EnsureSuccessStatusCode();
        var options = (await optionsResponse.Content.ReadFromJsonAsync<PromotionMappingOptionsDto>())!;
        Assert.True(options.GovernedTarget);
        Assert.Equal(fixture.Source.Artifact.Content.Routes[0].VersionId, Assert.Single(options.Routes!).ApiVersionId);
        var choices = options.Applications!.SelectMany(x => x.Credentials);
        using var saved = await fixture.SaveAsync(fixture.Mapping(scenario == "missing-target-route" ? null : routeId));
        if (scenario is "global-key" or "foreign-key")
        {
            Assert.DoesNotContain(choices, x => x.Id == selectedKey);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, saved.StatusCode);
            return;
        }
        Assert.Contains(choices, x => x.Id == selectedKey);
        if (scenario == "missing-target-route")
        {
            Assert.Equal(HttpStatusCode.Conflict, saved.StatusCode);
            Assert.Contains("governance_promotion_target_route_required", await saved.Content.ReadAsStringAsync());
            return;
        }
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());

        using var preview = await PromotionSubmissionTests.Send(fixture, "precheck",
            new PromotionPrecheckRequest(ConfirmUpstreamHealth: true, UpstreamHealthComment: "隔离环境后端已就绪，提交独立审批"));
        if (scenario == "missing-route-grant")
        {
            Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            var blocked = (await preview.Content.ReadFromJsonAsync<PromotionPrecheckDto>())!;
            Assert.False(blocked.CanSubmit);
            Assert.Equal("Blocked", blocked.Status);
            Assert.Contains(blocked.Checks, x => x.Blocking && x.ReasonCode == "governance_promotion_access_unverified");
            Assert.Null(blocked.CandidateHash);
            return;
        }
        Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
        var reviewed = (await preview.Content.ReadFromJsonAsync<PromotionPrecheckDto>())!;
        Assert.True(reviewed.CanSubmit);
        using var submitted = await PromotionSubmissionTests.Send(fixture, "submit", etag: $"\"{reviewed.Revision}\"");
        Assert.True(submitted.IsSuccessStatusCode, await submitted.Content.ReadAsStringAsync());
        var release = (await submitted.Content.ReadFromJsonAsync<PromotionDto>())!.TargetReleaseId!.Value;
        foreach (var role in new[] { "ApiApprover", "SecurityReviewer" })
        {
            var approver = await fixture.NewProductionApproverAsync(role);
            using var approved = await ApiFixture.CommandAsync(approver.Client, $"/api/v1/releases/{release}/approve",
                new { comment = "隔离环境目标审批" });
            Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
        }

        if (scenario == "grant-changed-after-approval")
        {
            await using var db = fixture.Api.Context();
            await using var change = await db.Database.BeginTransactionAsync();
            await db.Set<ApplicationApiPermission>().Where(x => x.Id == fixture.TargetAuthorizationId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Revision, x => x.Revision + 1));
            await db.Set<ApplicationRouteMethodGrant>().Where(x => x.PermissionId == fixture.TargetAuthorizationId).ExecuteDeleteAsync();
            await change.CommitAsync();
        }
        using var started = await PromotionExecutionTests.Start(fixture, release);
        if (scenario == "grant-changed-after-approval")
        {
            Assert.Equal(HttpStatusCode.Conflict, started.StatusCode);
            return;
        }
        Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
        await fixture.Source.Deployment.BuildQueuedAsync();
        await using var check = fixture.Api.Context();
        var record = await check.Set<ReleaseRecord>().SingleAsync(x => x.Id == release);
        Assert.Equal("Publishing", record.Status);
        Assert.Equal(2, await check.Set<ReleaseTarget>().CountAsync(x => x.ReleaseId == release));
        using var services = fixture.Api.Services();
        var snapshot = (await services.ServiceProvider.GetRequiredService<HistoricalSnapshotService>()
            .ReadAsync(target, record.ToConfigVersion)).Snapshot;
        Assert.Equal("2.4", snapshot.SchemaVersion);
        Assert.Equal(routeId, Assert.Single(snapshot.Routes).Id);
        var app = Assert.Single(snapshot.Applications);
        Assert.Equal(fixture.TargetApplicationId, app.Id);
        var credential = Assert.Single(app.Credentials);
        Assert.Equal(selectedKey, credential.Id);
        Assert.Equal(target, credential.EnvironmentId);
        Assert.Equal("EnvironmentBound", credential.BindingMode);
        Assert.Equal("RouteMethod", Assert.Single(app.Permissions).Mode);
    }

    private static async Task<(Guid RouteId, Guid CredentialId)> PrepareTargetAsync(PromotionScenario fixture, string scenario)
    {
        await using var db = fixture.Api.Context();
        var target = fixture.TargetEnvironmentId;
        var route = await db.Set<ApiRoute>().SingleAsync(x => x.EnvironmentId == target);
        var legacy = await db.Set<ApplicationCredential>().AsNoTracking().SingleAsync(x => x.Id == fixture.TargetCredentialId);
        var bound = new ApplicationCredential
        {
            ApplicationId = legacy.ApplicationId, OrganizationId = legacy.OrganizationId, ProjectId = fixture.Api.Project.Id,
            EnvironmentId = scenario == "foreign-key" ? fixture.Api.Environment.Id : target, BindingMode = "EnvironmentBound",
            AccessKey = "ak_target_bound_fixture", SecretHash = legacy.SecretHash, SecretLast4 = legacy.SecretLast4,
            ValidFrom = legacy.ValidFrom, ExpiresAt = legacy.ExpiresAt
        };
        db.Add(bound);
        var parent = await db.Set<ApplicationApiPermission>().SingleAsync(x => x.Id == fixture.TargetAuthorizationId);
        parent.Mode = "RouteMethod";
        parent.Revision++;
        if (scenario != "missing-route-grant")
            db.AddRange(route.Methods.Select(method => new ApplicationRouteMethodGrant
            {
                PermissionId = parent.Id, RouteId = route.Id, EnvironmentId = target, ApiId = parent.ApiId,
                Method = method, AuthorizationRevision = route.AuthorizationRevision
            }));
        foreach (var node in await db.Set<GatewayNode>().Where(x => x.EnvironmentId == target).ToArrayAsync())
        {
            var instance = Guid.Parse(node.InstanceId);
            node.Metadata = SecurityProtocolCapabilities.Merge(SnapshotSchemaCapabilities.Merge(node.Metadata, instance,
                ["2.0", "2.1", "2.2", "2.3", "2.4"]), instance, ["security-ledger-v1"]);
            node.LastHeartbeatAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        await AddFreshLedgerAsync(db, target, fixture.Api.Organization.Id, fixture.Api.Project.Id);
        return (route.Id, scenario == "global-key" ? legacy.Id : bound.Id);
    }

    private static async Task AddFreshLedgerAsync(WebApiDbContext db, Guid environmentId, Guid organizationId, Guid projectId)
    {
        db.Add(new EnvironmentSecurityState { EnvironmentId = environmentId, OrganizationId = organizationId,
            ProjectId = projectId, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    private sealed class IsolatedContinuity : IGovernanceRecoveryContinuity
    {
        private readonly GovernanceJournalId journal = new(Guid.NewGuid(), Guid.NewGuid());

        public async Task<GovernanceContinuity> RequireAsync(WebApiDbContext db, Guid environmentId, CancellationToken ct = default)
        {
            Assert.NotNull(db.Database.CurrentTransaction);
            return new(journal, GovernanceJournalProtocol.Genesis, await new SecurityLedgerReader(db).ReadCompleteLockedAsync(environmentId, ct));
        }
    }
}

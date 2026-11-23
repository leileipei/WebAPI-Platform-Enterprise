using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Releases;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class SnapshotRollbackSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotOffersTheCurrentPromotedReleaseOnlyWhenItsSourceArtifactIsVisible(bool sourceVisible)
    {
        await using var scenario = new PromotionScenario();
        await scenario.InitializeAsync(authenticatedTargetReceipts: true);
        var promotion = await ProductionVerificationTests.Deployed(scenario);
        var releaseId = promotion.TargetReleaseId!.Value;
        var reader = await scenario.NewProductionApproverAsync("SnapshotRollbackOperator");
        await scenario.GrantAsync(reader.User.Id, "gateway.config.read", "release.rollback");
        if (!sourceVisible)
        {
            await using var db = scenario.Api.Context();
            await db.Set<UserProjectScope>().Where(s => s.UserId == reader.User.Id).ExecuteDeleteAsync();
            db.Add(new UserProjectScope
            {
                UserId = reader.User.Id, OrganizationId = scenario.Api.Organization.Id, ProjectId = scenario.Api.Project.Id,
                EnvironmentId = scenario.TargetEnvironmentId, AccessMode = "read_write"
            });
            await db.SaveChangesAsync();
        }

        var snapshots = $"/api/v1/environments/{scenario.TargetEnvironmentId}/snapshots";
        using var target = await reader.Client.GetAsync(snapshots + "/1");
        Assert.Equal(HttpStatusCode.OK, target.StatusCode);
        using var artifact = await reader.Client.GetAsync($"/api/v1/release-artifacts/{promotion.ArtifactId}");
        Assert.Equal(sourceVisible ? HttpStatusCode.OK : HttpStatusCode.NotFound, artifact.StatusCode);
        var response = await reader.Client.GetFromJsonAsync<JsonElement>(snapshots + "/rollback-source");
        var source = response.GetProperty("source");

        using var rollback = await ApiFixture.CommandAsync(reader.Client, $"/api/v1/releases/{releaseId}/rollback", new CreateRollbackRequest(1));
        await using var verify = scenario.Api.Context();
        var drafts = verify.Set<ReleaseRecord>().Where(r => r.EnvironmentId == scenario.TargetEnvironmentId && r.ReleaseType == "rollback");
        if (!sourceVisible)
        {
            Assert.Equal(HttpStatusCode.NotFound, rollback.StatusCode);
            Assert.False(await drafts.AnyAsync());
            Assert.Equal(JsonValueKind.Null, source.ValueKind);
            return;
        }

        Assert.Equal(releaseId, source.GetProperty("releaseId").GetGuid());
        Assert.Equal(2, source.GetProperty("configVersion").GetInt64());
        Assert.Equal(2, source.GetProperty("deploymentSequence").GetInt64());
        Assert.True(rollback.IsSuccessStatusCode, await rollback.Content.ReadAsStringAsync());
        var result = (await rollback.Content.ReadFromJsonAsync<ReleaseDto>())!;
        Assert.Equal("Draft", result.State);
        Assert.Equal(1, result.TargetConfigVersion);
        Assert.Equal(releaseId, result.RollbackOf);
        Assert.Equal(result.Id, (await drafts.SingleAsync()).Id);
    }
}

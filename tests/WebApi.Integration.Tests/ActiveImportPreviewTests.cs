using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ActiveImportPreviewTests
{
    private const string Path = "/api/v1/openapi/import-previews";

    private static async Task<JsonElement[]> List(HttpClient client, Guid project)
    {
        using var response = await client.GetAsync($"{Path}?projectId={project}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
    }

    [Fact]
    public async Task OwnerCanReleaseOneOfFiveReplacedPreviewsAndRetry()
    {
        await using var f = await ImportSessionTests.Setup();
        var created = new List<ImportSessionDto>();
        foreach (var name in new[] { "one", "one", "two", "two", "three" })
        {
            var source = ImportAndCredentialTests.Source.Replace("/import/one", "/example/" + name);
            created.Add(await ImportSessionTests.Preview(f, ImportSessionTests.Input(f, source)));
        }
        using var sixth = await f.WriteAsync(HttpMethod.Post, Path, ImportSessionTests.Input(f));
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        var summaries = await List(f.Client, f.Project.Id);
        Assert.Equal(created.Select(x => x.PreviewId).Order(), summaries.Select(x => x.GetProperty("previewId").GetGuid()).Order());
        Assert.All(summaries, row =>
        {
            Assert.True(row.GetProperty("expiresAt").GetDateTimeOffset() > row.GetProperty("createdAt").GetDateTimeOffset());
            Assert.Equal(f.Environment.Name, row.GetProperty("environmentName").GetString());
            Assert.Equal(f.Cluster.Name, row.GetProperty("clusterName").GetString());
            Assert.Equal(64, row.GetProperty("bundleHash").GetString()!.Length);
            Assert.False(row.TryGetProperty("operations", out _));
            Assert.False(row.TryGetProperty("bundleJson", out _));
        });

        using var revoke = await f.WriteAsync(HttpMethod.Delete, $"{Path}/{created[0].PreviewId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var retry = await ImportSessionTests.Preview(f);
        Assert.Equal(created.Skip(1).Select(x => x.PreviewId).Append(retry.PreviewId).Order(),
            (await List(f.Client, f.Project.Id)).Select(x => x.GetProperty("previewId").GetGuid()).Order());
    }

    [Fact]
    public async Task OnlyOwnUnexpiredAndRevocableProjectSessionsAreListed()
    {
        await using var f = await ImportSessionTests.Setup();
        var other = await f.NewReviewerAsync("ProjectAdmin");
        using var foreignResponse = await ApiFixture.CommandAsync(other.Client, Path, ImportSessionTests.Input(f));
        foreignResponse.EnsureSuccessStatusCode();
        var foreign = (await foreignResponse.Content.ReadFromJsonAsync<ImportSessionDto>())!;
        var active = await ImportSessionTests.Preview(f);
        var revoked = await ImportSessionTests.Preview(f);
        using var revoke = await f.WriteAsync(HttpMethod.Delete, $"{Path}/{revoked.PreviewId}");
        revoke.EnsureSuccessStatusCode();
        var committed = await ImportSessionTests.Preview(f);
        using var commit = await f.WriteAsync(HttpMethod.Post, $"{Path}/{committed.PreviewId}/commit", ImportSessionTests.Commit(committed));
        commit.EnsureSuccessStatusCode();
        var expired = await ImportSessionTests.Preview(f);

        await using var db = f.Context();
        var then = DateTimeOffset.UtcNow.AddHours(-2);
        await db.Set<ApiImportPreview>().Where(x => x.Id == expired.PreviewId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, then).SetProperty(x => x.ExpiresAt, then.AddMinutes(20)));
        Assert.Equal(active.PreviewId, Assert.Single(await List(f.Client, f.Project.Id)).GetProperty("previewId").GetGuid());
        Assert.Equal(foreign.PreviewId, Assert.Single(await List(other.Client, f.Project.Id)).GetProperty("previewId").GetGuid());
        using var foreignRevoke = await f.WriteAsync(HttpMethod.Delete, $"{Path}/{foreign.PreviewId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignRevoke.StatusCode);
        await db.Set<UpstreamCluster>().Where(x => x.Id == f.Cluster.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Inactive"));
        Assert.Empty(await List(f.Client, f.Project.Id));
        await db.Set<UserProjectScope>().Where(x => x.UserId == f.User.Id).ExecuteDeleteAsync();
        using var denied = await f.Client.GetAsync($"{Path}?projectId={f.Project.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task ProjectFilledByOtherUsersStaysAtQuotaAndTheirPreviewsRemainPrivate()
    {
        await using var f = await ImportSessionTests.Setup();
        var sample = await ImportSessionTests.Preview(f);
        using var revoke = await f.WriteAsync(HttpMethod.Delete, $"{Path}/{sample.PreviewId}");
        revoke.EnsureSuccessStatusCode();
        await using var db = f.Context();
        var source = await db.Set<ApiImportPreview>().AsNoTracking().SingleAsync();
        var roleId = await db.Set<UserRole>().Where(x => x.UserId == f.User.Id).Select(x => x.RoleId).SingleAsync();
        for (var user = 0; user < 20; user++)
        {
            var owner = new UserRecord { Username = $"importer_{user}", DisplayName = "Importer" };
            db.Add(owner);
            db.Add(new UserRole { UserId = owner.Id, RoleId = roleId });
            db.Add(new UserProjectScope { UserId = owner.Id, OrganizationId = f.Organization.Id, ProjectId = f.Project.Id, AccessMode = "read_write" });
            for (var preview = 0; preview < 5; preview++)
            {
                var row = new ApiImportPreview {
                    ActorId = owner.Id, OrganizationId = source.OrganizationId, ProjectId = source.ProjectId,
                    EnvironmentId = source.EnvironmentId, ClusterId = source.ClusterId, CreatedAt = source.CreatedAt,
                    ExpiresAt = source.ExpiresAt, SourcePolicyRevision = source.SourcePolicyRevision, SourceFormat = source.SourceFormat,
                    Dialect = source.Dialect, BundleJson = source.BundleJson, BundleHash = source.BundleHash, SourceHash = source.SourceHash
                };
                row.PreviewJson = JsonSerializer.Serialize(sample with { PreviewId = row.Id }, CanonicalJson.Options);
                db.Add(row);
            }
        }
        await db.SaveChangesAsync();
        Assert.Empty(await List(f.Client, f.Project.Id));
        using var response = await f.WriteAsync(HttpMethod.Post, Path, ImportSessionTests.Input(f));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("import_preview_quota", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(100, await db.Set<ApiImportPreview>().CountAsync(x => x.Status == "Active"));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Releases;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ImportRouteMethodTests
{
    private const string Source = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Catalog", "version": "1" },
          "paths": {
            "/items": {
              "get": { "operationId": "readItems", "responses": { "200": { "description": "Items" } } },
              "trace": { "operationId": "traceItems", "responses": { "200": { "description": "Trace" } } }
            }
          }
        }
        """;
    private static readonly ImportTarget Read = new("readItems", "ITEMS", "Items");
    private static readonly ImportTarget Trace = new("traceItems", "TRACE_ITEMS", "Trace items");

    [Fact]
    public async Task MixedDocumentMarksTraceUnsupportedAndPublishesSelectedGet()
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var api = scenario.Api;
        var input = new ImportPreviewRequest(api.Project.Id, api.Environment.Id, api.Cluster.Id, Source);
        using var inlinePreview = await api.WriteAsync(HttpMethod.Post, "/api/v1/openapi/import-preview", input);
        inlinePreview.EnsureSuccessStatusCode();
        var inline = (await inlinePreview.Content.ReadFromJsonAsync<ImportPreviewResponse>())!;
        AssertMixedPreview(inline.Operations);

        var session = await ImportSessionTests.Preview(api, ImportSessionTests.Input(api, Source));
        AssertMixedPreview(session.Operations);
        using var commit = await api.WriteAsync(HttpMethod.Post, $"/api/v1/openapi/import-previews/{session.PreviewId}/commit",
            new CommitImportSessionRequest(session.BundleHash, [Read]));
        commit.EnsureSuccessStatusCode();
        var imported = Assert.Single((await commit.Content.ReadFromJsonAsync<ImportCommitResponse>())!.Operations);
        Assert.Equal("readItems", imported.OperationId);

        using var create = await ApiFixture.CommandAsync(api.Client, $"/api/v1/environments/{api.Environment.Id}/releases",
            await api.PreviewReleaseRequestAsync(versionId: imported.VersionId));
        create.EnsureSuccessStatusCode();
        var release = (await create.Content.ReadFromJsonAsync<ReleaseDto>())!;
        using var submit = await ApiFixture.CommandAsync(api.Client, $"/api/v1/releases/{release.Id}/submit");
        submit.EnsureSuccessStatusCode();
        await scenario.ApproveAsync(release.Id);
        await scenario.BuildAsync(release.Id);
        var snapshot = SnapshotValidator.ParsePayload((await scenario.DesiredAsync(release.Id)).Payload, api.Environment.Id);
        var route = Assert.Single(snapshot.Routes, r => r.Id == imported.RouteId);
        Assert.Equal(imported.VersionId, route.ApiVersionId);
        Assert.Equal("/items", route.Path);
        Assert.Equal("GET", Assert.Single(route.Methods));
        Assert.True(route.RequireApiKey);
        await scenario.AckAllAsync(release.Id);
        await using var db = api.Context();
        Assert.Equal("Succeeded", (await db.Set<ReleaseRecord>().SingleAsync(r => r.Id == release.Id)).Status);
        Assert.Equal("Published", (await db.Set<ApiVersion>().SingleAsync(v => v.Id == imported.VersionId)).Status);
    }

    [Fact]
    public async Task SavedPreviewMarkedSupportedRejectsTraceAndCanRetryWithGetOnly()
    {
        await using var api = await ImportSessionTests.Setup();
        var preview = await ImportSessionTests.Preview(api, ImportSessionTests.Input(api, Source));
        // Older previews stored TRACE as selectable; keep their original frozen source and hash.
        var oldPreview = preview with { Operations = preview.Operations.Select(op => op.Method == "TRACE"
            ? op with { Supported = true, SuggestedCode = op.OperationId, Warnings = [] } : op).ToArray() };
        await using (var seed = api.Context())
        {
            var row = await seed.Set<ApiImportPreview>().SingleAsync(p => p.Id == preview.PreviewId);
            row.PreviewJson = JsonSerializer.Serialize(oldPreview, CanonicalJson.Options);
            await seed.SaveChangesAsync();
        }

        var path = $"/api/v1/openapi/import-previews/{preview.PreviewId}/commit";
        var key = Guid.NewGuid().ToString("N");
        var mixed = new CommitImportSessionRequest(preview.BundleHash, [Read, Trace]);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var rejected = await ApiFixture.CommandAsync(api.Client, path, mixed, key);
            await AssertUnsupportedTrace(rejected);
        }
        await using (var db = api.Context())
        {
            var row = await db.Set<ApiImportPreview>().SingleAsync();
            Assert.Equal("Active", row.Status);
            Assert.Null(row.ImportId);
            Assert.Null(row.CommittedTargetsJson);
            Assert.False(await db.Set<Api>().AnyAsync(a => a.Id != api.Api.Id));
            Assert.False(await db.Set<ApiVersion>().AnyAsync(v => v.Id != api.Version.Id));
            Assert.False(await db.Set<ApiRoute>().AnyAsync());
            Assert.False(await db.Set<RouteMethod>().AnyAsync());
            Assert.False(await db.Set<ApiVersionContractSources>().AnyAsync());
            Assert.False(await db.Set<AuditLog>().AnyAsync(a => a.Action == "openapi.import"));
            Assert.False(await db.Set<IdempotencyRecord>().AnyAsync(r => r.Key == key));
        }

        var corrected = new CommitImportSessionRequest(preview.BundleHash, [Read]);
        using var first = await ApiFixture.CommandAsync(api.Client, path, corrected, key);
        first.EnsureSuccessStatusCode();
        using var replay = await ApiFixture.CommandAsync(api.Client, path, corrected, key);
        replay.EnsureSuccessStatusCode();
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await replay.Content.ReadAsByteArrayAsync());
        await using var saved = api.Context();
        Assert.Equal("Committed", (await saved.Set<ApiImportPreview>().SingleAsync()).Status);
        Assert.Equal("GET", Assert.Single((await saved.Set<ApiRoute>().SingleAsync()).Methods));
        Assert.Equal("GET", (await saved.Set<RouteMethod>().SingleAsync()).Method);
        Assert.Equal(1, await saved.Set<IdempotencyRecord>().CountAsync(r => r.Key == key));
    }

    [Fact]
    public async Task InlineCommitReportsUnsupportedSelectionBeforeEarlierRouteWriteConflict()
    {
        await using var api = await ImportSessionTests.Setup();
        using var existing = await api.WriteAsync(HttpMethod.Post, $"/api/v1/environments/{api.Environment.Id}/routes", api.RouteBody("/items"));
        existing.EnsureSuccessStatusCode();
        var input = new ImportPreviewRequest(api.Project.Id, api.Environment.Id, api.Cluster.Id, Source);
        using var commit = await api.WriteAsync(HttpMethod.Post, "/api/v1/openapi/import-commit", new ImportCommitRequest(input, [Read, Trace]));
        await AssertUnsupportedTrace(commit);
        await using var db = api.Context();
        Assert.Equal(api.Version.Id, (await db.Set<ApiRoute>().SingleAsync()).ApiVersionId);
        Assert.Equal("GET", (await db.Set<RouteMethod>().SingleAsync()).Method);
    }

    private static void AssertMixedPreview(IReadOnlyList<ImportOperationDto> operations)
    {
        Assert.Equal(2, operations.Count);
        var get = Assert.Single(operations, op => op.OperationId == "readItems");
        Assert.Equal("GET", get.Method);
        Assert.True(get.Supported);
        var trace = Assert.Single(operations, op => op.OperationId == "traceItems");
        Assert.Equal("TRACE", trace.Method);
        Assert.Equal("/items", trace.Path);
        Assert.False(trace.Supported);
        Assert.Contains(trace.Warnings, warning => warning.Contains("unsupported_route_method") && warning.Contains("TRACE"));
    }

    private static async Task AssertUnsupportedTrace(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unsupported_route_method", problem.GetProperty("code").GetString());
        var reason = problem.GetProperty("title").GetString();
        Assert.Contains("traceItems", reason);
        Assert.Contains("TRACE", reason);
    }
}

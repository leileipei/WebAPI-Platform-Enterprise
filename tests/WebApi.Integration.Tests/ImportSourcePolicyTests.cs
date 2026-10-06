using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ImportSourcePolicyTests
{
    private static SaveImportSourcePolicyRequest Request => new([new("https://contracts.example", "/contracts", [])], new());
    [Fact] public async Task FirstSaveRequiresRevisionZeroAndConcurrentUpdateHasOneWinner()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedScopeAsync(); using var login = await api.LoginAsync();
        var path = $"/api/v1/projects/{api.Project.Id}/import-source-policy";
        using var get = await api.Client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(0, (await get.Content.ReadFromJsonAsync<ImportSourcePolicyDto>())!.Revision);
        using var missing = await api.WriteAsync(HttpMethod.Put, path, Request); Assert.Equal(HttpStatusCode.PreconditionFailed, missing.StatusCode);
        using var create = await api.WriteAsync(HttpMethod.Put, path, Request, "\"0\""); Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var token = await api.CsrfAsync();
        async Task<HttpResponseMessage> Save() { using var req = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(Request) }; req.Headers.Add("X-CSRF-Token", token); req.Headers.Add("If-Match", "\"1\""); req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")); return await api.Client.SendAsync(req); }
        var results = await Task.WhenAll(Save(), Save());
        try { Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.PreconditionFailed); } finally { foreach (var r in results) r.Dispose(); }
        await using var db = api.Context(); Assert.Equal(2, (await db.Set<ProjectImportSourcePolicy>().SingleAsync()).Revision);
    }
    [Fact] public async Task ReadOnlyScopeAndRevokedPermissionCannotSave()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedScopeAsync("read_only"); using var login = await api.LoginAsync();
        var path = $"/api/v1/projects/{api.Project.Id}/import-source-policy";
        using var get = await api.Client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var save = await api.WriteAsync(HttpMethod.Put, path, Request, "\"0\""); Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);
        await using (var db = api.Context()) { var scope = await db.Set<UserProjectScope>().SingleAsync(); scope.AccessMode = "read_write"; var permission = await db.Set<Permission>().SingleAsync(x => x.Code == "project.write"); db.RemoveRange(await db.Set<RolePermission>().Where(x => x.PermissionId == permission.Id).ToArrayAsync()); await db.SaveChangesAsync(); }
        using var revoked = await api.WriteAsync(HttpMethod.Put, path, Request, "\"0\""); Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        await using var check = api.Context(); Assert.Empty(await check.Set<ProjectImportSourcePolicy>().ToArrayAsync());
    }
    [Fact] public async Task ForeignProjectIsInvisibleAndInvalidRulesDoNotWrite()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedScopeAsync(); using var login = await api.LoginAsync();
        var foreign = new Project { OrganizationId = Guid.NewGuid(), Code = "foreign", Name = "foreign" };
        await using (var db = api.Context()) { db.Add(new Organization { Id = foreign.OrganizationId, Code = "other", Name = "other" }); db.Add(foreign); await db.SaveChangesAsync(); }
        using var hidden = await api.Client.GetAsync($"/api/v1/projects/{foreign.Id}/import-source-policy"); Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var invalid = await api.WriteAsync(HttpMethod.Put, $"/api/v1/projects/{api.Project.Id}/import-source-policy", new SaveImportSourcePolicyRequest([new("https://*.example", "/contracts", [])], new()), "\"0\""); Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        await using var check = api.Context(); Assert.Empty(await check.Set<ProjectImportSourcePolicy>().ToArrayAsync());
    }

    [Fact] public async Task UnicodePrefixAndOriginAreStoredCanonicallyWithoutIncreasingLimits()
    {
        await using var api = new ApiFixture(); await api.InitializeAsync(); await api.SeedScopeAsync(); using var login = await api.LoginAsync();
        var path = $"/api/v1/projects/{api.Project.Id}/import-source-policy";
        using var save = await api.WriteAsync(HttpMethod.Put, path, new SaveImportSourcePolicyRequest([new("https://CONTRACTS.example:443/", "/契约", [])], new(MaxDocumentBytes: 1000)), "\"0\"");
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var result = (await save.Content.ReadFromJsonAsync<ImportSourcePolicyDto>())!;
        Assert.Equal("https://contracts.example", result.Allowances[0].Origin);
        Assert.Equal(new Uri("https://contracts.example/契约").AbsolutePath, result.Allowances[0].PathPrefix);
        using var tooLarge = await api.WriteAsync(HttpMethod.Put, path, Request with { Limits = new(MaxDocumentBytes: 3 * 1024 * 1024) }, "\"1\"");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLarge.StatusCode);
    }
}

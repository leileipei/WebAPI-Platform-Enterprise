using System.Net.Http.Json;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class WizardRoutePagingTests(MigratedDatabaseTemplateFixture template) : IClassFixture<MigratedDatabaseTemplateFixture>
{
    [Fact]
    public async Task VersionFilterPrecedesCountAndPagingAndIncludesDisabledRoutes()
    {
        await using var api = new ApiFixture(template.Template);
        await api.InitializeAsync();
        await api.SeedCatalogAsync();
        using var login = await api.LoginAsync();
        var routes = $"/api/v1/environments/{api.Environment.Id}/routes";
        using var created = await api.WriteAsync(HttpMethod.Post, $"/api/v1/apis/{api.Api.Id}/versions", new { version = "2.0.0" });
        created.EnsureSuccessStatusCode();
        var selected = (await created.Content.ReadFromJsonAsync<VersionDto>())!.Id;
        foreach (var (path, version, enabled) in new[] { ("/a", api.Version.Id, true), ("/b", selected, true), ("/c", selected, false) })
        {
            using var write = await api.WriteAsync(HttpMethod.Post, routes, new { apiVersionId = version, routeName = path, path, methods = new[] { "GET" }, clusterId = api.Cluster.Id, enabled });
            write.EnsureSuccessStatusCode();
        }
        var independent = (await api.Client.GetFromJsonAsync<PageResult<RouteDto>>(routes + "?pageSize=1"))!;
        Assert.Equal(3, independent.Total);
        Assert.Equal("/a", Assert.Single(independent.Items).Path);
        var filtered = routes + $"?apiVersionId={selected}&pageSize=1";
        var first = (await api.Client.GetFromJsonAsync<PageResult<RouteDto>>(filtered))!;
        Assert.Equal(2, first.Total);
        Assert.Equal("/b", Assert.Single(first.Items).Path);
        var second = (await api.Client.GetFromJsonAsync<PageResult<RouteDto>>(filtered + "&page=2"))!;
        Assert.Equal(2, second.Total);
        var row = Assert.Single(second.Items);
        Assert.Equal("/c", row.Path);
        Assert.False(row.Enabled);
        var absent = (await api.Client.GetFromJsonAsync<PageResult<RouteDto>>(routes + $"?apiVersionId={Guid.NewGuid()}"))!;
        Assert.Empty(absent.Items);
        Assert.Equal(0, absent.Total);
    }
}

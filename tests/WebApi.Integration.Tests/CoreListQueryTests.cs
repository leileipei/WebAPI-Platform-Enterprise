using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Domain.Routing;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Routing;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class CoreListQueryTests
{
    // Test-only SQL accounting: explicit resource relations are data; scope/user/role queries are not.
    // Actual consumed rows are independently measured by the existing probe, preventing a full read
    // from passing merely because it uses one SQL command. Neither observer records parameter values.
    private sealed class DataSqlCapture : DbCommandInterceptor
    {
        internal int Count;
        internal int Associations;
        internal void Reset() { Count = 0; Associations = 0; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            var sql = command.CommandText;
            if (new[] { "FROM apis ", "FROM upstream_clusters ", "FROM upstream_destinations ", "FROM api_routes ", "FROM route_policy_bindings " }.Any(sql.Contains)) Count++;
            if (sql.Contains("FROM upstream_destinations ") || sql.Contains("FROM route_policy_bindings ")) Associations++;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly ApiFixture Api = new();
        internal readonly DataSqlCapture Capture = new();
        internal readonly RequestSqlProbe Probe = new();
        internal readonly List<ApiRoute> Routes = [];
        internal readonly List<UpstreamCluster> Clusters = [];
        internal readonly List<Api> Apis = [];
        internal async Task InitializeAsync(bool routes = true)
        {
            await Api.InitializeAsync(b => b.Services.AddDbContext<WebApiDbContext>(o => o.AddInterceptors(Capture, Probe)));
            await Api.SeedCatalogAsync(); await using var db = Api.Context(); Apis.Add(Api.Api); Clusters.Add(Api.Cluster);
            // Existing uniqueness rules preclude duplicate API codes or cluster names.
            for (var i = 0; i < 150; i++)
            {
                var a = new Api { OrganizationId = Api.Organization.Id, ProjectId = Api.Project.Id, Code = "C" + i.ToString("D4"), Name = "同名 API", OwnerUserId = Api.User.Id }; Apis.Add(a); db.Add(a);
                var c = new UpstreamCluster { ProjectId = Api.Project.Id, EnvironmentId = Api.Environment.Id, Name = "C" + i.ToString("D4"), HealthCheckPath = "/health" }; Clusters.Add(c); db.Add(c);
                db.Add(new UpstreamDestination { ClusterId = c.Id, Name = "backend", Address = "http://test-backend:8080/" });
            }
            if (routes)
            {
                var auth = new Policy { OrganizationId = Api.Organization.Id, ProjectId = Api.Project.Id, Name = "Anonymous", Type = "authentication", Config = "{\"mode\":\"Anonymous\"}" };
                var timeout = new Policy { OrganizationId = Api.Organization.Id, ProjectId = Api.Project.Id, Name = "Timeout", Type = "timeout", Config = "{\"timeoutMs\":1234}" }; db.AddRange(auth, timeout);
                for (var i = 0; i < 151; i++)
                {
                    var path = "/core/" + i.ToString("D4") + (i % 3 == 1 ? "/{id}" : i % 3 == 2 ? "/{**rest}" : "");
                    var r = Route(path, i % 7); Routes.Add(r); db.Add(r); db.AddRange(new RoutePolicyBinding { RouteId = r.Id, PolicyId = auth.Id }, new RoutePolicyBinding { RouteId = r.Id, PolicyId = timeout.Id });
                }
            }
            await db.SaveChangesAsync(); (await Api.LoginAsync()).EnsureSuccessStatusCode();
        }
        internal ApiRoute Route(string path, int priority, Guid? id = null) => new() { Id = id ?? Guid.NewGuid(), ApiVersionId = Api.Version.Id, EnvironmentId = Api.Environment.Id, ClusterId = Api.Cluster.Id, RouteName = "重复名称", Path = path, NormalizedPath = RouteNormalizer.Normalize(path), Methods = ["GET"], TimeoutMs = 30000, Priority = priority, Enabled = false };
        internal string Path(string kind, Guid? scope = null) => kind == "api" ? $"/api/v1/projects/{scope ?? Api.Project.Id}/apis" : $"/api/v1/environments/{scope ?? Api.Environment.Id}/{(kind == "cluster" ? "clusters" : "routes")}";
        internal async Task<(PageResult<T> Page, int Sql, int Associations, long Rows)> ReadAsync<T>(string kind, int page, int size)
        {
            Capture.Reset(); var request = Guid.NewGuid().ToString("N"); using var probe = Probe.Begin(request);
            using var services = Api.Services(); var actor = new ActorContext(Api.User.Id, request);
            object result = kind switch {
                "api" => await services.ServiceProvider.GetRequiredService<CatalogService>().ListAsync(Api.Project.Id, actor, page, size),
                "cluster" => await services.ServiceProvider.GetRequiredService<ClusterService>().ListAsync(Api.Environment.Id, actor, page, size),
                _ => await services.ServiceProvider.GetRequiredService<RouteService>().ListAsync(Api.Environment.Id, actor, page, size)
            };
            return ((PageResult<T>)result, Capture.Count, Capture.Associations, Probe.Read(request).Rows);
        }
        public ValueTask DisposeAsync() => Api.DisposeAsync();
    }
    private static Guid[] Expected(Fixture f, string kind) => kind switch
    {
        "api" => f.Apis.OrderBy(a => a.Code, StringComparer.Ordinal).ThenBy(a => a.Id.ToString("D"), StringComparer.Ordinal).Select(a => a.Id).ToArray(),
        "cluster" => f.Clusters.OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id.ToString("D"), StringComparer.Ordinal).Select(c => c.Id).ToArray(),
        _ => f.Routes.OrderBy(r => r.Path.Contains("{**rest}") ? 2 : r.Path.Contains("{id}") ? 1 : 0).ThenByDescending(r => r.Priority).ThenBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Id.ToString("D"), StringComparer.Ordinal).Select(r => r.Id).ToArray()
    };
    [Theory]
    [InlineData("api", 2)] [InlineData("cluster", 3)] [InlineData("route", 4)]
    public async Task CoreListsRespectPageAndSqlBudgets(string kind, int budget)
    {
        await using var f = new Fixture(); await f.InitializeAsync(); var expected = Expected(f, kind);
        foreach (var size in new[] { 1, 50, 100 }) foreach (var page in new[] { 1, 2, (151 + size - 1) / size, (151 + size - 1) / size + 1 }.Distinct())
        {
            var ids = expected.Skip((page - 1) * size).Take(size).ToArray();
            if (kind == "api")
            {
                var r = await f.ReadAsync<ApiDto>(kind, page, size); Assert.Equal(151, r.Page.Total); Assert.Equal(ids, r.Page.Items.Select(x => x.Id)); Assert.True(r.Sql <= budget, $"data SQL {r.Sql} > {budget}"); Assert.True(r.Rows <= ids.Length + 12, $"consumed {r.Rows} rows for {ids.Length} items");
            }
            else if (kind == "cluster")
            {
                var r = await f.ReadAsync<ClusterDto>(kind, page, size); Assert.Equal(151, r.Page.Total); Assert.Equal(ids, r.Page.Items.Select(x => x.Id)); Assert.All(r.Page.Items, c => Assert.Single(c.Destinations)); Assert.True(r.Sql <= budget, $"data SQL {r.Sql} > {budget}"); Assert.True(r.Rows <= ids.Length * 2 + 12); Assert.Equal(ids.Length == 0 ? 0 : 1, r.Associations);
            }
            else
            {
                var r = await f.ReadAsync<RouteDto>(kind, page, size); Assert.Equal(151, r.Page.Total); Assert.Equal(ids, r.Page.Items.Select(x => x.Id)); Assert.All(r.Page.Items, x => { Assert.False(x.RequireApiKey); Assert.Equal(1234, x.EffectiveTimeoutMs); Assert.Null(x.EffectiveAuthenticationMode); }); Assert.True(r.Sql <= budget, $"data SQL {r.Sql} > {budget}"); Assert.True(r.Rows <= ids.Length * 3 + 12); Assert.Equal(ids.Length == 0 ? 0 : 1, r.Associations);
            }
        }
    }
    [Fact]
    public async Task RouteSqlOrderMatchesMatchOrderForLiteralParameterWildcard()
    {
        await using var f = new Fixture(); await f.InitializeAsync(false); await using var db = f.Api.Context();
        var inputs = new[] { ("/literal-z", 0, 0), ("/literal-a", 999999, -999999), ("/p/{id}", 999999, 1000001), ("/{id}", 0, 2000000), ("/w/{*rest}", 999999, 3000001), ("/w/{**rest}", 0, 4000000), ("/", 1, -1), ("/mixed/{id}/{**rest}", 0, 4000000) };
        var routes = inputs.Select(x => f.Route(x.Item1, x.Item2)).ToArray(); db.AddRange(routes); await db.SaveChangesAsync();
        foreach (var input in inputs) Assert.Equal(input.Item3, RouteNormalizer.MatchOrder(input.Item1, input.Item2));
        var result = await f.ReadAsync<RouteDto>("route", 1, 100);
        Assert.Equal(new[] { routes[1].Id, routes[6].Id, routes[0].Id, routes[2].Id, routes[3].Id, routes[4].Id, routes[7].Id, routes[5].Id }, result.Page.Items.Select(x => x.Id));
        Assert.All(result.Page.Items, r => { Assert.True(r.RequireApiKey); Assert.Equal(30000, r.EffectiveTimeoutMs); Assert.Null(r.EffectiveAuthenticationMode); });
    }
    [Fact]
    public async Task ClampAndDuplicateSortValuesRemainStable()
    {
        await using var f = new Fixture(); await f.InitializeAsync(false); await using var db = f.Api.Context();
        var orderedIds = Enumerable.Range(1, 151).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}")).ToArray();
        db.AddRange(orderedIds.Reverse().Select(id => f.Route("/duplicate", 0, id))); await db.SaveChangesAsync();
        var clamped = await f.ReadAsync<RouteDto>("route", -2, 1000); Assert.Equal((1, 100), (clamped.Page.Page, clamped.Page.PageSize)); Assert.Equal(orderedIds.Take(100), clamped.Page.Items.Select(r => r.Id));
        var next = await f.ReadAsync<RouteDto>("route", 2, 100); Assert.Equal(orderedIds.Skip(100), next.Page.Items.Select(r => r.Id));
        foreach (var page in new[] { 40001, 1000001 }) { var empty = await f.ReadAsync<RouteDto>("route", page, 100); Assert.Equal(Math.Min(page, 1000000), empty.Page.Page); Assert.Equal(151, empty.Page.Total); Assert.Empty(empty.Page.Items); Assert.Equal(0, empty.Associations); }
    }
    [Theory]
    [InlineData("api")] [InlineData("cluster")] [InlineData("route")]
    public async Task ListHttpStatusAndRevocationRemainUnchanged(string kind)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await f.Api.Client.GetAsync(f.Path(kind, Guid.NewGuid()))).StatusCode);
        await using var db = f.Api.Context(); var grants = db.Set<UserProjectScope>().Where(s => s.UserId == f.Api.User.Id);
        await grants.ExecuteDeleteAsync(); Assert.Equal(HttpStatusCode.NotFound, (await f.Api.Client.GetAsync(f.Path(kind))).StatusCode);
        db.Add(new UserProjectScope { UserId = f.Api.User.Id, OrganizationId = f.Api.Organization.Id, AccessMode = "read" }); await db.SaveChangesAsync();
        await db.Set<Project>().Where(p => p.Id == f.Api.Project.Id).ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, "Disabled")); Assert.Equal(HttpStatusCode.NotFound, (await f.Api.Client.GetAsync(f.Path(kind))).StatusCode);
    }
    [Theory]
    [InlineData("api")] [InlineData("cluster")] [InlineData("route")]
    public async Task UnrelatedEnvironmentDoesNotChangePageOrDataQueryCount(string kind)
    {
        await using var f = new Fixture(); await f.InitializeAsync();
        var before = await f.Api.Client.GetStringAsync(f.Path(kind) + "?pageSize=50"); f.Capture.Reset(); await f.Api.Client.GetStringAsync(f.Path(kind) + "?pageSize=50"); var count = f.Capture.Count;
        await using var db = f.Api.Context(); var project = new Project { OrganizationId = f.Api.Organization.Id, Code = "OTHER", Name = "无关项目" }; var env = new EnvironmentRecord { ProjectId = project.Id, Code = "OTHER", Name = "无关环境" }; db.AddRange(project, env);
        var cluster = new UpstreamCluster { ProjectId = project.Id, EnvironmentId = env.Id, Name = "other", HealthCheckPath = "/health" }; var api = new Api { ProjectId = project.Id, OrganizationId = f.Api.Organization.Id, Code = "OTHER", Name = "无关 API", OwnerUserId = f.Api.User.Id }; var version = new ApiVersion { ApiId = api.Id, Version = "1", CreatedBy = f.Api.User.Id }; db.AddRange(cluster, api, version); db.Add(new UpstreamDestination { ClusterId = cluster.Id, Name = "other", Address = "http://test-backend:8080/" });
        for (var i = 0; i < 151; i++) { var route = f.Route("/other/" + i, 0); route.ClusterId = cluster.Id; route.EnvironmentId = env.Id; route.ApiVersionId = version.Id; db.Add(route); } await db.SaveChangesAsync();
        f.Capture.Reset(); var after = await f.Api.Client.GetStringAsync(f.Path(kind) + "?pageSize=50"); Assert.Equal(before, after); Assert.Equal(count, f.Capture.Count);
    }
}

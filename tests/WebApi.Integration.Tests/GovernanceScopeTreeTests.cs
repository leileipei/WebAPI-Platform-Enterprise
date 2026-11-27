using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;
using Xunit.Abstractions;

namespace WebApi.Integration.Tests;

public sealed class GovernanceScopeTreeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TreeAndProjectsReadOnlyVisibleRowsAsUnrelatedCatalogGrows()
    {
        var probe = new RequestSqlProbe();
        await using var api = new ApiFixture();
        await api.InitializeAsync(b => b.Services.AddDbContext<WebApiDbContext>(o => o.AddInterceptors(probe)));
        await api.SeedScopeAsync("read");
        var otherOrg = new Organization { Code = "OTHER", Name = "Other organization" };
        var otherProject = new Project { OrganizationId = otherOrg.Id, Code = "OTHER", Name = "Other project" };
        await using (var db = api.Context())
        {
            var grant = await db.Set<UserProjectScope>().SingleAsync(s => s.UserId == api.User.Id);
            grant.ProjectId = api.Project.Id;
            grant.EnvironmentId = api.Environment.Id;
            await db.Set<EnvironmentRecord>().Where(e => e.Id == api.Environment.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, "Disabled"));
            db.AddRange(otherOrg, otherProject,
                new EnvironmentRecord { ProjectId = api.Project.Id, Code = "SIBLING", Name = "Sibling" },
                new UserProjectScope { UserId = api.User.Id, OrganizationId = otherOrg.Id, AccessMode = "read" });
            await db.SaveChangesAsync();
        }

        var results = new List<(SqlRequestStats Tree, SqlRequestStats Projects)>();
        var existing = 0;
        foreach (var count in new[] { 0, 12, 36, 256 })
        {
            await using (var db = api.Context())
            {
                for (var i = existing; i < count; i++)
                    db.Add(new EnvironmentRecord { ProjectId = otherProject.Id, Code = $"E{i}", Name = $"Environment {i}" });
                await db.SaveChangesAsync();
            }
            existing = count;
            using var services = api.Services();
            var governance = services.ServiceProvider.GetRequiredService<GovernanceService>();
            var actor = new ActorContext(api.User.Id, $"scope-tree-{count}");
            var watch = Stopwatch.StartNew();
            using (probe.Begin(actor.TraceId))
            {
                var tree = await governance.GetScopeTreeAsync(actor);
                Assert.Equal(api.Organization.Id, Assert.Single(tree.Organizations).Id);
                Assert.Equal(api.Project.Id, Assert.Single(tree.Projects).Id);
                var environment = Assert.Single(tree.Environments);
                Assert.Equal(api.Environment.Id, environment.Id);
                Assert.Equal("Disabled", environment.Status);
            }
            var treeMs = watch.Elapsed.TotalMilliseconds;
            var projectRequest = $"projects-{count}";
            watch.Restart();
            using (probe.Begin(projectRequest))
            {
                var page = await governance.ProjectsAsync(api.Organization.Id, actor, 1, 10);
                Assert.Equal(1, page.Total);
                var project = Assert.Single(page.Items);
                Assert.Equal(api.Project.Id, project.Id);
                Assert.Equal(1, project.EnvironmentCount);
            }
            var treeStats = probe.Read(actor.TraceId);
            var projectStats = probe.Read(projectRequest);
            output.WriteLine($"unrelated={count} treeCommands={treeStats.Total} treeRows={treeStats.Rows} treeMs={treeMs:F2} projectCommands={projectStats.Total} projectRows={projectStats.Rows} projectMs={watch.Elapsed.TotalMilliseconds:F2}");
            results.Add((treeStats, projectStats));
        }
        Assert.True(results[0].Tree.Total > 0 && results[0].Projects.Total > 0);
        Assert.All(results, point =>
        {
            Assert.Equal(results[0].Tree.Total, point.Tree.Total);
            Assert.Equal(results[0].Tree.Rows, point.Tree.Rows);
            Assert.Equal(results[0].Projects.Total, point.Projects.Total);
            Assert.Equal(results[0].Projects.Rows, point.Projects.Rows);
        });
    }
}

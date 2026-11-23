using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Observability;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Observability;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;
using Xunit.Abstractions;

namespace WebApi.Integration.Tests;

public sealed class GatewayMaintenanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NewLogicalNodeConfirmsRunningDeploymentIndependentlyOfFrozenTargets()
    {
        await using var scenario = new DeploymentScenario(3);
        await scenario.InitializeAsync(initialNodes: 2);
        var release = await scenario.PublishAsync(acknowledge: false);
        var envelope = (await scenario.DesiredAsync(release)).Envelope;
        await scenario.AckAsync(0, envelope);
        await scenario.AckAsync(1, envelope);
        var f = scenario.Api;
        using var register = await scenario.NodeAsync(2, "/internal/v1/nodes/register",
            new RegisterNodeRequest(f.Environment.Id, "node-2", scenario.Instances[2], "test"));
        register.EnsureSuccessStatusCode();
        scenario.NodeIds[2] = (await register.Content.ReadFromJsonAsync<RegisteredNode>())!.NodeId;
        using var stale = await scenario.NodeAsync(2,$"/internal/v1/nodes/{scenario.NodeIds[2]}/ack", new NodeAck(
            scenario.Instances[2],release,envelope.ConfigVersion,envelope.DeploymentSequence,envelope.PayloadHash,DateTimeOffset.UtcNow.AddMinutes(-10),true,null));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,stale.StatusCode);
        await scenario.AckAsync(2, envelope);
        await scenario.AckAsync(2, envelope);
        using var services = f.Services();
        var running = await services.ServiceProvider.GetRequiredService<RunningDeploymentReader>().ReadAsync(f.Environment.Id, release, default);
        Assert.Equal(envelope.ConfigVersion, running.Environment.DesiredConfigVersion);
        await using var db = f.Context();
        Assert.Equal(2, await db.Set<GatewayAck>().CountAsync(a => a.ReleaseId == release));
        Assert.Equal(2, await db.Set<ReleaseTarget>().CountAsync(t => t.ReleaseId == release));
        Assert.Single(await db.Set<GatewayNodeEvent>().Where(e => e.EventType == "runtime_applied").ToArrayAsync());
    }

    [Fact]
    public async Task ExpandingInvisibleEnvironmentDirectoryHasFixedSqlAndRows()
    {
        var probe = new RequestSqlProbe();
        await using var f = new ApiFixture();
        await f.InitializeAsync(b => b.Services.AddDbContext<WebApiDbContext>(options => options.AddInterceptors(probe)));
        await f.SeedCatalogAsync();
        await ObservationTestSupport.GrantAsync(f, ["metrics.read"]);
        await using var db = f.Context();
        var grant = await db.Set<UserProjectScope>().SingleAsync();
        grant.ProjectId = f.Project.Id;
        grant.EnvironmentId = f.Environment.Id;
        await db.SaveChangesAsync();
        long baseline = 0;
        foreach (var count in new[] { 0, 200 })
        {
            for (int i = 0; i < count; i++) db.Add(new EnvironmentRecord { ProjectId = f.Project.Id, Code = $"E{i}", Name = $"E{i}" });
            await db.SaveChangesAsync();
            var id = Guid.NewGuid().ToString();
            using var measurement = probe.Begin(id);
            using var services = f.Services();
            var resolver = services.ServiceProvider.GetRequiredService<ObservationScopeResolver>();
            var visible = await resolver.ResolveEnvironmentIdsAsync(new ActorContext(f.User.Id, id), "metrics.read",
                new(f.Organization.Id, f.Project.Id, null, true), default);
            Assert.Equal(f.Environment.Id, Assert.Single(visible));
            var stats = probe.Read(id);
            output.WriteLine($"hiddenEnvironments={count} visible={visible.Length} sqlReads={stats.Total} rows={stats.Rows}");
            if (count == 0) baseline = stats.Total;
            Assert.Equal(baseline, stats.Total);
            Assert.InRange(stats.Rows, 1, 16);
        }
    }

    [Fact]
    public async Task NodeReportsCurrentProcessDiagnosticsAndCanReceiveOnlyItsOwnRetry()
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var f = scenario.Api;
        var id = scenario.NodeIds[0];
        var instance = scenario.Instances[0];
        using var beat = await scenario.NodeAsync(0, $"/internal/v1/nodes/{id}/heartbeat", new {
            instanceId = instance, configVersion = 0, deploymentSequence = 0, status = "NotReady",
            diagnostics = new { cpuPercent = 12.5, workingSetBytes = 73400320, lastKnownGoodVersion = (long?)null }
        });
        beat.EnsureSuccessStatusCode();
        var path = $"/api/v1/gateway-nodes/{id}";
        using var node = await f.Client.GetAsync(path);
        var data = await node.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(73400320, data.GetProperty("diagnostics").GetProperty("workingSetBytes").GetInt64());
        using var download = await f.Client.GetAsync(path + "/diagnostics");
        download.EnsureSuccessStatusCode();
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        using var queued = await f.WriteAsync(HttpMethod.Post, path + "/retry", etag: node.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var action = (await queued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var signaled = await scenario.NodeAsync(0, $"/internal/v1/nodes/{id}/heartbeat", new {
            instanceId=instance, configVersion=0,deploymentSequence=0,status="NotReady",
            diagnostics=new {cpuPercent=12.5,workingSetBytes=73400320,lastKnownGoodVersion=(long?)null} });
        Assert.Equal("1", signaled.Headers.GetValues("X-WebApi-Pending-Operation").Single());
        using var wrong = await scenario.NodeAsync(1, $"/internal/v1/nodes/{id}/operations/claim", new { instanceId = instance });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var claim = await scenario.NodeAsync(0, $"/internal/v1/nodes/{id}/operations/claim", new { instanceId = instance });
        Assert.Equal(action, (await claim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        using var receipt = await scenario.NodeAsync(0, $"/internal/v1/nodes/{id}/operations/{action}/complete", new { instanceId = instance, outcome = "no_configuration" });
        receipt.EnsureSuccessStatusCode();
        using var completed = await f.Client.GetAsync(path + "/operations/" + action);
        Assert.Equal("no_configuration", (await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task RetriedCommandsKeepOneReceiptAndRevokedOperatorCannotDispatchIt()
    {
        await using var scenario=new DeploymentScenario();
        await scenario.InitializeAsync();
        var f=scenario.Api;
        var node=scenario.NodeIds[0];
        var instance=scenario.Instances[0];
        var path="/api/v1/gateway-nodes/"+node;
        using var report=await scenario.NodeAsync(0,$"/internal/v1/nodes/{node}/heartbeat",new {
            instanceId=instance,configVersion=0,deploymentSequence=0,status="NotReady",diagnostics=new {workingSetBytes=4096}});
        report.EnsureSuccessStatusCode();
        using var current=await f.Client.GetAsync(path);current.EnsureSuccessStatusCode();
        var csrf=await f.CsrfAsync();
        async Task<Guid> Queue()
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,path+"/retry");
            request.Headers.Add("X-CSRF-Token",csrf);request.Headers.Add("Idempotency-Key","repeat-node-operation");
            request.Headers.Add("If-Match",current.Headers.ETag!.Tag);
            using var response=await f.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        var first=await Queue();Assert.Equal(first,await Queue());
        await using var db=f.Context();
        Assert.Equal(1,await db.Set<GatewayNodeEvent>().CountAsync(e=>e.GatewayNodeId==node&&e.EventType=="maintenance"));
        var permission=await db.Set<Permission>().SingleAsync(p=>p.Code=="gateway.operate");
        await db.Set<RolePermission>().Where(p=>p.PermissionId==permission.Id).ExecuteDeleteAsync();
        using var claim=await scenario.NodeAsync(0,$"/internal/v1/nodes/{node}/operations/claim",new {instanceId=instance});
        Assert.Equal(HttpStatusCode.NoContent,claim.StatusCode);
        using var read=await f.Client.GetAsync(path+"/operations/"+first);
        Assert.Equal("target_changed",(await read.Content.ReadFromJsonAsync<NodeOperation>())!.Outcome);
    }

    [Fact]
    public async Task ClusterCopyHasIndependentDestinationsAndShowsScopedUsageAndHealth()
    {
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync();
        var f = scenario.Api;
        var path = $"/api/v1/clusters/{f.Cluster.Id}";
        using var cluster = await f.Client.GetAsync(path);
        var data = await cluster.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, data.GetProperty("routeCount").GetInt32());
        Assert.Equal(1, data.GetProperty("routes").GetArrayLength());
        Assert.False(data.TryGetProperty("healthyDestinationCount",out var count) && count.ValueKind==JsonValueKind.Number);
        using var copy = await f.WriteAsync(HttpMethod.Post, path + "/copy", new { name = "原集群副本" }, cluster.Headers.ETag!.Tag);
        copy.EnsureSuccessStatusCode();
        var result = (await copy.Content.ReadFromJsonAsync<ClusterDto>())!;
        Assert.NotEqual(f.Cluster.Id, result.Id);
        var destination = Assert.Single(result.Destinations);
        Assert.NotEqual(f.Destination.Id, destination.Id);
        Assert.Equal(result.Id, destination.ClusterId);
        Assert.Equal(f.Destination.Address, destination.Address);
        await using var db=f.Context();
        Assert.False(await db.Set<ApiRoute>().AnyAsync(r=>r.ClusterId==result.Id));
        using var stale=await f.WriteAsync(HttpMethod.Post,path+"/copy",new { name="旧修订" },"\"0\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed,stale.StatusCode);
    }
}

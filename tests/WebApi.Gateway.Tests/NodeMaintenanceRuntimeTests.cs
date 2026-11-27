using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Gateway;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Storage;
using WebApi.Gateway.Tests.Support;
using WebApi.Gateway.Workers;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.TestBackend;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class NodeMaintenanceRuntimeTests
{
    [Fact]
    public void NodeClientCanResolveBeforeGovernedLocalStorage()
    {
        var secret = Path.GetTempFileName();
        try
        {
            File.WriteAllText(secret, new string('x', 64));
            var services = new ServiceCollection();
            services.AddSingleton(new GatewaySettings(Guid.NewGuid(), "test-node", secret,
                Path.GetTempPath(), new Uri("http://127.0.0.1"), false, Guid.NewGuid()));
            services.AddSingleton<LkgStore>(_ => throw new InvalidOperationException("Local storage requires node identity."));
            services.AddSingleton<NodeClient>();
            using var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetRequiredService<NodeClient>());
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public async Task OperatorReadsActualProcessAndLkgThenRunsRetryAndSavedTargetProbeOnOneGateway()
    {
        await using var fixture=new GatewayFixture { ConfigureGateway=b=>
        {
            b.Configuration["Gateway:AutomaticUpdates"]="true";
            b.Configuration["Gateway:RedisNotifications"]="false";
        }};
        await fixture.InitializeAsync();
        var f=fixture.Control;
        await using(var db=f.Context())
        {
            var role=await db.Set<UserRole>().Where(r=>r.UserId==f.User.Id).Select(r=>r.RoleId).FirstAsync();
            foreach(var code in new[] { "gateway.read", "gateway.operate" })
            {
                var permission=new Permission { Code=code,Name=code,Module="gateway" };
                db.Add(permission);db.Add(new RolePermission {RoleId=role,PermissionId=permission.Id});
            }
            await db.SaveChangesAsync();
        }
        var desired=await fixture.PublishAsync();
        await fixture.ApplyBothAsync(desired);
        var services=fixture.Gateways[0].Services;
        var nodes=services.GetRequiredService<NodeClient>();
        var store=services.GetRequiredService<RuntimeGenerationStore>();
        await nodes.HeartbeatAsync(store,default);
        await nodes.HeartbeatAsync(store,default);
        var path="/api/v1/gateway-nodes/"+nodes.NodeId;
        using var read=await f.Client.GetAsync(path);
        read.EnsureSuccessStatusCode();
        var node=(await read.Content.ReadFromJsonAsync<GatewayNodeDto>())!;
        Assert.NotNull(node.Diagnostics);
        Assert.True(node.Diagnostics.WorkingSetBytes>0);
        Assert.InRange(node.Diagnostics.CpuPercent!.Value,0,100);
        Assert.Equal(desired.Envelope.ConfigVersion,node.Diagnostics.LastKnownGoodVersion);
        using var retry=await f.WriteAsync(HttpMethod.Post,path+"/retry",etag:read.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.Accepted,retry.StatusCode);
        var action=(await retry.Content.ReadFromJsonAsync<NodeOperation>())!;
        async Task<NodeOperation> Receipt(string url)
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                using var response=await f.Client.GetAsync(url,deadline.Token);response.EnsureSuccessStatusCode();
                var op=(await response.Content.ReadFromJsonAsync<NodeOperation>(deadline.Token))!;
                if(op.State=="Completed")return op;
                await Task.Delay(100,deadline.Token);
            }
        }
        Assert.Equal("applied",(await Receipt(path+"/operations/"+action.Id)).Outcome);

        var clusterPath="/api/v1/clusters/"+f.Cluster.Id;
        using var cluster=await f.Client.GetAsync(clusterPath);
        var dest=(await cluster.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("destinations")[0];
        var revision='"'+dest.GetProperty("revision").GetInt64().ToString()+'"';
        async Task<NodeOperation> Probe()
        {
            using var queued=await f.WriteAsync(HttpMethod.Post,clusterPath+"/destinations/"+f.Destination.Id+"/probe",etag:revision);
            Assert.Equal(HttpStatusCode.Accepted,queued.StatusCode);
            var op=(await queued.Content.ReadFromJsonAsync<NodeOperation>())!;
            return await Receipt(clusterPath+"/probes/"+op.Id);
        }
        var good=await Probe(); Assert.Equal("healthy",good.Outcome); Assert.Equal(200,good.HttpStatus);
        fixture.BackendA.Services.GetRequiredService<BackendProbe>().Unhealthy=true;
        var bad=await Probe(); Assert.Equal("unhealthy",bad.Outcome); Assert.Equal(503,bad.HttpStatus);
        using var changed=await f.WriteAsync(HttpMethod.Put,"/api/v1/destinations/"+f.Destination.Id,
            new { name="changed",address=fixture.BackendUrls[1]+"/another",weight=1,enabled=true },revision);
        changed.EnsureSuccessStatusCode();revision=changed.Headers.ETag!.Tag;
        Assert.Equal("target_changed",(await Probe()).Outcome);
    }
}

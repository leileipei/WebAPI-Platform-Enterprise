using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Gateway;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;

namespace WebApi.Integration.Tests;

public sealed class ClusterOperatorHealthTests
{
    [Fact]
    public async Task HealthCountsRequireCurrentInstancesAndSavedDestinationAndRouteNamesRequireRouteRead()
    {
        var provider = new RecordingSourceHandler();
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync(b =>
        {
            b.Configuration["Observability:Enabled"]="true";
            b.Services.PostConfigure<HttpClientFactoryOptions>("observability", o=>o.HttpMessageHandlerBuilderActions.Add(h=>h.PrimaryHandler=provider));
        });
        var f=scenario.Api;
        var snapshot=await scenario.DesiredAsync(await scenario.PublishAsync());
        var fingerprint=GatewayProbeTarget.Fingerprint(f.Destination.Address,f.Cluster.HealthCheckPath);
        object Row(int node, string component, string value, string? target=null) => new {
            metric = new Dictionary<string,string> {
                ["webapi_environment_id"]=f.Environment.Id.ToString(), ["service_instance_id"]="node-"+node,
                ["webapi_health_component"]=component, ["webapi_node_instance"]=scenario.Instances[node].ToString(),
                ["webapi_deployment_sequence"]=snapshot.Envelope.DeploymentSequence.ToString(), ["webapi_cluster_id"]=f.Cluster.Id.ToString(),
                ["webapi_destination_id"]=f.Destination.Id.ToString(), ["webapi_health_target"]=target??fingerprint },
            value = new object[] { DateTimeOffset.UtcNow.ToUnixTimeSeconds(), value } };
        void Submit(params object[] rows) => provider.Enqueue(System.Text.Json.JsonSerializer.Serialize(new { status="success", data=new { resultType="vector",result=rows }}));
        var path="/api/v1/clusters/"+f.Cluster.Id;
        Submit(Row(0,"active","1"),Row(1,"active","1"),Row(0,"passive","-2"),Row(1,"passive","-2"));
        using var read=await f.Client.GetAsync(path); read.EnsureSuccessStatusCode();
        var data=(await read.Content.ReadFromJsonAsync<ClusterDto>())!;
        Assert.Equal(1,data.HealthyDestinationCount);
        Assert.Equal("Healthy",data.Destinations.Single().ActiveHealth);
        Assert.Equal("Disabled",data.Destinations.Single().PassiveHealth);
        Assert.Equal(1,data.RouteCount);
        Assert.Equal("/orders",Assert.Single(data.Routes!).Path);
        Submit(Row(0,"active","1"),Row(1,"active","0"),Row(0,"passive","-2"),Row(1,"passive","-2"));
        using var list=await f.Client.GetAsync($"/api/v1/environments/{f.Environment.Id}/clusters");list.EnsureSuccessStatusCode();
        var unhealthy=Assert.Single((await list.Content.ReadFromJsonAsync<PageResult<ClusterDto>>())!.Items);
        Assert.Equal(0,unhealthy.HealthyDestinationCount);
        Assert.Equal("Unhealthy",Assert.Single(unhealthy.Destinations).ActiveHealth);

        await using var db=f.Context();
        var permission=await db.Set<Permission>().Where(p=>p.Code=="route.read").Select(p=>p.Id).SingleAsync();
        await db.Set<RolePermission>().Where(p=>p.PermissionId==permission).ExecuteDeleteAsync();
        Submit(Row(0,"active","1"),Row(1,"active","1","obsolete-draft"));
        using var partial=await f.Client.GetAsync(path);partial.EnsureSuccessStatusCode();
        var unknown=(await partial.Content.ReadFromJsonAsync<ClusterDto>())!;
        Assert.Null(unknown.HealthyDestinationCount);
        Assert.Equal("Unknown",Assert.Single(unknown.Destinations).ActiveHealth);
        Assert.Null(unknown.RouteCount);Assert.Null(unknown.Routes);
    }
}

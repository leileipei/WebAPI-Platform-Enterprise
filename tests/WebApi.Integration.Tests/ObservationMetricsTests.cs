using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Gateway;
using WebApi.Contracts.Observability;
using WebApi.Infrastructure.Observability;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ObservationMetricsTests
{
    private static async Task<ApiFixture> FixtureAsync(MetricProtocolHandler source,int nodes=1)
    {
        var fixture=new ApiFixture();await fixture.InitializeAsync(b=>{
            b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:PrometheusUrl"]="http://metric-source.invalid:9090";
            b.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(builder=>builder.PrimaryHandler=source));
        });await fixture.SeedConsumerAsync();await ObservationTestSupport.GrantAsync(fixture,["metrics.read"]);using var login=await fixture.LoginAsync();login.EnsureSuccessStatusCode();
        await using var db=fixture.Context();for(var n=0;n<nodes;n++)db.Add(new GatewayNode{EnvironmentId=fixture.Environment.Id,NodeName="node-"+n,Enabled=true});await db.SaveChangesAsync();
        source.EnvironmentId=fixture.Environment.Id;source.ApiId=fixture.Api.Id;source.ApplicationId=fixture.Application.Id;source.DestinationId=fixture.Destination.Id;source.ClusterId=fixture.Cluster.Id;return fixture;
    }
    private static string Url(ApiFixture fixture,MetricProtocolHandler source,Guid? environment=null,bool all=false,string? suffix=null)=>
        $"/api/v1/observability/metrics?organizationId={fixture.Organization.Id}&projectId={fixture.Project.Id}&environmentId={environment??fixture.Environment.Id}&allAccessibleEnvironments={all.ToString().ToLowerInvariant()}&start={Uri.EscapeDataString(source.Start.ToString("O"))}&end={Uri.EscapeDataString(source.End.ToString("O"))}{suffix}";
    private static MetricValueDto Value(ObservationEnvelope<MetricsDto> envelope,string key)=>Assert.Single(envelope.Data!.Kpis,x=>x.Metric==key);
    [Fact]
    public async Task DisabledDestinationHealthFollowsEachCurrentInstanceApplication()
    {
        var source = new MetricProtocolHandler();
        await using var deployment = new DeploymentScenario();
        await deployment.InitializeAsync(b =>
        {
            b.Configuration["Observability:Enabled"] = "true";
            b.Services.PostConfigure<HttpClientFactoryOptions>("observability",
                options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = source));
        });
        var f = deployment.Api;
        await ObservationTestSupport.GrantAsync(f, ["metrics.read"]);
        using var added = await f.WriteAsync(HttpMethod.Post, $"/api/v1/clusters/{f.Cluster.Id}/destinations",
            new SaveDestinationRequest("secondary", "http://test-backend:8080/secondary"));
        added.EnsureSuccessStatusCode();
        var target = (await added.Content.ReadFromJsonAsync<DestinationDto>())!;
        source.EnvironmentId = f.Environment.Id;
        source.ClusterId = f.Cluster.Id;
        source.DestinationId = f.Destination.Id;
        var firstRelease = await deployment.PublishAsync();
        var firstVersion = (await deployment.DesiredAsync(firstRelease)).Envelope.ConfigVersion;

        async Task Check(double? count, string node0, string node1, DateTimeOffset? end = null)
        {
            source.End = end ?? DateTimeOffset.UtcNow;
            using var response = await f.Client.GetAsync(Url(f, source, suffix: $"&destinationId={target.Id}"));
            response.EnsureSuccessStatusCode();
            var body = (await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
            Assert.True(body.Coverage.Complete);
            Assert.Equal(count, Value(body, "unhealthy_destinations").Value);
            var states = body.Data!.NodeHealth.ToDictionary(node => node.NodeName, node => Assert.Single(node.Destinations));
            Assert.Equal(2, states.Count);
            Assert.All(states.Values, row => Assert.Equal(f.Cluster.Id, row.ClusterId));
            Assert.Equal(node0, states["node-0"].Health);
            Assert.Equal(node1, states["node-1"].Health);
        }

        await using (var db = f.Context())
        {
            db.Add(new GatewayNode { EnvironmentId = f.Environment.Id, NodeName = "retired", Enabled = false });
            await db.SaveChangesAsync();
        }
        source.HealthSamples = [("node-0", target.Id, 1), ("node-1", target.Id, 1), ("retired", target.Id, 0),
            ("node-0", f.Destination.Id, 1), ("node-1", f.Destination.Id, 1)];
        await Check(0, "Healthy", "Healthy");
        using (var environment = await f.Client.GetAsync(Url(f, source)))
        {
            environment.EnsureSuccessStatusCode();
            Assert.Equal(0, Value((await environment.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!,
                "unhealthy_destinations").Value);
        }
        source.HealthSamples = [("node-0", target.Id, 0), ("node-1", target.Id, 0)];
        await Check(1, "Unhealthy", "Unhealthy");
        await using (var db = f.Context())
        {
            // The ACK covers the delivered bytes. The searchable JSON copy can disagree.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE gateway_config_snapshots s SET payload = jsonb_set(s.payload, ARRAY['clusters'], '[]'::jsonb)
                FROM gateway_config_versions v WHERE s.config_version_id = v.id
                    AND v.environment_id = {f.Environment.Id} AND v.version_no = {firstVersion}
                """);
        }
        await Check(1, "Unhealthy", "Unhealthy");
        using var draft = await f.WriteAsync(HttpMethod.Put, $"/api/v1/destinations/{target.Id}",
            new SaveDestinationRequest(target.Name, target.Address, Enabled: false), added.Headers.ETag!.ToString());
        draft.EnsureSuccessStatusCode();
        var beforeDeployment = DateTimeOffset.UtcNow;
        await Check(1, "Unhealthy", "Unhealthy");
        var release = await deployment.PublishAsync(acknowledge: false);
        var envelope = (await deployment.DesiredAsync(release)).Envelope;
        await Check(1, "Unhealthy", "Unhealthy");

        await deployment.AckAsync(0, envelope);
        await Check(1, "Disabled", "Unhealthy");
        source.HealthSamples = [];
        await Check(null, "Disabled", "Unknown");
        await deployment.AckAsync(1, envelope);
        await Check(0, "Disabled", "Disabled");
        source.HealthSamples = [("node-0", target.Id, 0), ("node-1", target.Id, 0)];
        await Check(1, "Unhealthy", "Unhealthy", beforeDeployment);

        deployment.Instances[0] = Guid.NewGuid();
        using var registered = await deployment.NodeAsync(0, "/internal/v1/nodes/register",
            new RegisterNodeRequest(f.Environment.Id, "node-0", deployment.Instances[0], "test"));
        registered.EnsureSuccessStatusCode();
        using var heartbeat = await deployment.NodeAsync(0, $"/internal/v1/nodes/{deployment.NodeIds[0]}/heartbeat",
            new NodeHeartbeat(deployment.Instances[0], envelope.ConfigVersion, envelope.DeploymentSequence, "Ready"));
        heartbeat.EnsureSuccessStatusCode();
        source.HealthSamples = [];
        await Check(null, "Unknown", "Disabled");
        await deployment.AckAsync(0, envelope);
        await Check(0, "Disabled", "Disabled");

        await using (var db = f.Context())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE gateway_config_snapshots s SET payload_bytes = convert_to('[]', 'UTF8'), size_bytes = 2
                FROM gateway_config_versions v WHERE s.config_version_id = v.id
                    AND v.environment_id = {f.Environment.Id} AND v.version_no = {envelope.ConfigVersion}
                """);
        }
        await Check(null, "Unknown", "Unknown");
    }

    [Fact]
    public async Task DeletedDestinationMetricsAndNamesFollowAcknowledgedMembershipAtQueryEnd()
    {
        var source = new MetricProtocolHandler();
        await using var deployment = new DeploymentScenario();
        await deployment.InitializeAsync(builder =>
        {
            builder.Configuration["Observability:Enabled"] = "true";
            builder.Services.PostConfigure<HttpClientFactoryOptions>("observability",
                options => options.HttpMessageHandlerBuilderActions.Add(client => client.PrimaryHandler = source));
        });
        var f = deployment.Api;
        await ObservationTestSupport.GrantAsync(f, ["metrics.read"]);
        using var added = await f.WriteAsync(HttpMethod.Post, $"/api/v1/clusters/{f.Cluster.Id}/destinations",
            new SaveDestinationRequest("orders replica", "http://test-backend:8080/replica"));
        added.EnsureSuccessStatusCode();
        var target = (await added.Content.ReadFromJsonAsync<DestinationDto>())!;
        source.EnvironmentId = f.Environment.Id;
        source.ApiId = f.Api.Id;
        source.ApplicationId = f.Application.Id;
        source.ClusterId = f.Cluster.Id;
        source.DestinationId = target.Id;
        source.HealthSamples = [("node-0", target.Id, 0), ("node-1", target.Id, 0),
            ("node-0", f.Destination.Id, 1), ("node-1", f.Destination.Id, 1)];
        await deployment.PublishAsync();
        var historicalEnd = DateTimeOffset.UtcNow;

        async Task Check(double? unhealthy, string node0, string node1, DateTimeOffset? end = null)
        {
            source.End = end ?? DateTimeOffset.UtcNow;
            foreach (var suffix in new[] { "&groupBy=Destination", $"&groupBy=Destination&destinationId={target.Id}" })
            {
                using var response = await f.Client.GetAsync(Url(f, source, suffix: suffix));
                response.EnsureSuccessStatusCode();
                var body = (await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
                Assert.Equal(unhealthy, Value(body, "unhealthy_destinations").Value);
                Assert.Equal(target.Name, Assert.Single(body.Data!.Groups).Name);
                string Status(string node) => Assert.Single(body.Data.NodeHealth.Single(n => n.NodeName == node).Destinations,
                    d => d.DestinationId == target.Id && d.ClusterId == f.Cluster.Id).Health;
                Assert.Equal(node0, Status("node-0"));
                Assert.Equal(node1, Status("node-1"));
            }
        }

        using var rename = await f.WriteAsync(HttpMethod.Put, $"/api/v1/destinations/{target.Id}",
            new SaveDestinationRequest("unpublished name", target.Address), added.Headers.ETag!.ToString());
        rename.EnsureSuccessStatusCode();
        await Check(1, "Unhealthy", "Unhealthy");
        using var deleted = await f.WriteAsync(HttpMethod.Delete, $"/api/v1/destinations/{target.Id}", etag: rename.Headers.ETag!.ToString());
        deleted.EnsureSuccessStatusCode();
        await Check(1, "Unhealthy", "Unhealthy");
        using var traffic = await f.Client.GetAsync(Url(f, source,
            suffix: $"&apiId={f.Api.Id}&applicationId={f.Application.Id}&destinationId={target.Id}"));
        traffic.EnsureSuccessStatusCode();
        var combined = (await traffic.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        Assert.Equal(200, Value(combined, "request_count").Value);
        Assert.Equal(SourceState.NotApplicable, Value(combined, "unhealthy_destinations").State);
        var release = await deployment.PublishAsync(acknowledge: false);
        var envelope = (await deployment.DesiredAsync(release)).Envelope;
        await Check(1, "Unhealthy", "Unhealthy");
        var other = new EnvironmentRecord { ProjectId = f.Project.Id, Code = "OTHER", Name = "其他环境" };
        await using (var db = f.Context()) { db.Add(other); await db.SaveChangesAsync(); }
        using var outside = await f.Client.GetAsync(Url(f, source, other.Id, suffix: $"&destinationId={target.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);

        await deployment.AckAsync(0, envelope);
        await Check(1, "Disabled", "Unhealthy");
        using var heartbeat = await deployment.NodeAsync(1, $"/internal/v1/nodes/{deployment.NodeIds[1]}/heartbeat",
            new NodeHeartbeat(deployment.Instances[1], envelope.ConfigVersion, envelope.DeploymentSequence, "Ready"));
        heartbeat.EnsureSuccessStatusCode();
        source.HealthSamples = [("node-0", target.Id, 0), ("node-1", target.Id, 1),
            ("node-0", f.Destination.Id, 1), ("node-1", f.Destination.Id, 1)];
        await Check(null, "Disabled", "Unknown");

        await deployment.AckAsync(1, envelope);
        source.End = DateTimeOffset.UtcNow;
        using var filtered = await f.Client.GetAsync(Url(f, source, suffix: $"&destinationId={target.Id}"));
        filtered.EnsureSuccessStatusCode();
        Assert.Equal(200, Value((await filtered.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!, "request_count").Value);
        using var environment = await f.Client.GetAsync(Url(f, source));
        environment.EnsureSuccessStatusCode();
        var retired = (await environment.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        Assert.Equal(0, Value(retired, "unhealthy_destinations").Value);
        Assert.All(retired.Data!.NodeHealth, node => Assert.DoesNotContain(node.Destinations, d => d.DestinationId == target.Id));
        source.HealthSamples = [("node-0", target.Id, 0), ("node-1", target.Id, 0),
            ("node-0", f.Destination.Id, 1), ("node-1", f.Destination.Id, 1)];
        await Check(1, "Unhealthy", "Unhealthy", historicalEnd);
    }

    [Fact]
    public async Task AHealthyGaugeCannotProveZeroBeforeEveryFirstApplication()
    {
        var source = new MetricProtocolHandler();
        await using var deployment = new DeploymentScenario();
        await deployment.InitializeAsync(b =>
        {
            b.Configuration["Observability:Enabled"] = "true";
            b.Services.PostConfigure<HttpClientFactoryOptions>("observability",
                options => options.HttpMessageHandlerBuilderActions.Add(client => client.PrimaryHandler = source));
        });
        var f = deployment.Api;
        await ObservationTestSupport.GrantAsync(f, ["metrics.read"]);
        source.EnvironmentId = f.Environment.Id;
        source.DestinationId = f.Destination.Id;
        source.ClusterId = f.Cluster.Id;
        source.HealthSamples = [("node-0", f.Destination.Id, 1), ("node-1", f.Destination.Id, 1)];
        var release = await deployment.PublishAsync(acknowledge: false);
        var envelope = (await deployment.DesiredAsync(release)).Envelope;
        var beforeFirst = DateTimeOffset.UtcNow;
        async Task Check(DateTimeOffset end, double? count)
        {
            source.End = end;
            using var response = await f.Client.GetAsync(Url(f, source));
            response.EnsureSuccessStatusCode();
            var body = (await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
            Assert.Equal(count, Value(body, "unhealthy_destinations").Value);
        }
        await Check(beforeFirst, null);
        await deployment.AckAsync(0, envelope);
        await Check(DateTimeOffset.UtcNow, null);
        await deployment.AckAsync(1, envelope);
        await Check(DateTimeOffset.UtcNow, 0);
        await Check(beforeFirst, null);
    }

    [Fact]
    public async Task RetainedClusterKeepsItsFrozenDestinationNameDuringAnUnrelatedRelease()
    {
        var source = new MetricProtocolHandler();
        await using var deployment = new DeploymentScenario();
        await deployment.InitializeAsync(b =>
        {
            b.Configuration["Observability:Enabled"] = "true";
            b.Services.PostConfigure<HttpClientFactoryOptions>("observability",
                options => options.HttpMessageHandlerBuilderActions.Add(client => client.PrimaryHandler = source));
        });
        var f = deployment.Api;
        await ObservationTestSupport.GrantAsync(f, ["metrics.read"]);
        await deployment.PublishAsync();
        using var rename = await f.WriteAsync(HttpMethod.Put, $"/api/v1/destinations/{f.Destination.Id}",
            new SaveDestinationRequest("new draft name", f.Destination.Address), "\"1\"");
        rename.EnsureSuccessStatusCode();

        async Task<Guid> Create(string path, object body)
        {
            using var reply = await f.WriteAsync(HttpMethod.Post, path, body);
            reply.EnsureSuccessStatusCode();
            return (await reply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        var api = await Create($"/api/v1/projects/{f.Project.Id}/apis", new SaveApiRequest("STOCK", "库存"));
        var version = await Create($"/api/v1/apis/{api}/versions", new CreateVersionRequest("1.0"));
        var cluster = await Create($"/api/v1/environments/{f.Environment.Id}/clusters", new SaveClusterRequest("Stock"));
        await Create($"/api/v1/clusters/{cluster}/destinations", new SaveDestinationRequest("inventory", "http://test-backend:8080/inventory"));
        await Create($"/api/v1/environments/{f.Environment.Id}/routes",
            new SaveRouteRequest(null, version, "Stock", "/inventory", ["GET"], cluster, TimeoutMs: 30000));
        var release = await Create($"/api/v1/environments/{f.Environment.Id}/releases", await f.PreviewReleaseRequestAsync(1, version));
        using var submitted = await ApiFixture.CommandAsync(f.Client, $"/api/v1/releases/{release}/submit");
        submitted.EnsureSuccessStatusCode();
        await deployment.ApproveAsync(release);
        await deployment.BuildAsync(release);
        await deployment.AckAllAsync(release);

        source.EnvironmentId = f.Environment.Id;
        source.ApiId = f.Api.Id;
        source.ApplicationId = f.Application.Id;
        source.DestinationId = f.Destination.Id;
        source.ClusterId = f.Cluster.Id;
        source.End = DateTimeOffset.UtcNow;
        using var response = await f.Client.GetAsync(Url(f, source, suffix: $"&groupBy=Destination&destinationId={f.Destination.Id}"));
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        Assert.Equal(f.Destination.Name, Assert.Single(body.Data!.Groups).Name);
    }

    [Fact]
    public async Task SharedApplicationMetricsFollowEachAuthorizedEnvironmentAcrossProjects()
    {
        var source = new MetricProtocolHandler();
        await using var f = await FixtureAsync(source);
        var shared = await SharedObservationFixture.AddApplicationAsync(f);
        var (project, api, allowed, denied) = await SharedObservationFixture.AddSecondProjectAsync(f, shared.Id);
        source.ApplicationId = shared.Id;
        string Query(Guid projectId, Guid env, bool all = false) => Url(f, source, env, all,
            $"&applicationId={shared.Id}&groupBy=Application").Replace($"projectId={f.Project.Id}", $"projectId={projectId}");
        async Task AssertVisible(string url)
        {
            using var response = await f.Client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
            Assert.Equal(200, Value(result, "request_count").Value);
            Assert.Equal(shared.Name, Assert.Single(result.Data!.Groups).Name);
        }
        await AssertVisible(Query(f.Project.Id, f.Environment.Id));
        source.EnvironmentId = allowed.Id;
        source.ApiId = api.Id;
        source.DefaultNodeName = "allowed-node";
        source.NoDestination = true;
        await AssertVisible(Query(project.Id, allowed.Id));
        await AssertVisible(Query(project.Id, allowed.Id, all: true));
        using var absent = await f.Client.GetAsync(Query(project.Id, denied.Id));
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        // The provider returns only a sibling environment's rows with otherwise valid labels.
        source.EnvironmentId = denied.Id;
        source.DefaultNodeName = "denied-node";
        var allApplications = Url(f, source, allowed.Id, all: true, suffix: "&groupBy=Application")
            .Replace($"projectId={f.Project.Id}", $"projectId={project.Id}");
        using var aggregate = await f.Client.GetAsync(allApplications);
        aggregate.EnsureSuccessStatusCode();
        var body = (await aggregate.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        Assert.Empty(body.Data!.Groups);
        Assert.Equal(0, Value(body, "request_count").SampleCount);
        Assert.Empty(body.Data.Trends["request_rps"]);
        Assert.Empty(body.Data.Trends["latency_p95_ms"]);
    }

    [Fact]
    public async Task SharedApplicationMetricsKeepOnlyRuntimeIdentitiesFromTheScopedOrganizationAndEnvironment()
    {
        var source = new MetricProtocolHandler();
        await using var f = await FixtureAsync(source);
        var shared = await SharedObservationFixture.AddApplicationAsync(f);
        var other = new Organization { Code = "OTHER", Name = "其他组织" };
        var foreign = new ApplicationRecord { OrganizationId = other.Id, Code = "OTHER_APP", Name = "其他组织应用" };
        var sibling = new EnvironmentRecord { ProjectId = f.Project.Id, Code = "SIBLING", Name = "其他环境" };
        var version = new GatewayConfigVersion { EnvironmentId = f.Environment.Id, VersionNo = 1, CreatedBy = f.User.Id };
        var payload = JsonSerializer.Serialize(new { environmentId = f.Environment.Id, applications = new[] {
            new { id = shared.Id }, new { id = foreign.Id } } });
        await using (var db = f.Context())
        {
            db.Remove(await db.Set<ApplicationApiPermission>().SingleAsync());
            db.AddRange(other, foreign, sibling, version, new GatewayConfigSnapshot { ConfigVersionId = version.Id,
                Payload = payload, PayloadBytes = System.Text.Encoding.UTF8.GetBytes(payload), SizeBytes = System.Text.Encoding.UTF8.GetByteCount(payload) });
            await db.SaveChangesAsync();
        }
        source.ApplicationId = shared.Id;
        using var historical = await f.Client.GetAsync(Url(f, source, suffix: $"&applicationId={shared.Id}"));
        historical.EnsureSuccessStatusCode();
        Assert.Equal(200, Value((await historical.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!, "request_count").Value);
        using var missing = await f.Client.GetAsync(Url(f, source, sibling.Id, suffix: $"&applicationId={shared.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var outside = await f.Client.GetAsync(Url(f, source, suffix: $"&applicationId={foreign.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }

    [Fact] public async Task EnvironmentOnlyGrantCannotReadSiblingMetrics()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);var sibling=new EnvironmentRecord{ProjectId=s.Project.Id,Code="SIBLING",Name="另一个环境"};
        await using(var db=s.Context()){db.Add(sibling);var grant=await db.Set<UserProjectScope>().SingleAsync();grant.ProjectId=s.Project.Id;grant.EnvironmentId=s.Environment.Id;await db.SaveChangesAsync();}
        using var foreign=await s.Client.GetAsync(Url(s,source,sibling.Id));Assert.Equal(HttpStatusCode.NotFound,foreign.StatusCode);Assert.Empty(source.Requests);
        using var own=await s.Client.GetAsync(Url(s,source,suffix:$"&apiId={s.Api.Id}"));own.EnsureSuccessStatusCode();
        using var api=await s.Client.GetAsync(Url(s,source,suffix:null).Replace("/metrics?",$"/apis/{s.Api.Id}/metrics?"));api.EnsureSuccessStatusCode();
        await using(var db=s.Context()){db.RemoveRange(await db.Set<UserProjectScope>().ToArrayAsync());await db.SaveChangesAsync();}
        using var revoked=await s.Client.GetAsync(Url(s,source));Assert.Equal(HttpStatusCode.NotFound,revoked.StatusCode);
    }
    [Fact] public async Task ProjectAllReturnsOnlyAllowedEnvironments()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);var sibling=new EnvironmentRecord{ProjectId=s.Project.Id,Code="SIBLING",Name="不可见环境"};
        await using(var db=s.Context()){db.Add(sibling);var grant=await db.Set<UserProjectScope>().SingleAsync();grant.ProjectId=s.Project.Id;grant.EnvironmentId=s.Environment.Id;await db.SaveChangesAsync();}
        source.ForeignEnvironmentId=sibling.Id;using var response=await s.Client.GetAsync(Url(s,source,all:true));response.EnsureSuccessStatusCode();
        var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;Assert.Equal(200,Value(body,"request_count").Value);
        Assert.NotEmpty(source.Requests);foreach(var request in source.Requests)Assert.DoesNotContain(sibling.Id.ToString(),Uri.UnescapeDataString(request.Query));
    }
    [Theory][InlineData("api")][InlineData("app")]
    public async Task FilteredResourceDoesNotAttributeUnrelatedEnvironmentBackendHealth(string kind)
    {
        var source=new MetricProtocolHandler();await using var f=await FixtureAsync(source);
        var other=new Api{OrganizationId=f.Organization.Id,ProjectId=f.Project.Id,Code="OTHER_HEALTH",Name="Other",OwnerUserId=f.User.Id,LifecycleStatus="Active"};var cluster=new UpstreamCluster{EnvironmentId=f.Environment.Id,ProjectId=f.Project.Id,Name="OtherBackend",LoadBalancingPolicy="RoundRobin",HealthCheckPath="/health",HealthCheckIntervalSec=2};var dest=new UpstreamDestination{ClusterId=cluster.Id,Name="Other",Address="http://backend-b:8080/",Weight=1};
        await using(var db=f.Context()){db.AddRange(other,cluster,dest);await db.SaveChangesAsync();}source.UnhealthyDestinationId=dest.Id;source.UnhealthyClusterId=cluster.Id;
        using(var environment=await f.Client.GetAsync(Url(f,source))){environment.EnsureSuccessStatusCode();Assert.Equal(1,Value((await environment.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!,"unhealthy_destinations").Value);}
        using var filtered=await f.Client.GetAsync(Url(f,source,suffix:kind=="api"?$"&apiId={f.Api.Id}":$"&applicationId={f.Application.Id}"));filtered.EnsureSuccessStatusCode();var body=(await filtered.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        Assert.Null(Value(body,"unhealthy_destinations").Value);Assert.Equal(SourceState.NotApplicable,Value(body,"unhealthy_destinations").State);Assert.All(body.Data!.NodeHealth,n=>Assert.Empty(n.Destinations));
    }
    [Fact] public async Task HistogramBucketsMergeBeforeQuantile()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source,2);using var response=await s.Client.GetAsync(Url(s,source));response.EnsureSuccessStatusCode();
        var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;Assert.Equal(9100,Value(body,"latency_p95_ms").Value!.Value,3);
        Assert.DoesNotContain(source.Requests,x=>Uri.UnescapeDataString(x.Query).Contains("histogram_quantile",StringComparison.Ordinal));
    }
    [Fact] public async Task MissingNodeMakesPartialNotZero()
    {
        var source=new MetricProtocolHandler{MissingSecondNode=true};await using var s=await FixtureAsync(source,2);using var response=await s.Client.GetAsync(Url(s,source));response.EnsureSuccessStatusCode();
        var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;Assert.Equal(SourceState.Partial,body.SourceState);Assert.Contains("node-1",body.Coverage.MissingNodes);
        Assert.Null(Value(body,"request_rps").Value);Assert.Null(Value(body,"success_ratio").Value);
    }
    [Fact] public async Task CounterResetNeverProducesNegativeRps()
    {
        var source=new MetricProtocolHandler{ResetWindow=true};await using var s=await FixtureAsync(source);using var response=await s.Client.GetAsync(Url(s,source));response.EnsureSuccessStatusCode();
        var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;Assert.True(Value(body,"request_rps").Value>=0);
        Assert.Contains(source.Requests,x=>Uri.UnescapeDataString(x.Query).Contains("increase(webapi_gateway_requests_total",StringComparison.Ordinal));
        Assert.All(body.Data!.Trends["request_rps"],x=>Assert.True(x.Value>=0));
    }
    [Theory][InlineData("Ready")][InlineData("NotReady")][InlineData("Degraded")]
    public async Task RegisteredOperationalNodeStatesRemainExpectedCollectors(string status)
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);
        await using(var db=s.Context()){var node=await db.Set<GatewayNode>().SingleAsync();node.Status=status;await db.SaveChangesAsync();}
        using var response=await s.Client.GetAsync(Url(s,source));response.EnsureSuccessStatusCode();
        var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        Assert.Equal(SourceState.Available,body.SourceState);Assert.True(body.Coverage.Complete);Assert.Single(body.Data!.NodeHealth);Assert.Equal(200,Value(body,"request_count").Value);
    }
    [Fact] public async Task LatencyRankingUsesMergedLatencyRatherThanTrafficCount()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);
        var fast=new Api{OrganizationId=s.Organization.Id,ProjectId=s.Project.Id,Code="FAST",Name="快速高流量 API",LifecycleStatus="Active",OwnerUserId=s.User.Id};
        await using(var db=s.Context()){db.Add(fast);await db.SaveChangesAsync();}source.SecondApiId=fast.Id;
        async Task<ObservationEnvelope<MetricsDto>> Query(string sort){using var r=await s.Client.GetAsync(Url(s,source,suffix:"&groupBy=Api&sortBy="+sort));r.EnsureSuccessStatusCode();return(await r.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;}
        Assert.Equal(fast.Id.ToString(),(await Query("request_rps")).Data!.Groups[0].Key);
        Assert.Equal(s.Api.Id.ToString(),(await Query("latency_p95_ms")).Data!.Groups[0].Key);
        Assert.Equal(s.Api.Id.ToString(),(await Query("latency_p99_ms")).Data!.Groups[0].Key);
    }
    [Fact] public async Task MiddleCollectionGapCannotClaimCompleteEvenWithFreshEndpoints()
    {
        var source=new MetricProtocolHandler{MiddleGap=true};await using var s=await FixtureAsync(source);
        using var response=await s.Client.GetAsync(Url(s,source));response.EnsureSuccessStatusCode();
        var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        Assert.Equal(SourceState.Partial,body.SourceState);Assert.False(body.Coverage.Complete);Assert.Null(Value(body,"request_rps").Value);
        Assert.Equal("time_window_coverage_incomplete",body.Coverage.Reason);
        Assert.Empty(body.Coverage.MissingNodes);
        Assert.Equal("Available", Assert.Single(body.Data!.NodeHealth).Health);
        Assert.Contains(source.Requests,x=>Uri.UnescapeDataString(x.Query).Contains("max_over_time((time()",StringComparison.Ordinal));
    }
    [Fact] public async Task AllRequiredTrendSeriesUseRealSamplesAndUnknownStaysNull()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);
        using var response=await s.Client.GetAsync(Url(s,source));response.EnsureSuccessStatusCode();
        var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        foreach(var metric in new[]{"request_rps","success_ratio","error_4xx_ratio","error_5xx_ratio","latency_p50_ms","latency_p95_ms","latency_p99_ms"})Assert.NotEmpty(body.Data!.Trends[metric]);
        Assert.All(body.Data!.Trends["success_ratio"],x=>Assert.Equal(1,x.Value));Assert.All(body.Data.Trends["error_4xx_ratio"],x=>Assert.Equal(0,x.Value));
    }
    [Fact]
    public async Task LongRangeTrendsStayAvailableWithManyApplications()
    {
        var source = new MetricProtocolHandler { WindowHours = 24 * 7 };
        await using var fixture = await FixtureAsync(source);
        using var services = fixture.Services();
        var resolver = services.ServiceProvider.GetRequiredService<ObservationScopeResolver>();
        var metrics = services.ServiceProvider.GetRequiredService<PrometheusMetricSource>();
        var scope = await resolver.ResolveAsync(new(fixture.User.Id, "many-apps"), "metrics.read",
            new(fixture.Organization.Id, fixture.Project.Id, fixture.Environment.Id, false), null, null, null, default);
        var authorized = Enumerable.Range(0, 99).Select(_ => Guid.NewGuid()).Append(source.ApplicationId).ToArray();
        source.RangeApplications = [.. authorized, Guid.NewGuid()];
        scope = scope with { Applications = authorized.ToDictionary(id => id, _ => new ObservationApplication("Application", null)) };

        var result = await metrics.QueryAsync(scope, new(source.Start, source.End), new(null, null, null), default);

        Assert.Equal(SourceState.Available, result.SourceState);
        var traffic = result.Data!.Trends["request_rps"];
        Assert.NotEmpty(traffic);
        Assert.All(traffic, point => Assert.Equal((double)authorized.Length, point.Value));
        Assert.All(result.Data.Trends["latency_p95_ms"], point => Assert.NotNull(point.Value));
        Assert.NotEmpty(result.Data.Trends["latency_p95_ms"]);
    }

    [Fact]
    public async Task EnvironmentTrendQueriesCanMakeProgressTogetherWithinTheSharedDeadline()
    {
        var simultaneous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = 0;
        var source = new MetricProtocolHandler
        {
            BeforeLatencyRange = async token =>
            {
                if (Interlocked.Increment(ref pending) == 2) simultaneous.TrySetResult();
                await simultaneous.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            }
        };
        await using var fixture = await FixtureAsync(source);
        using var services = fixture.Services();
        var resolver = services.ServiceProvider.GetRequiredService<ObservationScopeResolver>();
        var metrics = services.ServiceProvider.GetRequiredService<PrometheusMetricSource>();
        var scope = await resolver.ResolveAsync(new(fixture.User.Id, "parallel-trends"), "metrics.read",
            new(fixture.Organization.Id, fixture.Project.Id, fixture.Environment.Id, false), null, null, null, default);
        scope = scope with { EnvironmentIds = [fixture.Environment.Id, Guid.NewGuid()] };

        var result = await metrics.QueryAsync(scope, new(source.Start, source.End), new(null, null, null), default);

        Assert.Equal(2, pending);
        Assert.NotEmpty(result.Data!.Trends["latency_p95_ms"]);
    }

    [Fact] public async Task SourceFailureReturns503()
    {
        var source=new MetricProtocolHandler{Status=503};await using var s=await FixtureAsync(source);using var response=await s.Client.GetAsync(Url(s,source));Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        var body=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("observability_source_unavailable",body.GetProperty("code").GetString());Assert.DoesNotContain("metric-source.invalid",body.GetRawText());
    }
    [Fact] public async Task ProjectAllRevocationRejectsRatherThanReturningEmptySuccess()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);
        await using(var db=s.Context()){var grant=await db.Set<UserProjectScope>().SingleAsync();grant.ProjectId=s.Project.Id;grant.EnvironmentId=s.Environment.Id;await db.SaveChangesAsync();}
        using(var own=await s.Client.GetAsync(Url(s,source,all:true)))own.EnsureSuccessStatusCode();var queries=source.Requests.Count;
        await using(var db=s.Context()){db.RemoveRange(await db.Set<UserProjectScope>().ToArrayAsync());await db.SaveChangesAsync();}
        using var revoked=await s.Client.GetAsync(Url(s,source,all:true));Assert.Equal(HttpStatusCode.NotFound,revoked.StatusCode);Assert.Equal(queries,source.Requests.Count);
    }
    [Fact] public async Task NodeHealthCarriesItsEnvironmentIdentity()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);var sibling=new EnvironmentRecord{ProjectId=s.Project.Id,Code="SECOND",Name="第二环境"};
        await using(var db=s.Context()){db.Add(sibling);db.Add(new GatewayNode{EnvironmentId=sibling.Id,NodeName="second-node",Enabled=true});await db.SaveChangesAsync();}
        using var response=await s.Client.GetAsync(Url(s,source,all:true));response.EnsureSuccessStatusCode();var body=await response.Content.ReadFromJsonAsync<JsonElement>();var nodes=body.GetProperty("data").GetProperty("nodeHealth").EnumerateArray().ToArray();
        var available=Assert.Single(nodes,x=>x.GetProperty("health").GetString()=="Available");Assert.Equal(s.Environment.Id,available.GetProperty("environmentId").GetGuid());
        var stale=Assert.Single(nodes,x=>x.GetProperty("health").GetString()=="Stale");Assert.Equal(sibling.Id,stale.GetProperty("environmentId").GetGuid());
        var coverage = body.GetProperty("coverage");
        Assert.Equal("Partial", body.GetProperty("sourceState").GetString());
        Assert.False(coverage.GetProperty("complete").GetBoolean());
        Assert.Equal("second-node", Assert.Single(coverage.GetProperty("missingNodes").EnumerateArray()).GetString());
        Assert.False(coverage.TryGetProperty("missingNodeIdentities", out _));
    }
    [Fact]
    public async Task SameNamedCollectorsKeepIndependentHealthAndRecoverAcrossEnvironments()
    {
        var source = new MetricProtocolHandler();
        await using var fixture = await FixtureAsync(source);
        var sibling = new EnvironmentRecord { ProjectId = fixture.Project.Id, Code = "SECOND", Name = "第二环境" };
        await using (var db = fixture.Context())
        {
            db.AddRange(sibling, new GatewayNode { EnvironmentId = sibling.Id, NodeName = "second-node", Enabled = true });
            await db.SaveChangesAsync();
        }
        using var services = fixture.Services();
        var resolver = services.ServiceProvider.GetRequiredService<ObservationScopeResolver>();
        var metrics = services.ServiceProvider.GetRequiredService<PrometheusMetricSource>();
        async Task<TrustedObservationScope> Resolve(Guid? environment = null)
        {
            var authorized = await resolver.ResolveAsync(new(fixture.User.Id, "node-health"), "metrics.read",
                new(fixture.Organization.Id, fixture.Project.Id, environment, environment is null), null, null, null, default);
            // Registration enforces globally unique names. Exercise a name collision at the observation boundary.
            return authorized with { ExpectedNodes = authorized.ExpectedNodes.Select(n => n with { NodeName = "node-0" }).ToArray() };
        }
        var scope = await Resolve();
        var range = new TimeRange(source.Start, source.End);
        Task<ObservationEnvelope<MetricsDto>> Query() => metrics.QueryAsync(scope, range, new(null, null, null), default);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        void AssertSiblingIdentity(CoverageDto coverage)
        {
            var json = JsonSerializer.SerializeToElement(coverage, jsonOptions);
            Assert.True(json.TryGetProperty("missingNodeIdentities", out var identities));
            var missing = Assert.Single(identities.EnumerateArray());
            Assert.Equal("node-0", missing.GetProperty("nodeName").GetString());
            Assert.Equal(sibling.Id, missing.GetProperty("environmentId").GetGuid());
        }
        void AssertOnlySiblingStale(ObservationEnvelope<MetricsDto> result)
        {
            var nodes = result.Data!.NodeHealth;
            Assert.Equal(2, nodes.Count);
            Assert.All(nodes, node => Assert.Equal("node-0", node.NodeName));
            var fresh = Assert.Single(nodes, node => node.EnvironmentId == fixture.Environment.Id);
            Assert.Equal("Available", fresh.Health);
            Assert.Equal("Healthy", Assert.Single(fresh.Destinations).Health);
            var stale = Assert.Single(nodes, node => node.EnvironmentId == sibling.Id);
            Assert.Equal("Stale", stale.Health);
            Assert.Empty(stale.Destinations);
            Assert.Equal(SourceState.Partial, result.SourceState);
            Assert.Equal("missing_or_stale_nodes", result.Coverage.Reason);
            Assert.Equal(["node-0"], result.Coverage.MissingNodes);
            Assert.Null(Value(result, "request_rps").Value);
            AssertSiblingIdentity(result.Coverage);
        }
        AssertOnlySiblingStale(await Query());
        var signals = services.ServiceProvider.GetRequiredService<ObservationSignalCoverage>();
        foreach (var signal in new[] { "logs", "traces" })
        {
            var collection = await signals.QueryAsync(scope, range, signal, default);
            Assert.Equal(SourceState.Partial, collection.State);
            AssertSiblingIdentity(collection.Coverage);
        }
        var isolated = await metrics.QueryAsync(await Resolve(sibling.Id), range, new(null, null, null), default);
        var onlyNode = Assert.Single(isolated.Data!.NodeHealth);
        Assert.Equal(sibling.Id, onlyNode.EnvironmentId);
        Assert.Equal("Stale", onlyNode.Health);
        Assert.Equal(["node-0"], isolated.Coverage.MissingNodes);
        Assert.Equal(0, Value(isolated, "request_count").SampleCount);
        Assert.Empty(isolated.Data.Trends["request_rps"]);
        Assert.False(JsonSerializer.SerializeToElement(isolated.Coverage, jsonOptions).TryGetProperty("missingNodeIdentities", out _));
        source.PeerHeartbeat = (sibling.Id, 60);
        AssertOnlySiblingStale(await Query());
        source.PeerHeartbeat = (sibling.Id, 2);
        var recovered = await Query();
        Assert.Equal(SourceState.Available, recovered.SourceState);
        Assert.True(recovered.Coverage.Complete);
        Assert.Empty(recovered.Coverage.MissingNodes);
        Assert.False(JsonSerializer.SerializeToElement(recovered.Coverage, jsonOptions).TryGetProperty("missingNodeIdentities", out _));
        Assert.All(recovered.Data!.NodeHealth, node => Assert.Equal("Available", node.Health));
        Assert.Equal(200, Value(recovered, "request_count").Value);
    }
    [Fact] public async Task DestinationLabelMustBelongToItsReportedEnvironment()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);var sibling=new EnvironmentRecord{ProjectId=s.Project.Id,Code="SECOND",Name="第二环境"};
        await using(var db=s.Context()){db.Add(sibling);await db.SaveChangesAsync();}source.ForeignEnvironmentId=sibling.Id;
        using var response=await s.Client.GetAsync(Url(s,source,all:true));response.EnsureSuccessStatusCode();var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        Assert.Equal(200,Value(body,"request_count").SampleCount);
    }
    [Fact] public async Task EnvironmentWithNoCollectionNodesCannotClaimCompleteCoverage()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);var sibling=new EnvironmentRecord{ProjectId=s.Project.Id,Code="EMPTY",Name="未接入采集环境"};
        await using(var db=s.Context()){db.Add(sibling);await db.SaveChangesAsync();}
        using var response=await s.Client.GetAsync(Url(s,source,all:true));response.EnsureSuccessStatusCode();var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
        Assert.False(body.Coverage.Complete);Assert.Null(Value(body,"request_rps").Value);
    }
    [Fact] public async Task ApiPathAndQueryCannotSelectDifferentResources()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);
        using var response=await s.Client.GetAsync(Url(s,source,suffix:$"&apiId={Guid.NewGuid()}").Replace("/metrics?",$"/apis/{s.Api.Id}/metrics?"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.Empty(source.Requests);
    }
    [Fact] public async Task StatusGroupingKeepsRealCountsAndDoesNotInventLatencyByStatus()
    {
        var source=new MetricProtocolHandler();await using var s=await FixtureAsync(source);
        using var response=await s.Client.GetAsync(Url(s,source,suffix:"&groupBy=Status"));response.EnsureSuccessStatusCode();
        var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;var group=Assert.Single(body.Data!.Groups);
        Assert.Equal("200",group.Key);Assert.Equal("HTTP 200",group.Name);Assert.Equal(200,Assert.Single(group.Values,x=>x.Metric=="request_count").Value);Assert.Null(Assert.Single(group.Values,x=>x.Metric=="latency_p95_ms").Value);
    }

}

public sealed class MetricProtocolHandler : HttpMessageHandler
{
    public (string Node, Guid Destination, int Value)[]? HealthSamples { get; set; }
    public Guid? UnhealthyDestinationId,UnhealthyClusterId;public Guid EnvironmentId,ApiId,ApplicationId,DestinationId,ClusterId;
    public Guid? ForeignEnvironmentId,SecondApiId,PolicyId;
    public string DefaultNodeName = "node-0";
    public (Guid EnvironmentId, int AgeSeconds)? PeerHeartbeat;
    public bool MissingSecondNode,ResetWindow,MiddleGap,NoDestination;
    public int Status=200;
    public DateTimeOffset End {get;set;}=DateTimeOffset.UtcNow.AddSeconds(-1);
    public int WindowHours = 1;
    public Guid[] RangeApplications = [];
    public Func<CancellationToken, Task>? BeforeLatencyRange;
    public DateTimeOffset Start=>End.AddHours(-WindowHours);
    public ConcurrentQueue<Uri> Requests {get;}=new();
    private HttpResponseMessage ManyApplications(string query, bool histogram)
    {
        var apps = RangeApplications.Where(id => !query.Contains("webapi_application_id=~", StringComparison.Ordinal)
            || query.Contains(id.ToString(), StringComparison.Ordinal)).ToArray();
        var groupsByApp = query[..query.IndexOf(')')].Contains("webapi_application_id", StringComparison.Ordinal);
        const int responseLimit = 8 * 1024 * 1024;
        if (histogram && groupsByApp && apps.Length * 14 * 599 * 20 > responseLimit)
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[responseLimit + 1]) };

        var points = new[] { Start.AddSeconds(30).ToUnixTimeSeconds(), End.AddSeconds(-1).ToUnixTimeSeconds() };
        var rows = new List<object>();
        var labels = new Dictionary<string, string> { ["webapi_environment_id"] = EnvironmentId.ToString() };
        if (histogram)
        {
            foreach (var boundary in new[] { "0.1", "+Inf" })
                Add(new(labels) { ["le"] = boundary });
        }
        else
        {
            labels["webapi_success"] = "true";
            labels["http_response_status_code"] = "200";
            Add(labels);
        }
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
        {
            status = "success",
            data = new { resultType = "matrix", result = rows }
        }) };

        void Add(Dictionary<string, string> metric)
        {
            if (apps.Length != 0)
                rows.Add(new { metric, values = points.Select(point => new object[] { point, apps.Length.ToString(CultureInfo.InvariantCulture) }) });
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Requests.Enqueue(request.RequestUri!);var query=QueryHelpers.ParseQuery(request.RequestUri!.Query)["query"].ToString();
        var range = request.RequestUri.AbsolutePath.EndsWith("query_range", StringComparison.Ordinal);
        if (range && BeforeLatencyRange is not null && query.Contains("webapi_gateway_request_duration_seconds_bucket", StringComparison.Ordinal))
            await BeforeLatencyRange(ct);
        if (RangeApplications.Length != 0 && range)
        {
            var histogram = query.Contains("webapi_gateway_request_duration_seconds_bucket", StringComparison.Ordinal);
            if (histogram || query.Contains("webapi_gateway_requests_total", StringComparison.Ordinal))
                return ManyApplications(query, histogram);
        }
        var rows=new List<object>();Dictionary<string,string> Labels(string? node=null)=>new(){["webapi_environment_id"]=EnvironmentId.ToString(),["service_instance_id"]=node??DefaultNodeName,["webapi_api_id"]=ApiId.ToString(),["webapi_application_id"]=ApplicationId.ToString(),["webapi_destination_id"]=NoDestination?"None":DestinationId.ToString(),["webapi_cluster_id"]=ClusterId.ToString(),["http_response_status_code"]="200",["webapi_outcome"]="Completed",["webapi_success"]="true"};
        var stamp=End.ToUnixTimeMilliseconds()/1000d;
        void Add(Dictionary<string,string> labels,double value)=>rows.Add(new{metric=labels,value=new object[]{stamp,value.ToString(CultureInfo.InvariantCulture)}});
        if(query.Contains("otelcol_",StringComparison.Ordinal))Add(new(),0);
        else if(query.Contains("last_loss_timestamp",StringComparison.Ordinal))Add(Labels(),0);
        else if(query.Contains("last_observed_timestamp",StringComparison.Ordinal))
        {
            var time=query.Contains("min_over_time",StringComparison.Ordinal)?Start.AddSeconds(5):End.AddSeconds(-2);var value=query.Contains("max_over_time",StringComparison.Ordinal)?MiddleGap?90:5:time.ToUnixTimeMilliseconds()/1000d;Add(Labels(),value);if(!MissingSecondNode)Add(Labels("node-1"),value);
            if (PeerHeartbeat is { } peer)
            {
                var labels = Labels();
                labels["webapi_environment_id"] = peer.EnvironmentId.ToString();
                Add(labels, query.StartsWith("webapi_telemetry_last_observed_timestamp_seconds", StringComparison.Ordinal)
                    ? stamp - peer.AgeSeconds : value);
            }
        }
        else if(query.Contains("webapi_gateway_policy_decisions_total",StringComparison.Ordinal))
        {if(PolicyId is Guid policy){Add(new(){["webapi_environment_id"]=EnvironmentId.ToString(),["webapi_policy_id"]=policy.ToString(),["webapi_policy_type"]="rate_limit",["webapi_policy_decision"]="Bypass"},7);Add(new(){["webapi_environment_id"]=EnvironmentId.ToString(),["webapi_policy_id"]=policy.ToString(),["webapi_policy_type"]="circuit_breaker",["webapi_policy_decision"]="OpenRejected"},3);}}
        else if(query.Contains("_bucket",StringComparison.Ordinal))
        {foreach(var node in new[]{"node-0","node-1"})foreach(var le in new[]{"0.1","1","10","+Inf"}){var labels=Labels(node);labels["le"]=le;Add(labels,SecondApiId is null?node=="node-0"||le is "10" or "+Inf"?100:0:le is "10" or "+Inf"?100:0);if(SecondApiId is Guid second){var fast=Labels(node);fast["webapi_api_id"]=second.ToString();fast["le"]=le;Add(fast,100);}}}
        else if(query.Contains("webapi_destination_health",StringComparison.Ordinal))
        {
            if (HealthSamples is { } samples)
            {
                foreach (var (node, destination, value) in samples)
                {
                    var labels = Labels(node);
                    labels["webapi_destination_id"] = destination.ToString();
                    Add(labels, value);
                }
            }
            else {Add(Labels(),1);if(UnhealthyDestinationId is Guid bad){var labels=Labels();labels["webapi_destination_id"]=bad.ToString();labels["webapi_cluster_id"]=UnhealthyClusterId.ToString()!;Add(labels,0);}}
        }
        else if(query.Contains("dropped_total",StringComparison.Ordinal)||query.Contains("export_failures_total",StringComparison.Ordinal)){}
        else if(query.Contains("webapi_gateway_requests_total",StringComparison.Ordinal)){Add(Labels(),ResetWindow?6:200);if(SecondApiId is Guid second){var fast=Labels();fast["webapi_api_id"]=second.ToString();Add(fast,400);}if(ForeignEnvironmentId is Guid foreign){var labels=Labels();labels["webapi_environment_id"]=foreign.ToString();Add(labels,999999);}}
        if(request.RequestUri.AbsolutePath.EndsWith("query_range",StringComparison.Ordinal))
        {var matrix=rows.Select(row=>JsonSerializer.SerializeToElement(row)).Select(row=>new{metric=row.GetProperty("metric"),values=new[]{new object[]{Start.AddSeconds(30).ToUnixTimeMilliseconds()/1000d,"0.5"},new object[]{End.AddSeconds(-1).ToUnixTimeMilliseconds()/1000d,"1"}}});return new HttpResponseMessage((HttpStatusCode)Status){Content=JsonContent.Create(new{status="success",data=new{resultType="matrix",result=matrix}})};}
        return new HttpResponseMessage((HttpStatusCode)Status){Content=JsonContent.Create(new{status="success",data=new{resultType="vector",result=rows}})};
    }
}

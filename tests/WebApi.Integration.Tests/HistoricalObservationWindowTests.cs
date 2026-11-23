using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Observability;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
using System.Net;

namespace WebApi.Integration.Tests;

public sealed class HistoricalObservationWindowTests
{
    private static string Window(ApiFixture f, DateTimeOffset start, DateTimeOffset end)
        => $"?organizationId={f.Organization.Id}&projectId={f.Project.Id}&environmentId={f.Environment.Id}&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}";

    private sealed class FrozenRowHandler(LogProtocolHandler source) : DelegatingHandler(source)
    {
        public long Version, Sequence;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            if(request.RequestUri!.Query.Contains("otelcol_")) return response;
            if (!request.RequestUri.AbsolutePath.Contains("/loki/"))
            {
                var rows=json["data"]!["result"]!.AsArray();
                var first=rows[0]!;
                first["metric"]!["service_instance_id"]="node-0";
                var other=first.DeepClone();
                other["metric"]!["service_instance_id"]="node-1";
                rows.Add(other);
                response.Content=new StringContent(json.ToJsonString(),Encoding.UTF8,"application/json");
                return response;
            }
            foreach(var stream in json["data"]!["result"]!.AsArray())
                foreach(var value in stream!["values"]!.AsArray()) {
                    value![2]!["webapi_config_version"] = Version.ToString(CultureInfo.InvariantCulture);
                    value[2]!["webapi_deployment_sequence"] = Sequence.ToString(CultureInfo.InvariantCulture);
                }
            response.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }
    }

    [Fact]
    public async Task OneStoredLogAcrossFormalRemoval()
    {
        var logs = new LogProtocolHandler();
        var source = new FrozenRowHandler(logs);
        await using var scenario = new DeploymentScenario();
        await scenario.InitializeAsync(b => {
            b.Configuration["Observability:Enabled"]="true";
            b.Services.PostConfigure<HttpClientFactoryOptions>("observability", o => o.HttpMessageHandlerBuilderActions.Add(chain=>chain.PrimaryHandler=source));
        });
        var f=scenario.Api;
        await ObservationTestSupport.GrantAsync(f,["log.read"]);
        using var create=await f.WriteAsync(HttpMethod.Post,$"/api/v1/clusters/{f.Cluster.Id}/destinations",new SaveDestinationRequest("retiring replica","http://test-backend:8080/replica"));
        create.EnsureSuccessStatusCode();
        var target=(await create.Content.ReadFromJsonAsync<DestinationDto>())!;
        var v1=(await scenario.DesiredAsync(await scenario.PublishAsync())).Envelope;
        source.Version=v1.ConfigVersion;source.Sequence=v1.DeploymentSequence;
        logs.EnvironmentId=f.Environment.Id;logs.ApiId=f.Api.Id;logs.ApplicationId=f.Application.Id;logs.DestinationId=target.Id;
        var when=DateTimeOffset.UtcNow; var start=when.AddMinutes(-1);
        var id=Guid.NewGuid();
        logs.Rows.Add(new(id,(when.UtcDateTime.Ticks-DateTime.UnixEpoch.Ticks)*100L,"GET","/orders","node-0"));
        await Task.Delay(25);
        var before=DateTimeOffset.UtcNow;
        using var delete=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/destinations/{target.Id}",etag:create.Headers.ETag!.Tag);
        delete.EnsureSuccessStatusCode();
        var v2=(await scenario.DesiredAsync(await scenario.PublishAsync())).Envelope;
        var after=DateTimeOffset.UtcNow;
        const string path="/api/v1/observability/logs";
        async Task Check(DateTimeOffset windowStart, DateTimeOffset end, bool expected) {
            var scope=Window(f,windowStart,end);
            using var listing=await f.Client.GetAsync(path+scope);
            listing.EnsureSuccessStatusCode();
            var data=(await listing.Content.ReadFromJsonAsync<ObservationEnvelope<CursorPage<AccessLogDto>>>())!;
            using var detail=await f.Client.GetAsync(path+"/"+id+scope);
            using var filtered=await f.Client.GetAsync(path+scope+"&destinationId="+target.Id);
            using var csv=await f.Client.GetAsync(path+"/export"+scope);
            Assert.Equal(expected ? 1 : 0, data.Data!.Items.Count);
            Assert.Equal(expected, data.Data.Items.Any(e => e.Id == id));
            Assert.Equal(expected ? HttpStatusCode.OK : HttpStatusCode.NotFound, detail.StatusCode);
            Assert.Equal(expected ? HttpStatusCode.OK : HttpStatusCode.NotFound, filtered.StatusCode);
            Assert.Equal(expected ? "1" : "0", csv.Headers.GetValues("X-WebApi-Export-Rows").Single());
        }
        await Check(start, before, true);
        await Check(start, after, true);
        await Check(after, DateTimeOffset.UtcNow, false);
        await using var db = f.Context();
        await db.Set<UserProjectScope>().Where(g => g.UserId == f.User.Id).ExecuteDeleteAsync();
        using var revoked = await f.Client.GetAsync(path + "/" + id + Window(f, start, after));
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [Fact]
    public async Task RequestCountAcrossFormalRemoval()
    {
        var source=new MetricProtocolHandler();
        await using var scenario=new DeploymentScenario();
        await scenario.InitializeAsync(b=>{
            b.Configuration["Observability:Enabled"]="true";
            b.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(chain=>chain.PrimaryHandler=source));
        });
        var f=scenario.Api;
        await ObservationTestSupport.GrantAsync(f,["metrics.read"]);
        using var create=await f.WriteAsync(HttpMethod.Post,$"/api/v1/clusters/{f.Cluster.Id}/destinations",new SaveDestinationRequest("retiring replica","http://test-backend:8080/replica"));
        create.EnsureSuccessStatusCode();var target=(await create.Content.ReadFromJsonAsync<DestinationDto>())!;
        var first=await scenario.DesiredAsync(await scenario.PublishAsync());
        source.ApiId=f.Api.Id;source.EnvironmentId=f.Environment.Id;source.ClusterId=f.Cluster.Id;source.ApplicationId=f.Application.Id;source.DestinationId=target.Id;
        var before=DateTimeOffset.UtcNow;var start=before.AddHours(-1);
        using var delete=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/destinations/{target.Id}",etag:create.Headers.ETag!.Tag);delete.EnsureSuccessStatusCode();
        var next=await scenario.DesiredAsync(await scenario.PublishAsync());
        var after=DateTimeOffset.UtcNow;
        async Task Check(DateTimeOffset windowStart, DateTimeOffset end, bool expected) {
            source.End=end;
            var path="/api/v1/observability/metrics"+Window(f,windowStart,end)+"&groupBy=Destination";
            using var response=await f.Client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            var envelope=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<MetricsDto>>())!;
            var data=envelope.Data!;
            Assert.Equal(expected ? 200d : 0d, data.Kpis.Single(m => m.Metric == "request_count").Value);
            Assert.Equal(expected ? 1 : 0, data.Groups.Count);
            if (expected) Assert.Equal(new double?[] { 0.5, 1 }, data.Trends["request_rps"].Select(point=>point.Value));
            else Assert.DoesNotContain(data.Trends["request_rps"], point => point.Value > 0);
            if (end >= after) Assert.DoesNotContain(data.NodeHealth.SelectMany(n => n.Destinations),
                d => d.DestinationId == target.Id && d.Health == "Healthy");
        }
        await Check(start, before, true);
        await Check(start, after, true);
        await Check(after, DateTimeOffset.UtcNow, false);
    }
}

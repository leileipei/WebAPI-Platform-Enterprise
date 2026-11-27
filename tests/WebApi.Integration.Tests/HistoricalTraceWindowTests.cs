using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Observability;
using WebApi.Integration.Tests.Support;
using Xunit;
using System.Net;

namespace WebApi.Integration.Tests;

public sealed class HistoricalTraceWindowTests
{
    private sealed class ControlledTrace(TraceProtocolHandler source) : DelegatingHandler(source)
    {
        public Guid Destination;
        public long At, Version, Sequence;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response=await base.SendAsync(request,ct);
            var uri=request.RequestUri!;
            if(uri.AbsolutePath=="/api/search" || uri.Query.Contains("otelcol_")) return response;
            var json=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            if(uri.AbsolutePath.StartsWith("/api/traces/",StringComparison.Ordinal))
            {
                var span=json["batches"]![0]!["scopeSpans"]![0]!["spans"]![0]!;
                span["startTimeUnixNano"]=At.ToString(CultureInfo.InvariantCulture);
                span["endTimeUnixNano"]=(At+1000000).ToString(CultureInfo.InvariantCulture);
                void Attribute(string key,string value)=>span["attributes"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{key,value=new{stringValue=value}}));
                Attribute("webapi.destination.id",Destination.ToString());
                Attribute("webapi.config.version",Version.ToString(CultureInfo.InvariantCulture));
                Attribute("webapi.deployment.sequence",Sequence.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                var rows=json["data"]!["result"]!.AsArray();
                var first=rows[0]!;
                first["metric"]!["service_instance_id"]="node-0";
                var second=first.DeepClone(); second["metric"]!["service_instance_id"]="node-1"; rows.Add(second);
            }
            response.Content=new StringContent(json.ToJsonString(),Encoding.UTF8,"application/json");
            return response;
        }
    }

    [Fact]
    public async Task TraceAcrossFormalRemoval()
    {
        var provider=new TraceProtocolHandler();
        var transport=new ControlledTrace(provider);
        await using var scenario=new DeploymentScenario();
        await scenario.InitializeAsync(b=>{
            b.Configuration["Observability:Enabled"]="true";
            b.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(chain=>chain.PrimaryHandler=transport));
        });
        var f=scenario.Api;
        await ObservationTestSupport.GrantAsync(f,["trace.read"]);
        using var create=await f.WriteAsync(HttpMethod.Post,$"/api/v1/clusters/{f.Cluster.Id}/destinations",new SaveDestinationRequest("retiring replica","http://test-backend:8080/replica"));
        create.EnsureSuccessStatusCode();
        var destination=(await create.Content.ReadFromJsonAsync<DestinationDto>())!;
        var earlyVersion=(await scenario.DesiredAsync(await scenario.PublishAsync())).Envelope;
        provider.EnvironmentId=f.Environment.Id;provider.ApiId=f.Api.Id;provider.ApplicationLabel=f.Application.Id.ToString();
        transport.Destination=destination.Id;transport.Version=earlyVersion.ConfigVersion;transport.Sequence=earlyVersion.DeploymentSequence;
        var occurred=DateTimeOffset.UtcNow;transport.At=(occurred.UtcTicks-DateTimeOffset.UnixEpoch.UtcTicks)*100;
        var start=occurred.AddMinutes(-1);
        await Task.Delay(25);
        var before=DateTimeOffset.UtcNow;
        using var delete=await f.WriteAsync(HttpMethod.Delete,$"/api/v1/destinations/{destination.Id}",etag:create.Headers.ETag!.Tag);
        delete.EnsureSuccessStatusCode();var laterVersion=(await scenario.DesiredAsync(await scenario.PublishAsync())).Envelope;
        var after=DateTimeOffset.UtcNow;
        async Task Check(DateTimeOffset windowStart, DateTimeOffset end, bool expected)
        {
            var query=$"?organizationId={f.Organization.Id}&projectId={f.Project.Id}&environmentId={f.Environment.Id}&start={Uri.EscapeDataString(windowStart.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}";
            const string prefix="/api/v1/observability/traces";
            using var list=await f.Client.GetAsync(prefix+query);
            list.EnsureSuccessStatusCode();
            var page=(await list.Content.ReadFromJsonAsync<ObservationEnvelope<CursorPage<TraceSummaryDto>>>())!;
            using var detail=await f.Client.GetAsync(prefix+"/"+provider.TraceId+query);
            Assert.Equal(expected ? 1 : 0, page.Data!.Items.Count);
            Assert.Equal(expected, page.Data.Items.Any(x => x.TraceId == provider.TraceId));
            Assert.Equal(expected ? HttpStatusCode.OK : HttpStatusCode.NotFound, detail.StatusCode);
        }
        await Check(start, before, true);
        await Check(start, after, true);
        await Check(after, DateTimeOffset.UtcNow, false);
    }
}

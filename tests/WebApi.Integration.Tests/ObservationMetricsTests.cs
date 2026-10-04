using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using WebApi.Contracts.Observability;
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
    public Guid EnvironmentId,ApiId,ApplicationId,DestinationId,ClusterId;
    public Guid? ForeignEnvironmentId,SecondApiId;
    public bool MissingSecondNode,ResetWindow,MiddleGap;
    public int Status=200;
    public DateTimeOffset End {get;set;}=DateTimeOffset.UtcNow.AddSeconds(-1);
    public DateTimeOffset Start=>End.AddHours(-1);
    public ConcurrentQueue<Uri> Requests {get;}=new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Requests.Enqueue(request.RequestUri!);var query=QueryHelpers.ParseQuery(request.RequestUri!.Query)["query"].ToString();
        var rows=new List<object>();Dictionary<string,string> Labels(string? node=null)=>new(){["webapi_environment_id"]=EnvironmentId.ToString(),["service_instance_id"]=node??"node-0",["webapi_api_id"]=ApiId.ToString(),["webapi_application_id"]=ApplicationId.ToString(),["webapi_destination_id"]=DestinationId.ToString(),["webapi_cluster_id"]=ClusterId.ToString(),["http_response_status_code"]="200",["webapi_outcome"]="Completed",["webapi_success"]="true"};
        var stamp=End.ToUnixTimeMilliseconds()/1000d;
        void Add(Dictionary<string,string> labels,double value)=>rows.Add(new{metric=labels,value=new object[]{stamp,value.ToString(CultureInfo.InvariantCulture)}});
        if(query.Contains("last_observed_timestamp",StringComparison.Ordinal))
        {var time=query.Contains("min_over_time",StringComparison.Ordinal)?Start.AddSeconds(5):End.AddSeconds(-2);var value=query.Contains("max_over_time",StringComparison.Ordinal)?MiddleGap?90:5:time.ToUnixTimeMilliseconds()/1000d;Add(Labels(),value);if(!MissingSecondNode)Add(Labels("node-1"),value);}
        else if(query.Contains("_bucket",StringComparison.Ordinal))
        {foreach(var node in new[]{"node-0","node-1"})foreach(var le in new[]{"0.1","1","10","+Inf"}){var labels=Labels(node);labels["le"]=le;Add(labels,SecondApiId is null?node=="node-0"||le is "10" or "+Inf"?100:0:le is "10" or "+Inf"?100:0);if(SecondApiId is Guid second){var fast=Labels(node);fast["webapi_api_id"]=second.ToString();fast["le"]=le;Add(fast,100);}}}
        else if(query.Contains("webapi_destination_health",StringComparison.Ordinal))Add(Labels(),1);
        else if(query.Contains("dropped_total",StringComparison.Ordinal)||query.Contains("export_failures_total",StringComparison.Ordinal)){}
        else if(query.Contains("webapi_gateway_requests_total",StringComparison.Ordinal)){Add(Labels(),ResetWindow?6:200);if(SecondApiId is Guid second){var fast=Labels();fast["webapi_api_id"]=second.ToString();Add(fast,400);}if(ForeignEnvironmentId is Guid foreign){var labels=Labels();labels["webapi_environment_id"]=foreign.ToString();Add(labels,999999);}}
        if(request.RequestUri.AbsolutePath.EndsWith("query_range",StringComparison.Ordinal))
        {var matrix=rows.Select(row=>JsonSerializer.SerializeToElement(row)).Select(row=>new{metric=row.GetProperty("metric"),values=new[]{new object[]{Start.AddSeconds(30).ToUnixTimeMilliseconds()/1000d,"0.5"},new object[]{End.AddSeconds(-1).ToUnixTimeMilliseconds()/1000d,"1"}}});return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status){Content=JsonContent.Create(new{status="success",data=new{resultType="matrix",result=matrix}})});}
        return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status){Content=JsonContent.Create(new{status="success",data=new{resultType="vector",result=rows}})});
    }
}

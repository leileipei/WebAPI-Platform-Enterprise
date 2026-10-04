using System.Collections.Concurrent;using System.Globalization;using System.Net;using System.Net.Http.Json;using System.Text.Json;using Microsoft.AspNetCore.WebUtilities;using Microsoft.EntityFrameworkCore;using Microsoft.Extensions.DependencyInjection;using Microsoft.Extensions.Http;using WebApi.Contracts.Observability;using WebApi.Infrastructure.Persistence.Entities;using WebApi.Integration.Tests.Support;using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ObservationTraceTests
{
    private static async Task<(ApiFixture Api,TraceProtocolHandler Source)> FixtureAsync(string permission="trace.read")
    {
        var f=new ApiFixture();var source=new TraceProtocolHandler();await f.InitializeAsync(b=>{b.Configuration["Observability:Enabled"]="true";b.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(builder=>builder.PrimaryHandler=source));});await f.SeedConsumerAsync();await ObservationTestSupport.GrantAsync(f,[permission]);
        await using(var db=f.Context()){db.Add(new GatewayNode{EnvironmentId=f.Environment.Id,NodeName="trace-node"});await db.SaveChangesAsync();}using var login=await f.LoginAsync();login.EnsureSuccessStatusCode();source.EnvironmentId=f.Environment.Id;source.ApiId=f.Api.Id;return(f,source);
    }
    private static string Url(ApiFixture f,TraceProtocolHandler source,string? trace=null)=>$"/api/v1/observability/traces{(trace is null?"":"/"+trace)}?organizationId={f.Organization.Id}&projectId={f.Project.Id}&environmentId={f.Environment.Id}&start={Uri.EscapeDataString(source.Start.ToString("O"))}&end={Uri.EscapeDataString(source.End.ToString("O"))}";
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FirstLossAndCollectorDownstreamFailureCannotClaimComplete(bool collector)
    {
        var(f,source)=await FixtureAsync();await using var fixture=f;source.FirstLoss=!collector;source.CollectorLoss=collector;
        using var r=await f.Client.GetAsync(Url(f,source));r.EnsureSuccessStatusCode();var body=(await r.Content.ReadFromJsonAsync<ObservationEnvelope<CursorPage<TraceSummaryDto>>>())!;
        Assert.False(body.Coverage.Complete);Assert.Equal(SourceState.Partial,body.SourceState);Assert.Equal(collector?"collector_traces_collection_gap":"known_traces_collection_gap",body.Coverage.Reason);
    }
    [Fact] public async Task MiddleGapCannotClaimCompleteTraceCoverage()
    {
        var(f,source)=await FixtureAsync();await using var fixture=f;source.MiddleGap=true;
        using var r=await f.Client.GetAsync(Url(f,source,source.TraceId));r.EnsureSuccessStatusCode();
        var body=(await r.Content.ReadFromJsonAsync<ObservationEnvelope<TraceDetailDto>>())!;
        Assert.False(body.Coverage.Complete);Assert.Equal("time_window_coverage_incomplete",body.Coverage.Reason);
    }
    [Fact]public async Task SameTraceIdAcrossEnvironmentsDoesNotLeak()
    {
        var(f,source)=await FixtureAsync();await using var fixture=f;source.ForeignEnvironmentId=Guid.NewGuid();using var r=await f.Client.GetAsync(Url(f,source,source.TraceId));r.EnsureSuccessStatusCode();var json=await r.Content.ReadAsStringAsync();var detail=JsonSerializer.Deserialize<ObservationEnvelope<TraceDetailDto>>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Data!;
        Assert.Single(detail.Spans);Assert.True(detail.PartialTrace);Assert.DoesNotContain("foreign-private-marker",json);Assert.DoesNotContain("url.full",json);Assert.DoesNotContain("http.request.header.authorization",json);Assert.Equal("2222222222222222",detail.Spans[0].SpanId);
        using var search=await f.Client.GetAsync(Url(f,source));search.EnsureSuccessStatusCode();var page=(await search.Content.ReadFromJsonAsync<ObservationEnvelope<CursorPage<TraceSummaryDto>>>())!;Assert.Single(page.Data!.Items);Assert.DoesNotContain("foreign-private-marker",JsonSerializer.Serialize(page));Assert.True(page.Data.Items[0].DurationMs<1000);
    }
    [Fact]public async Task MissingScopeSpanOmittedAndPartialMarked()
    {
        var(f,source)=await FixtureAsync();await using var fixture=f;source.Unscoped=true;using var response=await f.Client.GetAsync(Url(f,source,source.TraceId));response.EnsureSuccessStatusCode();var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<TraceDetailDto>>())!;
        Assert.Single(body.Data!.Spans);Assert.True(body.Data.PartialTrace);Assert.DoesNotContain("unscoped-private-marker",JsonSerializer.Serialize(body));
    }
    [Fact]public async Task MetricsPermissionDoesNotGrantTrace()
    {
        var(f,source)=await FixtureAsync("metrics.read");await using var fixture=f;using var response=await f.Client.GetAsync(Url(f,source,source.TraceId));Assert.Equal(HttpStatusCode.Forbidden,response.StatusCode);Assert.Empty(source.Requests);
    }
    [Fact]public async Task UnsampledNotFoundDiffersFromSourceFailure()
    {
        var(f,source)=await FixtureAsync();await using var fixture=f;source.Status=404;using(var absent=await f.Client.GetAsync(Url(f,source,source.TraceId)))Assert.Equal(HttpStatusCode.NotFound,absent.StatusCode);
        source.Status=503;using var failure=await f.Client.GetAsync(Url(f,source,source.TraceId));Assert.Equal(HttpStatusCode.ServiceUnavailable,failure.StatusCode);var body=await failure.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("traces",body.GetProperty("sourceType").GetString());
    }
    [Fact]public async Task InvalidIdAndFilterNeverReachSources()
    {
        var(f,source)=await FixtureAsync();await using var fixture=f;
        foreach(var suffix in new[]{"/00000000000000000000000000000000","/legacy-request-id"}){using var response=await f.Client.GetAsync(Url(f,source,source.TraceId).Replace("/"+source.TraceId,suffix));Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);}Assert.Empty(source.Requests);
    }
    [Fact]public async Task ProviderCapIsExplicitWithoutUnsafeCursor()
    {
        var(f,source)=await FixtureAsync();await using var fixture=f;source.CandidateCount=201;using var response=await f.Client.GetAsync(Url(f,source));response.EnsureSuccessStatusCode();var page=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<CursorPage<TraceSummaryDto>>>())!;
        Assert.True(page.Coverage.Truncated);Assert.True(page.Data!.Truncated);Assert.Null(page.Data.NextCursor);Assert.Equal(SourceState.Partial,page.SourceState);
    }
    [Fact]public async Task MissingTraceCursorKeyReportsTraceSourceFailure()
    {
        var(f,source)=await FixtureAsync();await using var fixture=f;source.CandidateCount=2;using var response=await f.Client.GetAsync(Url(f,source)+"&limit=1");Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var json=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("traces",json.GetProperty("sourceType").GetString());
    }
    [Fact]public async Task SpanTimesRemainReal()
    {
        var(f,source)=await FixtureAsync();await using var fixture=f;source.IncludeClient=true;using var response=await f.Client.GetAsync(Url(f,source,source.TraceId));response.EnsureSuccessStatusCode();var body=(await response.Content.ReadFromJsonAsync<ObservationEnvelope<TraceDetailDto>>())!;
        Assert.Equal(2,body.Data!.Spans.Count);var server=Assert.Single(body.Data.Spans,x=>x.Kind=="Server");var client=Assert.Single(body.Data.Spans,x=>x.Kind=="Client");Assert.Equal(source.SpanStart,server.Start);Assert.Equal(200,server.DurationMs);Assert.Equal(source.SpanStart.AddMilliseconds(20),client.Start);Assert.Equal(140,client.DurationMs);Assert.Equal(server.SpanId,client.ParentSpanId);Assert.Equal(0.1,body.Sampling!.Ratio);Assert.Equal("ParentBasedTraceIdRatioBased",body.Sampling.Mode);
    }
}
public sealed class TraceProtocolHandler:HttpMessageHandler
{
    public Guid EnvironmentId,ApiId;public Guid? ForeignEnvironmentId;public bool Unscoped,IncludeClient,MiddleGap,FirstLoss,CollectorLoss;public int Status=200;public int CandidateCount=1;
    public string TraceId="1234567890abcdef1234567890abcdef";public ConcurrentQueue<Uri> Requests{get;}=new();
    public DateTimeOffset End{get;}=DateTimeOffset.UtcNow.AddSeconds(-1);public DateTimeOffset Start=>End.AddHours(-1);public DateTimeOffset SpanStart=>End.AddSeconds(-5);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var uri=request.RequestUri!;Requests.Enqueue(uri);object Attr(string key,string value)=>new{key,value=new{stringValue=value}};
        long Nano(DateTimeOffset time)=>(time.UtcTicks-DateTimeOffset.UnixEpoch.UtcTicks)*100;
        object Span(string id,string parent,string name,DateTimeOffset start,int duration,int kind)=>new{traceId=uri.AbsolutePath.StartsWith("/api/traces/",StringComparison.Ordinal)?uri.AbsolutePath.Split('/').Last():TraceId,spanId=id,parentSpanId=parent,name,kind,startTimeUnixNano=Nano(start).ToString(CultureInfo.InvariantCulture),endTimeUnixNano=Nano(start.AddMilliseconds(duration)).ToString(CultureInfo.InvariantCulture),status=new{code=1},attributes=new[]{Attr("webapi.api.id",ApiId.ToString()),Attr("webapi.outcome","Completed"),Attr("http.request.method","GET"),Attr("url.template","/orders"),Attr("url.full","https://secret.invalid/?credential=foreign-private-marker"),Attr("http.request.header.authorization","foreign-private-marker")}};
        object Batch(Guid? env,object[] spans)=>new{resource=new{attributes=env is Guid environment?new[]{Attr("webapi.environment.id",environment.ToString()),Attr("service.name","webapi-gateway")}:new[]{Attr("service.name","backend-without-trusted-scope")}},scopeSpans=new[]{new{scope=new{name="owned-test-source"},spans}}};
        if(uri.AbsolutePath.StartsWith("/api/traces/",StringComparison.Ordinal)){
            var rows=new List<object>{Batch(EnvironmentId,new[]{Span("2222222222222222",ForeignEnvironmentId is not null?"1111111111111111":"","gateway.request",SpanStart,200,2)})};
            if(IncludeClient)rows.Add(Batch(EnvironmentId,new[]{Span("3333333333333333","2222222222222222","gateway.proxy",SpanStart.AddMilliseconds(20),140,3)}));
            if(ForeignEnvironmentId is Guid foreign)rows.Add(Batch(foreign,new[]{Span("1111111111111111","","foreign-private-marker",SpanStart.AddSeconds(-10),12000,2)}));
            if(Unscoped)rows.Add(Batch(null,new[]{Span("4444444444444444","2222222222222222","unscoped-private-marker",SpanStart.AddMilliseconds(30),90,3)}));
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status){Content=JsonContent.Create(new{batches=rows})});
        }
        if(uri.AbsolutePath=="/api/search")return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status){Content=JsonContent.Create(new{traces=Enumerable.Range(0,CandidateCount).Select(i=>new{traceID=i==0?TraceId:i.ToString("x32"),rootServiceName="foreign-private-marker",rootTraceName="foreign-private-marker",startTimeUnixNano=Nano(SpanStart).ToString(CultureInfo.InvariantCulture),durationMs=12000}).ToArray()})});
        var p=QueryHelpers.ParseQuery(uri.Query);var q=p["query"].ToString();var time=End.ToUnixTimeMilliseconds()/1000d;var value=q.Contains("otelcol_")?CollectorLoss?1:0:q.Contains("last_loss_timestamp")?FirstLoss?End.AddSeconds(-5).ToUnixTimeMilliseconds()/1000d:0:q.Contains("max_over_time")?MiddleGap?90:5:q.Contains("min_over_time")?Start.AddSeconds(5).ToUnixTimeMilliseconds()/1000d:q.Contains("last_observed_timestamp")?time-2:0;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{status="success",data=new{resultType="vector",result=new[]{new{metric=new Dictionary<string,string>{["webapi_environment_id"]=EnvironmentId.ToString(),["service_instance_id"]="trace-node"},value=new object[]{time,value.ToString(CultureInfo.InvariantCulture)}}}}})});
    }
}
public sealed class TraceProjectionBoundaryTests
{
    [Fact]public void CapturedTempo310ResponseProjectsRealServerClient()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/tempo-3.1.0-synthetic-trace.json")));var root=document.RootElement;var traceId=root.GetProperty("traceId").GetString()!;var env=root.GetProperty("environmentId").GetGuid();var api=root.GetProperty("apiId").GetGuid();var dest=root.GetProperty("destinationId").GetGuid();
        var source=WebApi.Infrastructure.Observability.TempoTraceSource.Parse(root.GetProperty("payload"),traceId);Assert.False(source.Rejected);
        var scope=new WebApi.Infrastructure.Observability.TrustedObservationScope(Guid.NewGuid(),Guid.NewGuid(),[env],[],new Dictionary<Guid,string>{{api,"synthetic-api"}},new Dictionary<Guid,string>(),new Dictionary<Guid,WebApi.Infrastructure.Observability.ObservationDestination>{{dest,new(dest,Guid.NewGuid(),env,"synthetic-destination",true)}});
        var detail=WebApi.Infrastructure.Observability.TraceProjection.Project(traceId,source.Spans,scope);Assert.False(detail.PartialTrace);var server=Assert.Single(detail.Spans,x=>x.Kind=="Server");var client=Assert.Single(detail.Spans,x=>x.Kind=="Client");Assert.Equal(server.SpanId,client.ParentSpanId);Assert.Equal(200,server.DurationMs);Assert.Equal(140,client.DurationMs);Assert.Equal(server.Start.AddMilliseconds(20),client.Start);
    }

    [Fact]public void Base64IdentifiersAndEmptyParentKeepRealSpan()
    {
        var trace="1234567890abcdef1234567890abcdef";var span="2222222222222222";var start=DateTimeOffset.UtcNow.AddSeconds(-2);var nano=(start.UtcTicks-DateTimeOffset.UnixEpoch.UtcTicks)*100;var resource=Guid.NewGuid();
        var json=JsonSerializer.SerializeToElement(new{batches=new[]{new{resource=new{attributes=new[]{new{key="webapi.environment.id",value=new{stringValue=resource.ToString()}}}},scopeSpans=new[]{new{spans=new[]{new{traceId=Convert.ToBase64String(Convert.FromHexString(trace)),spanId=Convert.ToBase64String(Convert.FromHexString(span)),parentSpanId=Convert.ToBase64String(new byte[8]),name="gateway.request",kind="SPAN_KIND_SERVER",startTimeUnixNano=nano.ToString(),endTimeUnixNano=(nano+100_000_000).ToString(),status=new{code="STATUS_CODE_OK"}}}}}}}});
        var batch=WebApi.Infrastructure.Observability.TempoTraceSource.Parse(json,trace);Assert.Single(batch.Spans);Assert.False(batch.Rejected);Assert.Null(batch.Spans[0].ParentSpanId);Assert.Equal(span,batch.Spans[0].SpanId);
    }
}

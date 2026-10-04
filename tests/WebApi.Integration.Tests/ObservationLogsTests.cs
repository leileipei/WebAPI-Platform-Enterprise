using System.Collections.Concurrent;using System.Globalization;using System.Net;using System.Net.Http.Json;using System.Security.Cryptography;using System.Text;using System.Text.Json;using Microsoft.AspNetCore.WebUtilities;using Microsoft.EntityFrameworkCore;using Microsoft.Extensions.DependencyInjection;using Microsoft.Extensions.Http;using WebApi.Contracts.Observability;using WebApi.Infrastructure.Persistence.Entities;using WebApi.Integration.Tests.Support;using Xunit;
namespace WebApi.Integration.Tests;
public sealed class ObservationLogsTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FirstLossAndCollectorDownstreamFailureCannotClaimComplete(bool collector)
    {
        await using var f=await FixtureAsync(2);f.Source.FirstLoss=!collector;f.Source.CollectorLoss=collector;
        using var r=await f.Api.Client.GetAsync(Url(f));r.EnsureSuccessStatusCode();var body=(await r.Content.ReadFromJsonAsync<ObservationEnvelope<CursorPage<AccessLogDto>>>())!;
        Assert.False(body.Coverage.Complete);Assert.Equal(SourceState.Partial,body.SourceState);Assert.Equal(collector?"collector_logs_collection_gap":"known_logs_collection_gap",body.Coverage.Reason);
    }
    [Fact] public async Task MiddleGapCannotClaimCompleteLogCoverage()
    {
        await using var s=await FixtureAsync(2);s.Source.MiddleGap=true;
        var page=await PageAsync(s.Api.Client,Url(s));
        Assert.False(page.Coverage.Complete);Assert.Equal("time_window_coverage_incomplete",page.Coverage.Reason);
    }

    private static async Task<LogFixture> FixtureAsync(int rows=8,bool sameNano=true)
    {
        var s=new LogFixture();File.WriteAllText(s.Secret,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(s.Secret,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        await s.Api.InitializeAsync(b=>{b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:CursorSigningSecretFile"]=s.Secret;b.Configuration["Observability:IpHmacSecretFile"]=s.Secret;
            b.Services.PostConfigure<HttpClientFactoryOptions>("observability",o=>o.HttpMessageHandlerBuilderActions.Add(builder=>builder.PrimaryHandler=s.Source));});
        await s.Api.SeedConsumerAsync();await ObservationTestSupport.GrantAsync(s.Api,["log.read"]);using var login=await s.Api.LoginAsync();login.EnsureSuccessStatusCode();
        await using(var db=s.Api.Context()){db.Add(new GatewayNode{EnvironmentId=s.Api.Environment.Id,NodeName="logs-node"});await db.SaveChangesAsync();}
        s.Source.IpHmac=Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(File.ReadAllText(s.Secret)),Encoding.UTF8.GetBytes("10.0.0.1"))).ToLowerInvariant();s.Source.EnvironmentId=s.Api.Environment.Id;s.Source.ApiId=s.Api.Api.Id;s.Source.ApplicationId=s.Api.Application.Id;s.Source.DestinationId=s.Api.Destination.Id;
        for(var i=0;i<rows;i++)s.Source.Rows.Add(new(new Guid(i+1,0,0,new byte[8]),s.Source.Nano-(sameNano?0:i*1000),"GET","/orders","logs-node"));return s;
    }
    private static string Url(LogFixture f,int limit=2,string? cursor=null,Guid? environment=null)=>$"/api/v1/observability/logs?organizationId={f.Api.Organization.Id}&projectId={f.Api.Project.Id}&environmentId={environment??f.Api.Environment.Id}&start={Uri.EscapeDataString(f.Source.Start.ToString("O"))}&end={Uri.EscapeDataString(f.Source.End.ToString("O"))}&limit={limit}"+(cursor is null?"":"&cursor="+Uri.EscapeDataString(cursor));
    private static async Task<ObservationEnvelope<CursorPage<AccessLogDto>>> PageAsync(HttpClient client,string url){using var r=await client.GetAsync(url);r.EnsureSuccessStatusCode();return (await r.Content.ReadFromJsonAsync<ObservationEnvelope<CursorPage<AccessLogDto>>>())!;}
    [Fact]public async Task EqualNanosecondRowsCrossBoundaryWithoutLoss()
    {
        await using var s=await FixtureAsync();s.Source.Rows.Add(s.Source.Rows[0]);var ids=new List<Guid>();string? cursor=null;var pages=0;
        do{var page=await PageAsync(s.Api.Client,Url(s,cursor:cursor));Assert.False(page.Coverage.Truncated);ids.AddRange(page.Data!.Items.Select(x=>x.Id));cursor=page.Data.NextCursor;Assert.True(++pages<=8);}while(cursor is not null);
        Assert.Equal(s.Source.Rows.Select(x=>x.Id).Distinct().OrderDescending(),ids);Assert.Equal(8,ids.Distinct().Count());Assert.True(s.Source.LogRequests.Count>1);
    }
    [Fact]public async Task ProviderCapReturnsExplicitTruncated()
    {
        await using var s=await FixtureAsync(5001);var page=await PageAsync(s.Api.Client,Url(s));Assert.True(page.Coverage.Truncated);Assert.True(page.Data!.Truncated);Assert.Null(page.Data.NextCursor);
    }
    [Fact]public async Task CursorCannotChangeEnvironmentOrUser()
    {
        await using var s=await FixtureAsync();var page=await PageAsync(s.Api.Client,Url(s));Assert.NotNull(page.Data!.NextCursor);var sibling=new EnvironmentRecord{ProjectId=s.Api.Project.Id,Code="SIBLING",Name="同组织第二环境"};
        await using(var db=s.Api.Context()){db.Add(sibling);await db.SaveChangesAsync();}
        using var foreign=await s.Api.Client.GetAsync(Url(s,cursor:page.Data.NextCursor,environment:sibling.Id));Assert.Equal(HttpStatusCode.UnprocessableEntity,foreign.StatusCode);
        var other=await s.Api.NewReviewerAsync("ProjectAdmin");using var actorChanged=await other.Client.GetAsync(Url(s,cursor:page.Data.NextCursor));Assert.Equal(HttpStatusCode.UnprocessableEntity,actorChanged.StatusCode);
        using var rangeChanged=await s.Api.Client.GetAsync(Url(s,cursor:page.Data.NextCursor).Replace("limit=2","limit=3"));Assert.Equal(HttpStatusCode.UnprocessableEntity,rangeChanged.StatusCode);
    }
    [Fact]public async Task IpFilterIsHmacAndNeverStoredRaw()
    {
        await using var s=await FixtureAsync();var filter=new LogFilter(Ip:"10.0.0.1",Limit:2);using var response=await s.Api.WriteAsync(HttpMethod.Post,"/api/v1/observability/logs/query",new{scope=new ObservationScopeRequest(s.Api.Organization.Id,s.Api.Project.Id,s.Api.Environment.Id,false),range=new TimeRange(s.Source.Start,s.Source.End),filter});response.EnsureSuccessStatusCode();
        var json=await response.Content.ReadAsStringAsync();Assert.Equal(2,JsonSerializer.Deserialize<ObservationEnvelope<CursorPage<AccessLogDto>>>(json,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Data!.Items.Count);Assert.DoesNotContain("10.0.0.1",json);var query=Uri.UnescapeDataString(Assert.Single(s.Source.LogRequests).Query);Assert.DoesNotContain("10.0.0.1",query);
        var hash=Convert.ToHexString(HMACSHA256.HashData(Convert.FromBase64String(File.ReadAllText(s.Secret)),Encoding.UTF8.GetBytes("10.0.0.1"))).ToLowerInvariant();Assert.Contains(hash,query);
    }
    [Fact]public async Task FlattenedLokiMetadataKeepsNanosecondBoundariesAndNoRawFields()
    {
        await using var s=await FixtureAsync(4);s.Source.Flattened=true;s.Source.Rows.Add(new(Guid.NewGuid(),s.Source.Nano+1,"GET","/orders","logs-node"));var ids=new List<Guid>();string? cursor=null;
        do{var page=await PageAsync(s.Api.Client,Url(s,1,cursor));ids.AddRange(page.Data!.Items.Select(x=>x.Id));cursor=page.Data.NextCursor;Assert.True(ids.Count<=5);}while(cursor is not null);
        Assert.Equal(5,ids.Count);Assert.Equal(s.Source.Rows.OrderByDescending(x=>x.Nano).ThenByDescending(x=>x.Id).Select(x=>x.Id),ids);
    }
    [Fact]public async Task RawIpQueryStringIsRejectedInsteadOfIgnoringSensitiveFilter()
    {
        await using var s=await FixtureAsync();using var response=await s.Api.Client.GetAsync(Url(s)+"&ip=10.0.0.1");Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.Empty(s.Source.LogRequests);
    }
    [Fact]public async Task ExpiredAndTamperedCursorCannotBeUsed()
    {
        await using var s=await FixtureAsync();var page=await PageAsync(s.Api.Client,Url(s));var cursor=page.Data!.NextCursor!;
        using var tampered=await s.Api.Client.GetAsync(Url(s,cursor:cursor[..^3]+"bad"));Assert.Equal(HttpStatusCode.UnprocessableEntity,tampered.StatusCode);
        var parts=cursor.Split('.');byte[] Decode(string value){value=value.Replace('-','+').Replace('_','/');return Convert.FromBase64String(value+new string('=',(4-value.Length%4)%4));}
        string Encode(byte[] value)=>Convert.ToBase64String(value).TrimEnd('=').Replace('+','-').Replace('/','_');
        var payload=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(Decode(parts[0]))!;payload["Expires"]=JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds());var bytes=JsonSerializer.SerializeToUtf8Bytes(payload);var expired=Encode(bytes)+"."+Encode(HMACSHA256.HashData(Convert.FromBase64String(File.ReadAllText(s.Secret)),bytes));
        using var response=await s.Api.Client.GetAsync(Url(s,cursor:expired));Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
    }
    [Fact]public async Task SourceFailureProvidesOnlySafeSourceTypeAndRetryHint()
    {
        await using var s=await FixtureAsync();s.Source.Status=503;using var response=await s.Api.Client.GetAsync(Url(s));Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);
        var body=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("logs",body.GetProperty("sourceType").GetString());Assert.Equal("5",response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture));Assert.DoesNotContain("provider-fixture-secret",body.GetRawText());
    }
    [Fact]public async Task UnrepresentableNanosecondRangeReturnsValidationError()
    {
        await using var s=await FixtureAsync();var url=Url(s).Replace(Uri.EscapeDataString(s.Source.Start.ToString("O")),Uri.EscapeDataString(DateTimeOffset.MinValue.ToString("O"))).Replace(Uri.EscapeDataString(s.Source.End.ToString("O")),Uri.EscapeDataString(DateTimeOffset.MinValue.AddHours(1).ToString("O")));
        using var response=await s.Api.Client.GetAsync(url);Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);Assert.Empty(s.Source.LogRequests);
    }
    [Fact]public async Task LargeEqualTimeBoundaryIsExplicitlyTruncatedBeforeCursorOverflows()
    {
        await using var s=await FixtureAsync(130);var first=await PageAsync(s.Api.Client,Url(s,50));Assert.NotNull(first.Data!.NextCursor);var second=await PageAsync(s.Api.Client,Url(s,50,first.Data.NextCursor));Assert.Equal(50,second.Data!.Items.Count);Assert.True(second.Coverage.Truncated);Assert.Null(second.Data.NextCursor);
    }
    [Fact]public async Task DestinationFilterCannotIgnoreUnknownOrMismatchedResources()
    {
        await using var s=await FixtureAsync();using var unknown=await s.Api.Client.GetAsync(Url(s)+"&destinationId="+Guid.NewGuid());Assert.Equal(HttpStatusCode.NotFound,unknown.StatusCode);Assert.Empty(s.Source.LogRequests);
        var second=new UpstreamDestination{ClusterId=s.Api.Cluster.Id,Name="第二目标",Address="http://test-backend:8080/",Weight=1};await using(var db=s.Api.Context()){db.Add(second);await db.SaveChangesAsync();}
        var page=await PageAsync(s.Api.Client,Url(s)+"&destinationId="+second.Id);Assert.Empty(page.Data!.Items);Assert.All(s.Source.LogRequests,u=>Assert.Contains(second.Id.ToString(),Uri.UnescapeDataString(u.Query)));
    }
    [Fact]public async Task CsvFormulaIsNeutralized()
    {
        await using var s=await FixtureAsync(1);s.Source.Rows[0]=s.Source.Rows[0] with{Node="=HYPERLINK(\"bad\")"};using var response=await s.Api.Client.GetAsync(Url(s,50).Replace("/logs?","/logs/export?"));response.EnsureSuccessStatusCode();
        var csv=await response.Content.ReadAsStringAsync();Assert.Contains("'=HYPERLINK",csv);Assert.Equal("1",response.Headers.GetValues("X-WebApi-Export-Rows").Single());Assert.Equal("false",response.Headers.GetValues("X-WebApi-Export-Truncated").Single());Assert.Equal("Available",response.Headers.GetValues("X-WebApi-Source-State").Single());Assert.Contains("text/csv",response.Content.Headers.ContentType!.ToString());
    }
    [Fact]public async Task RevocationStopsExportPages()
    {
        await using var s=await FixtureAsync(201,false);s.Source.OnLogRequest=async call=>{if(call==2){await using var db=s.Api.Context();db.RemoveRange(await db.Set<UserProjectScope>().ToArrayAsync());await db.SaveChangesAsync();}};
        using var response=await s.Api.Client.GetAsync(Url(s,50).Replace("/logs?","/logs/export?"));Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);Assert.False(response.Headers.Contains("X-WebApi-Export-Rows"));Assert.NotEqual("text/csv",response.Content.Headers.ContentType?.MediaType);Assert.Equal(2,s.Source.LogRequests.Count);
    }
}
public sealed class LogFixture:IAsyncDisposable
{
    public ApiFixture Api{get;}=new();public LogProtocolHandler Source{get;}=new();public string Secret{get;}=Path.Combine(Path.GetTempPath(),"webapi-log-secret-"+Guid.NewGuid().ToString("N"));
    public async ValueTask DisposeAsync(){await Api.DisposeAsync();File.Delete(Secret);}
}
public sealed record TestLog(Guid Id,long Nano,string Method,string Path,string Node);
public sealed class LogProtocolHandler:HttpMessageHandler
{
    public Guid EnvironmentId,ApiId,ApplicationId,DestinationId;public bool Flattened,MiddleGap,FirstLoss,CollectorLoss;public string IpHmac="";public int Status=200;public List<TestLog> Rows{get;}=[];public ConcurrentQueue<Uri> LogRequests{get;}=new();public Func<int,Task>? OnLogRequest;
    public DateTimeOffset End{get;}=DateTimeOffset.UtcNow.AddSeconds(-1);public DateTimeOffset Start=>End.AddHours(-1);public long Nano=DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds()*1000000+47;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var uri=request.RequestUri!;var p=QueryHelpers.ParseQuery(uri.Query);
        if(uri.AbsolutePath.Contains("/loki/")){
            LogRequests.Enqueue(uri);if(Status!=200)return new((HttpStatusCode)Status){Content=JsonContent.Create(new{error="provider-fixture-secret"})};if(OnLogRequest is not null)await OnLogRequest(LogRequests.Count);
            var max=long.Parse(p["end"].ToString(),CultureInfo.InvariantCulture);var min=long.Parse(p["start"].ToString(),CultureInfo.InvariantCulture);var limit=int.Parse(p["limit"].ToString(),CultureInfo.InvariantCulture);
            var values=Rows.Where(x=>x.Nano>=min&&x.Nano<max).OrderByDescending(x=>x.Nano).ThenBy(x=>x.Id).Take(limit).Select(row=>new object[]{row.Nano.ToString(CultureInfo.InvariantCulture),"gateway.request",new Dictionary<string,string>{["webapi_log_id"]=row.Id.ToString(),["webapi_api_id"]=ApiId.ToString(),["webapi_application_id"]=ApplicationId.ToString(),["http_request_method"]=row.Method,["url_template"]=row.Path,["http_response_status_code"]="200",["webapi_duration_ms"]="12.5",["webapi_outcome"]="Completed",["webapi_request_id"]="legacy-request-id",["trace_id"]="0123456789abcdef0123456789abcdef",["webapi_client_ip_masked"]="10.0.0.xxx",["webapi_client_ip_hmac"]=IpHmac,["webapi_node_name"]=row.Node,["webapi_config_version"]="1",["webapi_deployment_sequence"]="2",["webapi_destination_id"]=DestinationId.ToString(),["unexpected_secret"]="do-not-return"}}).ToArray();
            var streams=new List<object>();var baseLabels=new Dictionary<string,string>{["service_name"]="webapi-gateway",["webapi_environment_id"]=EnvironmentId.ToString()};
            if(Flattened){foreach(var row in values){var labels=new Dictionary<string,string>(baseLabels);foreach(var field in (Dictionary<string,string>)row[2])labels[field.Key]=field.Value;streams.Add(new{stream=labels,values=new[]{new object[]{row[0],row[1]}}});}}else streams.Add(new{stream=baseLabels,values});
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{status="success",data=new{resultType="streams",result=streams}})};
        }
        var q=p["query"].ToString();var time=End.ToUnixTimeMilliseconds()/1000d;var value=q.Contains("otelcol_")?CollectorLoss?1:0:q.Contains("last_loss_timestamp")?FirstLoss?End.AddSeconds(-5).ToUnixTimeMilliseconds()/1000d:0:q.Contains("max_over_time")?MiddleGap?90:5:q.Contains("min_over_time")?Start.AddSeconds(5).ToUnixTimeMilliseconds()/1000d:q.Contains("last_observed_timestamp")?time-2:0;
        return new(HttpStatusCode.OK){Content=JsonContent.Create(new{status="success",data=new{resultType="vector",result=new[]{new{metric=new Dictionary<string,string>{["webapi_environment_id"]=EnvironmentId.ToString(),["service_instance_id"]="logs-node"},value=new object[]{time,value.ToString(CultureInfo.InvariantCulture)}}}}})};
    }
}

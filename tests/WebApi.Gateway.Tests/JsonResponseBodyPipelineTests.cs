using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Policies;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Policies;
using Xunit;
using static WebApi.Gateway.Tests.Support.RecordingTelemetrySink;
namespace WebApi.Gateway.Tests;
public sealed class JsonResponseBodyPipelineTests
{
    private sealed class CancellationWitness(TaskCompletionSource observed):IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>
        {
            app.Use(async(HttpContext context,RequestDelegate handler)=>
            {using var registration=context.Request.Path=="/orders"?context.RequestAborted.Register(()=>observed.TrySetResult()):default;await handler(context);});
            next(app);
        };
    }
    internal static string Config(bool both=false,bool headers=false,int limit=1024,string? operations=null)
    {
        var root=JsonNode.Parse(TransformPipelineTests.Config(headers:headers))!;
        root["responseBody"]=new JsonObject{["statusCodes"]=new JsonArray(200),["maxBodyBytes"]=limit,["operations"]=JsonNode.Parse(operations??"[{\"op\":\"move\",\"from\":\"/data\",\"path\":\"/result\"},{\"op\":\"remove\",\"path\":\"/debug\"}]")};
        if(both)root["requestBody"]=JsonNode.Parse(JsonRequestBodyPipelineTests.Config(limit))!["requestBody"]!.DeepClone();return root.ToJsonString();
    }
    private static void Telemetry(GatewayFixture f,RecordingTelemetrySink sink)
    {var prior=f.ConfigureGateway;f.ConfigureGateway=b=>{prior?.Invoke(b);b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:TraceSampleRatio"]="1";b.Configuration["Observability:BatchDelayMs"]="20";b.Services.AddSingleton<ITelemetryBatchSink>(sink);};}
    private static async Task SendBody(HttpContext context,byte[] bytes,string? encoding=null,int status=200,string media="application/json")
    {context.Response.StatusCode=status;context.Response.ContentType=media;context.Response.ContentLength=bytes.Length;context.Response.Headers.ETag="\"old\"";context.Response.Headers.LastModified="Wed, 21 Oct 2015 07:28:00 GMT";context.Response.Headers["Digest"]="sha-256=:dGVzdA==:";if(encoding is not null)context.Response.Headers.ContentEncoding=encoding;await context.Response.Body.WriteAsync(bytes,context.RequestAborted);}
    [Theory][InlineData(null)][InlineData("gzip")][InlineData("br")]
    public async Task JsonResponse_MapsCompleteFinalRepresentation_WithOwnedHeaders(string? encoding)
    {
        var source=JsonBodyCodecTests.Encode("{\"data\":{\"stock\":10},\"debug\":\"private-upstream-body\"}"u8.ToArray(),encoding);
        await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>await SendBody(ctx,source,encoding))};await f.InitializeAsync();await TransformPipelineTests.BindAsync(f,Config(headers:true));await CorsPipelineTests.BindAsync(f);await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1})
        {using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Add("X-API-Key",f.Credential);request.Headers.Add("Origin","https://portal.example.com");using var response=await f.Clients[node].SendAsync(request);response.EnsureSuccessStatusCode();var bytes=await response.Content.ReadAsByteArrayAsync();using var json=JsonDocument.Parse(bytes);Assert.Equal(10,json.RootElement.GetProperty("result").GetProperty("stock").GetInt32());Assert.False(json.RootElement.TryGetProperty("data",out _));Assert.False(json.RootElement.TryGetProperty("debug",out _));Assert.Equal(bytes.Length,response.Content.Headers.ContentLength);Assert.Equal("application/json; charset=utf-8",response.Content.Headers.ContentType!.ToString());Assert.Empty(response.Content.Headers.ContentEncoding);Assert.False(response.Headers.Contains("ETag"));Assert.False(response.Content.Headers.Contains("Last-Modified"));Assert.False(response.Headers.Contains("Digest"));Assert.Equal("inventory",response.Headers.GetValues("X-Upstream-Service").Single());Assert.Equal("https://portal.example.com",response.Headers.GetValues("Access-Control-Allow-Origin").Single());}
    }
    [Theory]
    [InlineData("missing","transform_json_operation_failed")][InlineData("invalid-json","transform_json_invalid")][InlineData("non-json","transform_body_media_unsupported")]
    [InlineData("empty-subtype","transform_body_media_unsupported")][InlineData("duplicate-charset","transform_body_media_unsupported")]
    [InlineData("encoding","transform_body_encoding_unsupported")][InlineData("broken-gzip","transform_body_encoding_unsupported")]
    [InlineData("oversize","transform_body_too_large")][InlineData("decoded","transform_body_too_large")][InlineData("output","transform_body_too_large")]
    [InlineData("trailer","transform_body_trailer_unsupported")][InlineData("signature","transform_body_signature_unsupported")][InlineData("signature-input","transform_body_signature_unsupported")]
    public async Task JsonResponse_SelectedFailure_IsAtomic502AndNeverCached(string kind,string reason)
    {
        var sink=new RecordingTelemetrySink();await using var fixture=new CachePipelineFixture();Telemetry(fixture.F,sink);
        fixture.Respond=async(ctx,n)=>
        {
            var raw=kind switch{"missing"=>"{\"debug\":\"private-upstream-body\"}","invalid-json"=>"private-upstream-body not-json", "oversize" or "decoded"=>"{\"data\":{\"secret\":\""+new string('x',1500)+"\"},\"debug\":\"private-upstream-body\"}",_=>"{\"data\":{\"stock\":10},\"debug\":\"private-upstream-body\"}"};
            var encoding=kind=="decoded"?"gzip":kind=="encoding"?"gzip, br":kind=="broken-gzip"?"gzip":null;
            var bytes=kind=="broken-gzip"?new byte[]{255,255,255}:JsonBodyCodecTests.Encode(Encoding.UTF8.GetBytes(raw),kind=="decoded"?"gzip":null);
            if(kind=="trailer")ctx.Response.Headers["Trailer"]="X-Tail";
            if(kind is "signature" or "signature-input")ctx.Response.Headers[kind=="signature"?"Signature":"Signature-Input"]="test";
            await SendBody(ctx,bytes,encoding,media:kind switch{"non-json"=>"text/plain","empty-subtype"=>"application/+json","duplicate-charset"=>"application/json; charset=utf-8; charset=iso-8859-1",_=>"application/json"});
        };
        await fixture.Init();var operations=kind=="output"?"[{\"op\":\"add\",\"path\":\"/big\",\"value\":\""+new string('x',1100)+"\"}]":null;await TransformPipelineTests.BindAsync(fixture.F,Config(operations:operations));await fixture.F.ApplyBothAsync(await fixture.F.PublishAsync());
        using var response=await fixture.F.RequestAsync();Assert.Equal(HttpStatusCode.BadGateway,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("private-upstream-body",text);using var json=JsonDocument.Parse(text);Assert.Equal("transform_rejected",json.RootElement.GetProperty("code").GetString());Assert.Equal(reason,json.RootElement.GetProperty("reason").GetString());Assert.False(response.Headers.Contains("ETag"));Assert.False(response.Headers.Contains("Digest"));Assert.Empty(response.Content.Headers.ContentEncoding);await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal("502",Attribute(sink.Logs[0],"http.response.status_code"));Assert.Equal("ProxyError",Attribute(sink.Logs[0],"webapi.outcome"));Assert.Equal("Rejected",Attribute(sink.Logs[0],"webapi.policy.transform.decision"));Assert.All(fixture.Stores,s=>Assert.Equal(0,s.Writes));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task JsonResponse_SelectedHeadOrUnselectedStatus_PreservesCorrectSemantics(bool head)
    {
        var source=JsonBodyCodecTests.Encode("{\"data\":{\"stock\":10},\"debug\":\"old\"}"u8.ToArray(),"gzip");
        await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>await SendBody(ctx,source,"gzip",status:head?200:503))};await f.InitializeAsync();if(head)await TransformPipelineTests.RouteAsync(f,"/orders","HEAD");await TransformPipelineTests.BindAsync(f,Config());await f.ApplyBothAsync(await f.PublishAsync());
        using var request=new HttpRequestMessage(head?HttpMethod.Head:HttpMethod.Get,"/orders");request.Headers.Add("X-API-Key",f.Credential);using var response=await f.Clients[0].SendAsync(request);Assert.Equal(head?HttpStatusCode.OK:HttpStatusCode.ServiceUnavailable,response.StatusCode);
        if(head){Assert.Empty(await response.Content.ReadAsByteArrayAsync());Assert.NotEqual(source.Length,response.Content.Headers.ContentLength);Assert.False(response.Headers.Contains("ETag"));Assert.Empty(response.Content.Headers.ContentEncoding);}
        else{Assert.Equal(source,await response.Content.ReadAsByteArrayAsync());Assert.Equal("\"old\"",response.Headers.ETag!.Tag);Assert.Equal("gzip",Assert.Single(response.Content.Headers.ContentEncoding));}
    }
    [Fact]
    public async Task JsonResponse_QuotaBusy_Is503WithCorsAndNoFalseUpstreamSuccess()
    {
        var sink=new RecordingTelemetrySink();await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>await SendBody(ctx,"{\"data\":{\"stock\":10},\"debug\":\"old\"}"u8.ToArray()))};Telemetry(f,sink);await f.InitializeAsync();await TransformPipelineTests.BindAsync(f,Config(headers:true));await CorsPipelineTests.BindAsync(f);await RetryPipelineTests.Bind(f,RetryPipelineTests.Config());await f.ApplyBothAsync(await f.PublishAsync());var budget=f.Gateways[0].Services.GetRequiredService<BodyTransformBudget>();var held=new List<BodyTransformBudget.Lease>();for(var i=0;i<16;i++){Assert.True(budget.TryAcquire(out var lease));held.Add(lease!);}
        try{using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Add("X-API-Key",f.Credential);request.Headers.Add("Origin","https://portal.example.com");using var response=await f.Clients[0].SendAsync(request);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal("https://portal.example.com",response.Headers.GetValues("Access-Control-Allow-Origin").Single());Assert.False(response.Headers.Contains("X-Upstream-Service"));Assert.Contains("transform_body_busy",await response.Content.ReadAsStringAsync());await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal("503",Attribute(sink.Logs[0],"http.response.status_code"));Assert.Equal("ProxyError",Attribute(sink.Logs[0],"webapi.outcome"));Assert.Equal("503",Attribute(sink.Items.Single(x=>x.Signal=="traces"&&x.Item.GetProperty("kind").GetInt32()==3).Item,"http.response.status_code"));}
        finally{foreach(var lease in held)lease.Dispose();}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task JsonResponse_RetrySuppressesIntermediateBody_MapsOnlyFinalAndStopsOnLocalFailure(bool fail)
    {
        var calls=0;var sink=new RecordingTelemetrySink();await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{var n=Interlocked.Increment(ref calls);await SendBody(ctx,n<3?"not-json-private-intermediate"u8.ToArray():fail?"private-final-invalid"u8.ToArray():"{\"data\":{\"stock\":10},\"debug\":\"old\"}"u8.ToArray(),status:n<3?503:200);})};Telemetry(f,sink);await f.InitializeAsync();await TransformPipelineTests.BindAsync(f,Config());await RetryPipelineTests.Bind(f,RetryPipelineTests.Config());await f.ApplyBothAsync(await f.PublishAsync());using var response=await f.RequestAsync();Assert.Equal(fail?HttpStatusCode.BadGateway:HttpStatusCode.OK,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("private",text);if(!fail){using var json=JsonDocument.Parse(text);Assert.Equal(10,json.RootElement.GetProperty("result").GetProperty("stock").GetInt32());}Assert.Equal(3,calls);await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal(fail?"Rejected":"Applied",Attribute(sink.Logs[0],"webapi.policy.transform.decision"));Assert.Equal(fail?"ProxyError":"Completed",Attribute(sink.Logs[0],"webapi.outcome"));var attempts=sink.Items.Where(x=>x.Signal=="traces"&&x.Item.GetProperty("kind").GetInt32()==3).Select(x=>Attribute(x.Item,"http.response.status_code")).Order().ToArray();Assert.Equal(fail?new[]{"502","503","503"}:new[]{"200","503","503"},attempts);
    }
    [Fact]
    public async Task JsonResponse_TwoDirections_ReuseOneLease()
    {
        var sink=new RecordingTelemetrySink();await using var f=new GatewayFixture{ConfigureGateway=b=>b.Configuration["GatewayPolicies:Transform:MaxConcurrentBodyTransforms"]="1",ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{using var reader=new StreamReader(ctx.Request.Body);using var request=JsonDocument.Parse(await reader.ReadToEndAsync(ctx.RequestAborted));Assert.Equal(123,request.RootElement.GetProperty("customer_id").GetInt32());await SendBody(ctx,"{\"data\":{\"stock\":10},\"debug\":\"old\"}"u8.ToArray());})};Telemetry(f,sink);await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/orders","POST");await TransformPipelineTests.BindAsync(f,Config(both:true));await f.ApplyBothAsync(await f.PublishAsync());
        using var message=new HttpRequestMessage(HttpMethod.Post,"/orders"){Content=new StringContent("{\"user_id\":123}",Encoding.UTF8,"application/json")};message.Headers.Add("X-API-Key",f.Credential);using var response=await f.Clients[0].SendAsync(message);response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.Equal(10,json.RootElement.GetProperty("result").GetProperty("stock").GetInt32());await sink.WaitAsync(()=>sink.Logs.Length==1);var budget=f.Gateways[0].Services.GetRequiredService<BodyTransformBudget>();Assert.True(budget.TryAcquire(out var recovered));recovered!.Dispose();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task JsonResponse_NewRepresentationHeaders_AreNotRemovedByOldConnectionNomination(bool nominateRequest)
    {
        await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>
        {using var reader=new StreamReader(ctx.Request.Body);var request=await reader.ReadToEndAsync(ctx.RequestAborted);ctx.Response.Headers.Connection="Content-Type, Content-Length, X-Skip";ctx.Response.Headers["X-Skip"]="private-hop";await SendBody(ctx,JsonSerializer.SerializeToUtf8Bytes(new{data=new{requestType=ctx.Request.ContentType,requestLength=ctx.Request.ContentLength,request},debug="old"}));})};await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/orders","POST");await TransformPipelineTests.BindAsync(f,Config(both:true,headers:true));await f.ApplyBothAsync(await f.PublishAsync());
        using var message=new HttpRequestMessage(HttpMethod.Post,"/orders"){Content=new StringContent("{\"user_id\":123}",Encoding.UTF8,"application/json")};message.Headers.Add("X-API-Key",f.Credential);if(nominateRequest)message.Headers.Add("Connection","Content-Type, Content-Length, X-Skip");using var response=await f.Clients[0].SendAsync(message);response.EnsureSuccessStatusCode();var bytes=await response.Content.ReadAsByteArrayAsync();using var body=JsonDocument.Parse(bytes);var result=body.RootElement.GetProperty("result");Assert.Equal("application/json; charset=utf-8",result.GetProperty("requestType").GetString());Assert.Equal(Encoding.UTF8.GetByteCount(result.GetProperty("request").GetString()!),result.GetProperty("requestLength").GetInt64());Assert.Equal("application/json; charset=utf-8",response.Content.Headers.ContentType!.ToString());Assert.Equal(bytes.Length,response.Content.Headers.ContentLength);Assert.False(response.Headers.Contains("X-Skip"));
    }
    private sealed class RawTrailerBackend : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener=new(IPAddress.Loopback,0);private readonly CancellationTokenSource stop=new();private readonly Task worker;public string Url {get;}
        public RawTrailerBackend(bool declare)
        {listener.Start();Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;worker=Serve(declare);}
        private async Task Serve(bool declare)
        {
            try
            {
                while(!stop.IsCancellationRequested)
                {
                    using var client=await listener.AcceptTcpClientAsync(stop.Token);await using var stream=client.GetStream();var headers="";var buffer=new byte[1024];
                    while(!headers.Contains("\r\n\r\n",StringComparison.Ordinal)){var count=await stream.ReadAsync(buffer,stop.Token);if(count==0)break;headers+=Encoding.ASCII.GetString(buffer,0,count);if(headers.Length>16384)throw new InvalidDataException("Test request header budget exceeded.");}
                    const string body="{\"data\":{\"stock\":10},\"debug\":\"old\"}";var wire="HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n"+(declare?"Trailer: X-Tail\r\n":"")+"\r\n"+Encoding.UTF8.GetByteCount(body).ToString("x")+"\r\n"+body+"\r\n0\r\nX-Tail: private-upstream-tail\r\n\r\n";await stream.WriteAsync(Encoding.ASCII.GetBytes(wire),stop.Token);
                }
            }
            catch(Exception error)when(stop.IsCancellationRequested&&error is OperationCanceledException or System.Net.Sockets.SocketException or IOException){ }
        }
        public async ValueTask DisposeAsync(){stop.Cancel();listener.Stop();await worker;stop.Dispose();}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task JsonResponse_ActualDeclaredOrUndeclaredTrailers_AreRejectedBeforeOutput(bool declare)
    {
        await using var raw=new RawTrailerBackend(declare);await using var f=new GatewayFixture{ConfigureGateway=b=>b.Configuration["Upstream:AllowedOrigins:2"]=raw.Url,ConfigureControl=b=>b.Configuration["Upstream:AllowedOrigins:2"]=raw.Url};await f.InitializeAsync();f.BackendUrls[0]=raw.Url;await TransformPipelineTests.BindAsync(f,Config());await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1}){using var response=await f.RequestAsync(node);Assert.Equal(HttpStatusCode.BadGateway,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.Contains("transform_body_trailer_unsupported",text);Assert.DoesNotContain("private-upstream-tail",text);Assert.False(response.Headers.Contains("Trailer"));Assert.Empty(response.TrailingHeaders);}
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task JsonResponse_HeldReadPublishAndCancel_UsesFrozenGenerationAndReleases(bool cancel)
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var serverAborted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var hold=1;var sink=new RecordingTelemetrySink();
        await using var f=new GatewayFixture{ConfigureGateway=b=>{b.Configuration["GatewayPolicies:Transform:MaxConcurrentBodyTransforms"]="1";b.Services.AddSingleton<IStartupFilter>(new CancellationWitness(serverAborted));},ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>
        {
            if(Interlocked.Exchange(ref hold,0)==1){ctx.Response.ContentType="application/json";await ctx.Response.WriteAsync("{\"data\":{\"stock\":10},",ctx.RequestAborted);await ctx.Response.Body.FlushAsync(ctx.RequestAborted);started.TrySetResult();await release.Task.WaitAsync(ctx.RequestAborted);await ctx.Response.WriteAsync("\"debug\":\"old\"}",ctx.RequestAborted);}
            else await SendBody(ctx,"{\"data\":{\"stock\":10},\"debug\":\"old\"}"u8.ToArray());
        })};Telemetry(f,sink);await f.InitializeAsync();var policy=await TransformPipelineTests.BindAsync(f,Config());var original=await f.PublishAsync();await f.ApplyBothAsync(original);
        using var abort=new CancellationTokenSource(TimeSpan.FromSeconds(20));using var message=new HttpRequestMessage(HttpMethod.Get,"/orders");message.Headers.Add("X-API-Key",f.Credential);var pending=f.Clients[0].SendAsync(message,abort.Token);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var budget=f.Gateways[0].Services.GetRequiredService<BodyTransformBudget>();var entered=false;
        for(var i=0;i<100;i++){if(budget.TryAcquire(out var free)){free!.Dispose();await Task.Delay(10);}else{entered=true;break;}}Assert.True(entered);
        await using(var db=f.Control.Context()){var saved=await db.Set<Policy>().SingleAsync(p=>p.Id==policy.Id);saved.Config=Config().Replace("\"/result\"","\"/latest\"",StringComparison.Ordinal);saved.VersionNo++;await db.SaveChangesAsync();}
        var current=await f.PublishAsync();await f.ApplyBothAsync(current);var store=f.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>();using(var old=store.Acquire(original.Envelope.DeploymentSequence))Assert.Equal(original.Envelope,old.Generation.Envelope);
        try
        {
            if(cancel){abort.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await pending);await serverAborted.Task.WaitAsync(TimeSpan.FromSeconds(5));}
            else{release.TrySetResult();using var response=await pending;response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Assert.True(json.RootElement.TryGetProperty("result",out _));Assert.False(json.RootElement.TryGetProperty("latest",out _));Assert.Equal(original.Envelope.DeploymentSequence.ToString(),response.Headers.GetValues("X-WebApi-Deployment-Sequence").Single());}
        }
        finally{abort.Cancel();release.TrySetResult();}
        await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal(cancel?"ClientAborted":"Completed",Attribute(sink.Logs[0],"webapi.outcome"));Assert.Throws<InvalidOperationException>(()=>store.Acquire(original.Envelope.DeploymentSequence));Assert.True(budget.TryAcquire(out var recovered));recovered!.Dispose();
        using var nextResponse=await f.RequestAsync();nextResponse.EnsureSuccessStatusCode();using var nextBody=JsonDocument.Parse(await nextResponse.Content.ReadAsStringAsync());Assert.True(nextBody.RootElement.TryGetProperty("latest",out _));Assert.False(nextBody.RootElement.TryGetProperty("result",out _));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task JsonResponse_StalledSelectedBody_RespectsRouteOrPhysicalAttemptTimeout(bool retry)
    {
        var sink=new RecordingTelemetrySink();await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{ctx.Response.ContentType="application/json";await ctx.Response.WriteAsync("{\"data\":",ctx.RequestAborted);await ctx.Response.Body.FlushAsync(ctx.RequestAborted);await Task.Delay(5000,ctx.RequestAborted);})};Telemetry(f,sink);await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/orders",timeoutMs:retry?5000:300);await TransformPipelineTests.BindAsync(f,Config());if(retry)await RetryPipelineTests.Bind(f,RetryPipelineTests.Config(perAttempt:100));await f.ApplyBothAsync(await f.PublishAsync());
        using var response=await f.RequestAsync();Assert.Equal(HttpStatusCode.GatewayTimeout,response.StatusCode);var text=await response.Content.ReadAsStringAsync();Assert.DoesNotContain("data",text);await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal("Timeout",Attribute(sink.Logs[0],"webapi.outcome"));var budget=f.Gateways[0].Services.GetRequiredService<BodyTransformBudget>();Assert.True(budget.TryAcquire(out var recovered));recovered!.Dispose();
    }
    [Theory][InlineData(502)][InlineData(503)]
    public async Task JsonResponse_LocalFailure_UsesConfiguredStatusCircuitRulesWithoutConnectionFault(int countedStatus)
    {
        var hits=0;var sink=new RecordingTelemetrySink();await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref hits);await SendBody(ctx,"private-invalid-json"u8.ToArray());})};Telemetry(f,sink);await f.InitializeAsync();await TransformPipelineTests.BindAsync(f,Config());await RetryPipelineTests.Bind(f,RetryPipelineTests.Config(attempts:1),circuit:true);
        await using(var db=f.Control.Context()){var policy=await db.Set<Policy>().SingleAsync(p=>p.Type=="circuit_breaker");policy.Config=JsonSerializer.Serialize(new{samplingWindowMs=30000,minimumRequests=1,failureRatio=1,openDurationMs=30000,halfOpenMaxRequests=1,halfOpenSuccesses=1,failureStatusCodes=new[]{countedStatus},countTimeouts=false,countConnectionFailures=true});await db.SaveChangesAsync();}
        await f.ApplyBothAsync(await f.PublishAsync());using(var first=await f.RequestAsync())Assert.Equal(HttpStatusCode.BadGateway,first.StatusCode);using(var second=await f.RequestAsync())Assert.Equal(countedStatus==502?HttpStatusCode.ServiceUnavailable:HttpStatusCode.BadGateway,second.StatusCode);Assert.Equal(countedStatus==502?1:2,hits);await sink.WaitAsync(()=>sink.Logs.Length==2);var firstLog=sink.Logs.First();Assert.Equal("Bypass",Attribute(firstLog,"webapi.policy.retry.decision"));Assert.Equal("response_transform_rejected",Attribute(firstLog,"webapi.policy.retry.rejection_reason"));Assert.Equal("502",Attribute(firstLog,"http.response.status_code"));Assert.Equal("ProxyError",Attribute(firstLog,"webapi.outcome"));
    }

}

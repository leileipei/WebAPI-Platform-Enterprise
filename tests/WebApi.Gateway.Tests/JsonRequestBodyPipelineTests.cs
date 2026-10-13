using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Yarp.ReverseProxy.Forwarder;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Tests.Support;
using WebApi.Gateway.Workers;
using WebApi.Infrastructure.Policies;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class JsonRequestBodyPipelineTests
{
    internal static string Config(int limit=1024,string[]? methods=null,bool response=false,string? operations=null)
    {var root=JsonNode.Parse(TransformPipelineTests.Config(headers:false))!;root["requestBody"]=new JsonObject{["methods"]=JsonSerializer.SerializeToNode(methods??["POST"]),["maxBodyBytes"]=limit,["operations"]=JsonNode.Parse(operations??"[{\"op\":\"move\",\"from\":\"/user_id\",\"path\":\"/customer_id\"},{\"op\":\"add\",\"path\":\"/source\",\"value\":\"webapi\"}]")};if(response)root["responseBody"]=new JsonObject{["statusCodes"]=new JsonArray(200),["maxBodyBytes"]=limit,["operations"]=JsonNode.Parse("[{\"op\":\"add\",\"path\":\"/responseMapped\",\"value\":true}]")};return root.ToJsonString();}
    private sealed class ForwarderDiagnostic : IStartupFilter
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Errors=new();
        public System.Collections.Concurrent.ConcurrentQueue<(byte[] Body,string? Media,long? Length,bool CanHaveBody)> Originals=new();
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>{app.Use(async(HttpContext context,RequestDelegate continuation)=>{context.Response.OnStarting(async()=>{if(WebApi.Gateway.Policies.TrafficExecutionContext.From(context)?.RequestBodyApplied==true){var source=context.Request.Body;using var retained=new MemoryStream();await source.CopyToAsync(retained,context.RequestAborted);source.Position=0;Originals.Enqueue((retained.ToArray(),context.Request.ContentType,context.Request.ContentLength,context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()!.CanHaveBody));}});await continuation(context);if(context.Features.Get<IForwarderErrorFeature>() is { } error){for(var ex=error.Exception;ex is not null;ex=ex.InnerException)Errors.Enqueue(error.Error+" "+ex.GetType().Name+" "+ex.Message);}});next(app);};
    }
    private static GatewayFixture Echo(Action<WebApplicationBuilder>? configure=null)=>new(){ConfigureGateway=b=>{configure?.Invoke(b);b.Services.AddSingleton<ForwarderDiagnostic>();b.Services.AddSingleton<IStartupFilter>(sp=>sp.GetRequiredService<ForwarderDiagnostic>());},ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{using var reader=new StreamReader(ctx.Request.Body);var body=await reader.ReadToEndAsync(ctx.RequestAborted);await ctx.Response.WriteAsJsonAsync(new{body,path=ctx.Request.Path.Value,method=ctx.Request.Method,contentType=ctx.Request.ContentType,length=ctx.Request.ContentLength,encoding=ctx.Request.Headers.ContentEncoding.ToString(),digest=ctx.Request.Headers["Digest"].ToString(),etag=ctx.Request.Headers["ETag"].ToString(),apiKey=ctx.Request.Headers.ContainsKey("X-API-Key")});})};
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context)=>stream.WriteAsync(bytes).AsTask();protected override bool TryComputeLength(out long length){length=0;return false;}}
    private static HttpRequestMessage Request(GatewayFixture f,byte[] bytes,string? encoding=null,string media="application/json",string method="POST",bool unknown=false)
    {var request=new HttpRequestMessage(new HttpMethod(method),"/orders"){Content=unknown?new UnknownLengthContent(bytes):new ByteArrayContent(bytes)};request.Content.Headers.TryAddWithoutValidation("Content-Type",media);if(encoding is not null)request.Content.Headers.TryAddWithoutValidation("Content-Encoding",encoding);request.Headers.Add("X-API-Key",f.Credential);return request;}
    [Theory][InlineData(null,false)][InlineData("gzip",false)][InlineData("br",true)]
    public async Task JsonRequest_RealPost_UsesMappedBackendContentOnly(string? encoding,bool unknown)
    {
        await using var f=Echo();await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/orders","POST");await TransformPipelineTests.BindAsync(f,Config());await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var node in new[]{0,1})
        {var original=JsonBodyCodecTests.Encode("{\"user_id\":123,\"items\":[\"A\",\"B\"]}"u8.ToArray(),encoding);using var request=Request(f,original,encoding,unknown:unknown);request.Headers.TryAddWithoutValidation("Digest","sha-256=:dGVzdA==:");request.Headers.Add("ETag","\"old\"");using var response=await f.Clients[node].SendAsync(request);Assert.True(response.IsSuccessStatusCode,"Forwarder errors: "+string.Join(" | ",f.Gateways[node].Services.GetRequiredService<ForwarderDiagnostic>().Errors));var echo=(await response.Content.ReadFromJsonAsync<JsonElement>());var text=echo.GetProperty("body").GetString()!;using var json=JsonDocument.Parse(text);Assert.Equal(123,json.RootElement.GetProperty("customer_id").GetInt32());Assert.False(json.RootElement.TryGetProperty("user_id",out _));Assert.Equal("webapi",json.RootElement.GetProperty("source").GetString());Assert.Equal("/orders",echo.GetProperty("path").GetString());Assert.Equal("POST",echo.GetProperty("method").GetString());Assert.Equal(Encoding.UTF8.GetByteCount(text),echo.GetProperty("length").GetInt64());Assert.Equal("application/json; charset=utf-8",echo.GetProperty("contentType").GetString());Assert.Equal("",echo.GetProperty("encoding").GetString());Assert.Equal("",echo.GetProperty("digest").GetString());Assert.Equal("",echo.GetProperty("etag").GetString());Assert.False(echo.GetProperty("apiKey").GetBoolean());Assert.True(f.Gateways[node].Services.GetRequiredService<ForwarderDiagnostic>().Originals.TryDequeue(out var preserved));Assert.Equal(original,preserved.Body);Assert.Equal("application/json",preserved.Media);Assert.Equal(unknown?null:(long?)original.Length,preserved.Length);Assert.True(preserved.CanHaveBody);}
    }
    [Theory]
    [InlineData("empty",400,"transform_json_invalid")][InlineData("duplicate",400,"transform_json_invalid")][InlineData("invalid-utf8",400,"transform_json_invalid")]
    [InlineData("empty-subtype",415,"transform_body_media_unsupported")][InlineData("duplicate-charset",415,"transform_body_media_unsupported")]
    [InlineData("media",415,"transform_body_media_unsupported")][InlineData("charset",415,"transform_body_media_unsupported")][InlineData("encoding",415,"transform_body_encoding_unsupported")]
    [InlineData("too-large",413,"transform_body_too_large")][InlineData("output",413,"transform_body_too_large")][InlineData("missing",422,"transform_json_operation_failed")]
    [InlineData("signature",415,"transform_body_signature_unsupported")][InlineData("signature-input",415,"transform_body_signature_unsupported")]
    [InlineData("sse",415,"transform_body_stream_unsupported")][InlineData("grpc",415,"transform_body_stream_unsupported")][InlineData("upgrade",415,"transform_body_stream_unsupported")]
    [InlineData("range",400,"transform_body_condition_unsupported")][InlineData("if-none-match",400,"transform_body_condition_unsupported")]
    public async Task JsonRequest_InvalidSelectedBody_FailsBeforeBackend(string kind,int status,string reason)
    {
        var hits=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref hits);await next(ctx);})};await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/orders","POST");
        var operations=kind=="output"?"[{\"op\":\"add\",\"path\":\"/big\",\"value\":\""+new string('x',1100)+"\"}]":null;await TransformPipelineTests.BindAsync(f,Config(response:kind is "range" or "if-none-match",operations:operations));await f.ApplyBothAsync(await f.PublishAsync());
        var bytes=kind switch{"empty"=>[],"duplicate"=>"{\"user_id\":1,\"user_id\":2}"u8.ToArray(),"invalid-utf8"=>new byte[]{34,255,34},"too-large"=>Encoding.UTF8.GetBytes("{\"x\":\""+new string('x',1025)+"\"}"),"missing"=>"{}"u8.ToArray(),_=>"{\"user_id\":123}"u8.ToArray()};
        foreach(var node in new[]{0,1})
        {using var request=Request(f,bytes,encoding:kind=="encoding"?"gzip, br":null,media:kind switch{"media"=>"text/plain","empty-subtype"=>"application/+json","duplicate-charset"=>"application/json; charset=utf-8; charset=iso-8859-1","charset"=>"application/json; charset=iso-8859-1","grpc"=>"application/grpc",_=>"application/json"},unknown:kind=="too-large");if(kind is "signature" or "signature-input")request.Headers.TryAddWithoutValidation(kind=="signature"?"Signature":"Signature-Input","test");if(kind=="sse")request.Headers.Accept.ParseAdd("text/event-stream");if(kind=="upgrade"){request.Headers.Add("Connection","upgrade");request.Headers.Add("Upgrade","websocket");}if(kind=="range")request.Headers.Add("Range","bytes=0-2");if(kind=="if-none-match")request.Headers.Add("If-None-Match","\"v1\"");using var response=await f.Clients[node].SendAsync(request);Assert.Equal(status,(int)response.StatusCode);var json=(await response.Content.ReadFromJsonAsync<JsonElement>());Assert.Equal(reason,json.GetProperty("reason").GetString());Assert.Equal("transform_rejected",json.GetProperty("code").GetString());}Assert.Equal(0,hits);
    }
    [Fact]
    public async Task JsonRequest_UnselectedMethod_DoesNotValidateOrMapBody()
    {await using var f=Echo();await f.InitializeAsync();await TransformPipelineTests.BindAsync(f,Config());await f.ApplyBothAsync(await f.PublishAsync());using var request=Request(f,"not-json"u8.ToArray(),media:"text/plain",method:"GET");using var response=await f.Clients[0].SendAsync(request);response.EnsureSuccessStatusCode();Assert.Equal("not-json",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("body").GetString());}
    [Fact]
    public async Task JsonRequest_OverNodeLimit_NacksAndRetainsOldGeneration()
    {await using var f=Echo(b=>b.Configuration["GatewayPolicies:Transform:MaxBodyBytes"]="1024");await f.InitializeAsync();var original=await f.PublishAsync();await f.ApplyBothAsync(original);await TransformPipelineTests.BindAsync(f,Config(2048));var next=await f.PublishAsync();foreach(var gateway in f.Gateways){var result=await gateway.Services.GetRequiredService<ConfigWatcher>().AcceptAsync(next);Assert.False(result.Applied);Assert.Equal("invalid_snapshot",result.ErrorCode);Assert.Equal(original.Envelope.DeploymentSequence,gateway.Services.GetRequiredService<RuntimeGenerationStore>().Current!.Envelope.DeploymentSequence);}using var ok=await f.RequestAsync();ok.EnsureSuccessStatusCode();}
    [Fact]
    public async Task JsonRequest_ActualDefaultQuota16_Rejects17AndCancelRestores()
    {
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var hits=0;
        await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{if(Interlocked.Increment(ref hits)==16)started.TrySetResult();await release.Task.WaitAsync(ctx.RequestAborted);await ctx.Response.WriteAsJsonAsync(new{ok=true});})};await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/orders","POST");await TransformPipelineTests.BindAsync(f,Config());await f.ApplyBothAsync(await f.PublishAsync());
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(20));var requests=Enumerable.Range(0,16).Select(_=>Request(f,"{\"user_id\":123}"u8.ToArray())).ToArray();var pending=requests.Select(r=>f.Clients[0].SendAsync(r,cancel.Token)).ToArray();
        try{await started.Task.WaitAsync(TimeSpan.FromSeconds(10));using var seventeenth=Request(f,"{\"user_id\":123}"u8.ToArray());using var busy=await f.Clients[0].SendAsync(seventeenth);Assert.Equal(HttpStatusCode.ServiceUnavailable,busy.StatusCode);Assert.Contains("transform_body_busy",await busy.Content.ReadAsStringAsync());Assert.Equal(16,hits);cancel.Cancel();foreach(var call in pending)await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await call);}
        finally{cancel.Cancel();release.TrySetResult();foreach(var request in requests)request.Dispose();}
        var budget=f.Gateways[0].Services.GetRequiredService<BodyTransformBudget>();var restored=false;for(var attempt=0;attempt<100;attempt++){var held=new List<BodyTransformBudget.Lease>();while(held.Count<16&&budget.TryAcquire(out var lease))held.Add(lease!);restored=held.Count==16;foreach(var lease in held)lease.Dispose();if(restored)break;await Task.Delay(10);}Assert.True(restored);
    }
    [Theory][InlineData(401)][InlineData(429)][InlineData(204)]
    public async Task JsonRequest_AuthRateAndPreflight_DoNotAcquireBodyBudget(int status)
    {
        await using var f=Echo(b=>{if(status==429)b.Services.AddSingleton<WebApi.Gateway.Policies.IRateLimitStore>(new RejectRate());});await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/orders","POST");await TransformPipelineTests.BindAsync(f,Config());
        if(status==204)await CorsPipelineTests.BindAsync(f,methods:["POST"]);
        if(status==429)
        {await using var db=f.Control.Context();var p=new WebApi.Infrastructure.Persistence.Entities.Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="BodyRate",Type="rate_limit",Config="{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":1,\"windowMs\":600000,\"burst\":1,\"redisFailureMode\":\"Reject\"}"};db.Add(p);db.Add(new WebApi.Infrastructure.Persistence.Entities.RoutePolicyBinding{RouteId=f.RouteId,PolicyId=p.Id,Priority=2});await db.SaveChangesAsync();}
        await f.ApplyBothAsync(await f.PublishAsync());var budget=f.Gateways[0].Services.GetRequiredService<BodyTransformBudget>();var held=new List<BodyTransformBudget.Lease>();for(var i=0;i<16;i++){Assert.True(budget.TryAcquire(out var lease));held.Add(lease!);}
        try
        {
            using var request=Request(f,new byte[]{255});
            if(status==401){request.Headers.Remove("X-API-Key");request.Headers.Add("X-API-Key","invalid.invalid");}
            if(status==204){request.Method=HttpMethod.Options;request.Content=null;request.Headers.Remove("X-API-Key");request.Headers.Add("Origin","https://portal.example.com");request.Headers.Add("Access-Control-Request-Method","POST");}
            using var response=await f.Clients[0].SendAsync(request);Assert.Equal(status,(int)response.StatusCode);Assert.False(budget.TryAcquire(out _));
        }
        finally{foreach(var lease in held)lease.Dispose();}
    }
    private sealed class RejectRate : WebApi.Gateway.Policies.IRateLimitStore
    {public ValueTask<WebApi.Gateway.Policies.RateLimitDecision> TakeAsync(WebApi.Gateway.Policies.RateLimitRequest request,CancellationToken ct)=>ValueTask.FromResult(new WebApi.Gateway.Policies.RateLimitDecision(WebApi.Gateway.Policies.RateLimitDecisionKind.Exceeded,1));}
    [Fact]
    public async Task JsonRequest_LkgOverNodeLimit_FallsBackToOlderCompatibleBytes()
    {
        var limit=2048;await using var f=Echo(b=>b.Configuration["GatewayPolicies:Transform:MaxBodyBytes"]=limit.ToString());await f.InitializeAsync();var original=await f.PublishAsync();await f.ApplyBothAsync(original);await TransformPipelineTests.BindAsync(f,Config(2048));var network=await f.PublishAsync();await f.ApplyBothAsync(network);
        limit=1024;await f.RestartAsync(0);var current=f.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!;Assert.Equal(original.Envelope,current.Envelope);Assert.Equal(original.Payload,current.Payload.ToArray());Assert.Empty(current.Transforms);using var response=await f.RequestAsync();response.EnsureSuccessStatusCode();
    }
    [Theory][InlineData("GET")][InlineData("POST")][InlineData("PUT")][InlineData("PATCH")]
    public async Task JsonRequest_MappedBody_DoesNotEnableAutomaticRetry(string method)
    {
        var hits=0;await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref hits);using var reader=new StreamReader(ctx.Request.Body);var body=await reader.ReadToEndAsync(ctx.RequestAborted);ctx.Response.StatusCode=503;await ctx.Response.WriteAsJsonAsync(new{body});})};await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/orders",method);await TransformPipelineTests.BindAsync(f,Config(methods:[method]));
        await using(var db=f.Control.Context()){var p=new WebApi.Infrastructure.Persistence.Entities.Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="BodyRetry",Type="retry",Config="{\"maxAttempts\":3,\"perAttemptTimeoutMs\":1000,\"baseDelayMs\":0,\"maxDelayMs\":0,\"jitterPercent\":0,\"retryStatusCodes\":[503],\"retryConnectionFailures\":false}"};db.Add(p);db.Add(new WebApi.Infrastructure.Persistence.Entities.RoutePolicyBinding{RouteId=f.RouteId,PolicyId=p.Id,Priority=2});await db.SaveChangesAsync();}
        await f.ApplyBothAsync(await f.PublishAsync());using var request=Request(f,"{\"user_id\":123}"u8.ToArray(),method:method);using var response=await f.Clients[0].SendAsync(request);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);var body=(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("body").GetString()!;Assert.Contains("customer_id",body);Assert.Equal(1,hits);
    }

    [Fact]
    public async Task JsonRequest_SlowIncompleteUpload_RespectsRouteTimeoutAndRestoresBudget()
    {
        var hits=0;var sink=new RecordingTelemetrySink();await using var f=new GatewayFixture{ConfigureGateway=b=>{b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:BatchDelayMs"]="20";b.Services.AddSingleton<WebApi.Gateway.Observability.ITelemetryBatchSink>(sink);},ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{Interlocked.Increment(ref hits);await next(ctx);})};await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/orders","POST",1000);await TransformPipelineTests.BindAsync(f,Config());await f.ApplyBothAsync(await f.PublishAsync());
        var target=new Uri(GatewayFixture.Url(f.Gateways[0]));using var socket=new System.Net.Sockets.TcpClient();await socket.ConnectAsync(target.Host,target.Port);await using var stream=socket.GetStream();
        var wire=Encoding.ASCII.GetBytes($"POST /orders HTTP/1.1\r\nHost: {target.Host}\r\nX-API-Key: {f.Credential}\r\nContent-Type: application/json\r\nContent-Length: 100\r\nConnection: close\r\n\r\n{{\"user_id\":");await stream.WriteAsync(wire);
        var budget=f.Gateways[0].Services.GetRequiredService<BodyTransformBudget>();var entered=false;
        for(var attempt=0;attempt<100;attempt++){var held=new List<BodyTransformBudget.Lease>();while(held.Count<16&&budget.TryAcquire(out var lease))held.Add(lease!);entered=held.Count==15;foreach(var lease in held)lease.Dispose();if(entered)break;await Task.Delay(5);}Assert.True(entered);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));var response=new byte[4096];var count=await stream.ReadAsync(response,deadline.Token);Assert.StartsWith("HTTP/1.1 504",Encoding.ASCII.GetString(response,0,count));Assert.Equal(0,hits);
        var restored=false;for(var attempt=0;attempt<100;attempt++){var held=new List<BodyTransformBudget.Lease>();while(held.Count<16&&budget.TryAcquire(out var lease))held.Add(lease!);restored=held.Count==16;foreach(var lease in held)lease.Dispose();if(restored)break;await Task.Delay(10);}Assert.True(restored);await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal("Timeout",RecordingTelemetrySink.Attribute(sink.Logs[0],"webapi.outcome"));
    }

}

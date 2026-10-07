using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Tests.Support;
using Xunit;
using static WebApi.Gateway.Tests.Support.RecordingTelemetrySink;
using SigningKey=WebApi.Gateway.Tests.JwtTokenVerifierTests.KeyFixture;
namespace WebApi.Gateway.Tests;
public sealed class AdvancedPolicyTelemetryTests
{
    private static void Telemetry(GatewayFixture f,RecordingTelemetrySink sink)
    {var prior=f.ConfigureGateway;f.ConfigureGateway=b=>{prior?.Invoke(b);b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:TraceSampleRatio"]="1";b.Configuration["Observability:BatchDelayMs"]="20";b.Configuration["Observability:MetricExportIntervalMs"]="100";b.Services.AddSingleton<ITelemetryBatchSink>(sink);};}
    [Fact]public async Task ThreeAttemptsAreThreeClientSpansOneRequestMetric()
    {
        var calls=0;var measurements=new ConcurrentQueue<(string Name,long Value)>();var attemptTags=new ConcurrentQueue<string[]>();var sink=new RecordingTelemetrySink();await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(c,next)=>{if(c.Request.Path=="/health"){await next();return;}var n=Interlocked.Increment(ref calls);c.Response.StatusCode=n<3?503:200;await c.Response.WriteAsync("attempt-"+n);})};Telemetry(f,sink);
        using var listener=new MeterListener();listener.InstrumentPublished=(i,l)=>{if(i.Meter.Name.StartsWith("WebApi.Gateway."+f.Control.Environment.Id.ToString("N"),StringComparison.Ordinal)&&i.Name is "webapi_gateway_requests_total" or "webapi_gateway_forward_attempts_total")l.EnableMeasurementEvents(i);};listener.SetMeasurementEventCallback<long>((i,v,t,s)=>{measurements.Enqueue((i.Name,v));if(i.Name=="webapi_gateway_forward_attempts_total"){var keys=new List<string>();foreach(var tag in t)keys.Add(tag.Key);attemptTags.Enqueue(keys.Order().ToArray());}});listener.Start();
        await f.InitializeAsync();await RetryPipelineTests.Bind(f,RetryPipelineTests.Config());await f.ApplyBothAsync(await f.PublishAsync());using var r=await f.RequestAsync();Assert.Equal("attempt-3",await r.Content.ReadAsStringAsync());await sink.WaitAsync(()=>sink.Logs.Length==1&&sink.Items.Any(x=>x.Signal=="traces"));
        Assert.Equal(3,calls);Assert.Equal(1,measurements.Where(x=>x.Name=="webapi_gateway_requests_total").Sum(x=>x.Value));Assert.Equal(3,measurements.Where(x=>x.Name=="webapi_gateway_forward_attempts_total").Sum(x=>x.Value));Assert.Equal("3",Attribute(sink.Logs[0],"webapi.attempt.count"));Assert.All(attemptTags,t=>Assert.Equal(new[]{"webapi.environment.id","webapi.node.name","webapi.policy.decision","webapi.policy.id","webapi.policy.type"},t));
        var trace=sink.Logs[0].GetProperty("traceId").GetString();var clients=sink.Items.Where(x=>x.Signal=="traces"&&x.Item.GetProperty("traceId").GetString()==trace&&x.Item.GetProperty("kind").GetInt32()==3).Select(x=>x.Item).OrderBy(x=>Attribute(x,"webapi.attempt.number")).ToArray();Assert.Equal(3,clients.Length);Assert.Equal(new[]{"503","503","200"},clients.Select(x=>Attribute(x,"http.response.status_code")));Assert.Equal(new[]{2,2,1},clients.Select(x=>x.GetProperty("status").GetProperty("code").GetInt32()));Assert.All(clients,x=>Assert.Equal(f.Control.Destination.Id.ToString(),Attribute(x,"webapi.destination.id")));
    }
    [Fact]public async Task CacheHitNoClientSpanAndFiveDecisionsSurvive()
    {
        using var key=new SigningKey();var sink=new RecordingTelemetrySink();await using var s=new CachePipelineFixture{Respond=async(c,n)=>{c.Response.StatusCode=n<3?503:200;await c.Response.WriteAsync("final-"+n);}};Telemetry(s.F,sink);await s.Init(circuit:true,rate:true,jwt:key);await RetryPipelineTests.Bind(s.F,RetryPipelineTests.Config());await s.F.ApplyBothAsync(await s.F.PublishAsync());
        var firstToken=CachePipelineFixture.Token(key,"subject-private-canary");var refreshedToken=CachePipelineFixture.Token(key,"subject-private-canary", "refreshed");
        using var fill=await s.Get(token:firstToken);Assert.Equal("final-3",await fill.Content.ReadAsStringAsync());await s.WaitWrites(1);using var hit=await s.Get(1,token:refreshedToken);Assert.Equal("final-3",await hit.Content.ReadAsStringAsync());await sink.WaitAsync(()=>sink.Logs.Length==2&&sink.Items.Count(x=>x.Signal=="traces")>=2);
        var second=Assert.Single(sink.Logs,x=>Attribute(x,"webapi.request.id")==hit.Headers.GetValues("X-WebApi-Trace-Id").Single());Assert.Equal("0",Attribute(second,"webapi.attempt.count"));Assert.Equal("Hit",Attribute(second,"webapi.cache.disposition"));Assert.Equal("None",Attribute(second,"webapi.destination.id"));foreach(var type in new[]{"authentication","rate_limit","cache","circuit_breaker","retry"})Assert.NotNull(Attribute(second,"webapi.policy."+type+".decision"));Assert.Equal("CacheSkipped",Attribute(second,"webapi.policy.circuit_breaker.decision"));Assert.Equal("Bypass",Attribute(second,"webapi.policy.retry.decision"));Assert.Equal("cache_hit",Attribute(second,"webapi.policy.retry.rejection_reason"));
        var trace=second.GetProperty("traceId").GetString();Assert.DoesNotContain(sink.Items,x=>x.Signal=="traces"&&x.Item.GetProperty("traceId").GetString()==trace&&x.Item.GetProperty("kind").GetInt32()==3);Assert.Equal(3,s.Calls);
        var first=Assert.Single(sink.Logs,x=>Attribute(x,"webapi.cache.disposition")=="Stored");Assert.Equal("Miss",Attribute(first,"webapi.policy.cache.cache_read"));Assert.Equal("Stored",Attribute(first,"webapi.policy.cache.cache_write"));Assert.Equal("Retried",Attribute(first,"webapi.policy.retry.decision"));
        var all=string.Join('\n',sink.Items.Select(x=>x.Item.GetRawText()));Assert.DoesNotContain("subject-private-canary",all);Assert.DoesNotContain("private-app-canary-8362",all);Assert.DoesNotContain(firstToken,all);Assert.DoesNotContain(refreshedToken,all);Assert.DoesNotContain(File.ReadAllText(Path.Combine(s.F.Directory,"cache.secret")).Trim(),all);Assert.DoesNotContain(":{cache}:",all);
        using var mux=await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync("redis:6379");foreach(var cacheKey in mux.GetServer(mux.GetEndPoints().Single()).Keys(pattern:s.Prefix+":{cache}:entry:*"))Assert.DoesNotContain(cacheKey.ToString().Split(':')[^1],all);
    }
    [Fact]public async Task RejectedJwtRecordsOnlyFinalSafeDecisionAndNoAttempt()
    {
        using var key=new SigningKey();var sink=new RecordingTelemetrySink();await using var s=new CachePipelineFixture();Telemetry(s.F,sink);await s.Init(jwt:key);
        using var response=await s.Get(token:"private-token-canary-invalid");Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal("Rejected",Attribute(sink.Logs[0],"webapi.policy.authentication.decision"));Assert.Equal("invalid_jwt",Attribute(sink.Logs[0],"webapi.policy.authentication.rejection_reason"));Assert.Equal("0",Attribute(sink.Logs[0],"webapi.attempt.count"));Assert.Equal(0,s.Calls);Assert.DoesNotContain("private-token-canary-invalid",string.Join('\n',sink.Items.Select(x=>x.Item.GetRawText())));
    }
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task AttemptTimeoutUsesTimeoutCircuitSettingAndFinalObservation(bool countTimeouts,bool countConnections)
    {
        var calls=0;var sink=new RecordingTelemetrySink();
        await using var f=new GatewayFixture{ConfigureBackendA=a=>a.Use(async(c,next)=>{if(c.Request.Path=="/health"){await next();return;}Interlocked.Increment(ref calls);await Task.Delay(700,c.RequestAborted);await next();})};Telemetry(f,sink);
        await f.InitializeAsync();await RetryPipelineTests.Bind(f,RetryPipelineTests.Config(perAttempt:100),timeout:5000,circuit:true);
        await using(var db=f.Control.Context()){
            var policy=Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Set<WebApi.Infrastructure.Persistence.Entities.Policy>(),p=>p.Type=="circuit_breaker");
            var p=await policy;p.Config=JsonSerializer.Serialize(new {samplingWindowMs=30000,minimumRequests=1,failureRatio=1,openDurationMs=30000,halfOpenMaxRequests=1,halfOpenSuccesses=1,failureStatusCodes=new[]{503},countTimeouts,countConnectionFailures=countConnections});await db.SaveChangesAsync();
        }
        await f.ApplyBothAsync(await f.PublishAsync());using(var first=await f.RequestAsync())Assert.Equal(HttpStatusCode.GatewayTimeout,first.StatusCode);
        await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal("Timeout",Attribute(sink.Logs[0],"webapi.outcome"));Assert.Equal("1",Attribute(sink.Logs[0],"webapi.attempt.count"));
        using var second=await f.RequestAsync();Assert.Equal(countTimeouts?HttpStatusCode.ServiceUnavailable:HttpStatusCode.GatewayTimeout,second.StatusCode);Assert.Equal(countTimeouts?1:2,calls);
    }

}

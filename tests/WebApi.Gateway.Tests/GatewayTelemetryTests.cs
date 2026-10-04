using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApi.Gateway;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Tests.Support;
using WebApi.TestBackend;
using WebApi.Gateway.Configuration;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;
using Xunit;
using static WebApi.Gateway.Tests.Support.RecordingTelemetrySink;
namespace WebApi.Gateway.Tests;
public sealed class GatewayTelemetryTests
{
    [Theory][InlineData(null,true)][InlineData("00",false)][InlineData("01",true)]
    public async Task RootSamplingIgnoresUnrecordedHostingActivityButHonorsRemoteDecision(string? flags,bool expected)
    {
        var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);await s.InitializeAsync();
        var services=s.Gateways[0].Services;var context=new DefaultHttpContext();context.Response.Body=new MemoryStream();
        if(flags is not null)context.Request.Headers["traceparent"]="00-0123456789abcdef0123456789abcdef-0123456789abcdef-"+flags;
        using var hosting=new System.Diagnostics.Activity("unrecorded-host-request").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();hosting.ActivityTraceFlags=System.Diagnostics.ActivityTraceFlags.None;
        bool? recorded=null;System.Diagnostics.Activity? server=null;var middleware=new RequestTelemetryMiddleware(ctx=>{server=((RequestTelemetryState)ctx.Items[RequestTelemetryState.Item]!).ServerActivity;recorded=server?.Recorded??false;ctx.Response.StatusCode=200;return Task.CompletedTask;});
        await middleware.InvokeAsync(context,services.GetRequiredService<GatewayTelemetryRecorder>(),services.GetRequiredService<TelemetrySanitizer>(),services.GetRequiredService<GatewaySettings>(),services.GetRequiredService<TelemetryDropTracker>());
        Assert.Equal(expected,recorded);
        if(flags is null){Assert.NotNull(server);Assert.Equal(default,server.ParentSpanId);Assert.NotEqual(hosting.TraceId,server.TraceId);Assert.Same(hosting,System.Diagnostics.Activity.Current);}
    }
    private static GatewayFixture Fixture(RecordingTelemetrySink sink,int capacity=2048)
    {
        var s=new GatewayFixture();s.ConfigureGateway=b=>{b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:QueueCapacity"]=capacity.ToString();b.Configuration["Observability:TraceSampleRatio"]="1";b.Configuration["Observability:BatchDelayMs"]="20";b.Configuration["Observability:MetricExportIntervalMs"]="100";b.Services.AddSingleton<ITelemetryBatchSink>(sink);};return s;
    }
    [Fact] public async Task LateOldRequestKeepsOldSequenceAndDestination()
    {
        var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);await s.InitializeAsync();var first=await s.PublishAsync();await s.ApplyBothAsync(first);
        var probe=s.BackendA.Services.GetRequiredService<BackendProbe>();probe.HoldNext();var old=s.RequestAsync();await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));
        try{var second=await s.PublishAsync(1);await s.ApplyBothAsync(second);using var current=await s.RequestAsync();Assert.Equal("B",(await current.Content.ReadFromJsonAsync<BackendResponse>())!.BackendId);}
        finally{probe.Release();}
        using var previous=await old;var body=(await previous.Content.ReadFromJsonAsync<BackendResponse>())!;Assert.Equal("A",body.BackendId);
        await sink.WaitAsync(()=>sink.Logs.Length>=2);var log=Assert.Single(sink.Logs,x=>Attribute(x,"webapi.request.id")==body.TraceId);
        Assert.Equal(first.Envelope.DeploymentSequence.ToString(),Attribute(log,"webapi.deployment.sequence"));
        Assert.Equal(first.Envelope.ConfigVersion.ToString(),Attribute(log,"webapi.config.version"));
        Assert.Equal(s.Control.Destination.Id.ToString(),Attribute(log,"webapi.destination.id"));
    }
    [Fact] public async Task InvalidCredentialUsesUnknownApp()
    {
        var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());
        using var response=await s.RequestAsync(key:s.Credential.Split('.')[0]+".invalid-synthetic-secret");Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        var error=await response.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(32,error.GetProperty("w3cTraceId").GetString()!.Length);
        await sink.WaitAsync(()=>sink.Logs.Length==1);Assert.Equal("Unknown",Attribute(sink.Logs[0],"webapi.application.id"));Assert.Equal("None",Attribute(sink.Logs[0],"webapi.destination.id"));
        Assert.Equal(error.GetProperty("traceId").GetString(),Attribute(sink.Logs[0],"webapi.request.id"));
        Assert.Equal(error.GetProperty("w3cTraceId").GetString(),sink.Logs[0].GetProperty("traceId").GetString());
    }
    [Fact] public async Task Aborted200HeadersNotCountedSuccessful()
    {
        var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);s.ConfigureBackendA=app=>app.Use(async(ctx,next)=>{
            if(ctx.Request.Path!="/orders"){await next();return;}ctx.Response.StatusCode=200;await ctx.Response.WriteAsync("streaming");await ctx.Response.Body.FlushAsync();
            try{await Task.Delay(Timeout.Infinite,ctx.RequestAborted);}catch(OperationCanceledException){}
        });await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());
        using var cancel=new CancellationTokenSource();using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Add("X-API-Key",s.Credential);
        using var response=await s.Clients[0].SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancel.Token);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var read=response.Content.ReadAsStringAsync(cancel.Token);cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await read);
        await sink.WaitAsync(()=>sink.Logs.Any(x=>Attribute(x,"webapi.outcome")=="ClientAborted"));
        var log=Assert.Single(sink.Logs);Assert.Equal("200",Attribute(log,"http.response.status_code"));Assert.Equal("false",Attribute(log,"webapi.success"));
    }
    [Fact] public async Task SensitiveMarkersAbsentFromAllSignals()
    {
        const string marker="synthetic-sensitive-marker-987";var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());
        using var request=new HttpRequestMessage(HttpMethod.Get,"/orders?token="+marker);request.Headers.Add("X-API-Key",s.Credential);request.Headers.Add("Authorization","Bearer "+marker);request.Headers.Add("Cookie","session="+marker);request.Headers.Add("X-Forwarded-For","203.0.113.99");
        using var response=await s.Clients[0].SendAsync(request);response.EnsureSuccessStatusCode();await sink.WaitAsync(()=>sink.Logs.Length==1&&sink.Items.Any(x=>x.Signal=="traces")&&sink.Items.Any(x=>x.Signal=="metrics"));
        var all=string.Join('\n',sink.Items.Select(x=>x.Item.GetRawText()));Assert.DoesNotContain(marker,all);Assert.DoesNotContain(s.Credential,all);Assert.DoesNotContain("203.0.113.99",all);Assert.DoesNotContain("http://",all);
        Assert.Equal("/orders",Attribute(sink.Logs[0],"url.template"));Assert.Equal("127.0.0.xxx",Attribute(sink.Logs[0],"webapi.client.ip_masked"));
    }
    [Fact] public async Task QueueFullDoesNotBlockProxy()
    {
        var sink=new RecordingTelemetrySink{Hold=true};await using var s=Fixture(sink,2);await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());
        try{for(var i=0;i<20;i++){using var r=await s.RequestAsync();r.EnsureSuccessStatusCode();}var drops=s.Gateways[0].Services.GetRequiredService<TelemetryDropTracker>();Assert.True(drops.DroppedCount>0);}
        finally{sink.Release();}
    }
    [Fact] public async Task HealthRequestsExcluded()
    {
        var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());
        using(var live=await s.Clients[0].GetAsync("/health/live"))live.EnsureSuccessStatusCode();using(var ready=await s.Clients[0].GetAsync("/health/ready"))ready.EnsureSuccessStatusCode();
        using var business=await s.RequestAsync(path:"/health/orders");Assert.Equal(HttpStatusCode.NotFound,business.StatusCode);await sink.WaitAsync(()=>sink.Logs.Length==1);
        Assert.Equal("Unmatched",Attribute(sink.Logs[0],"webapi.api.id"));Assert.Equal("[unmatched]",Attribute(sink.Logs[0],"url.template"));
    }
    [Fact] public async Task ExportFailureIsCountedAndLaterRequestsRecover()
    {
        var sink=new RecordingTelemetrySink{Fail=true};await using var s=Fixture(sink);await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());
        using(var first=await s.RequestAsync())first.EnsureSuccessStatusCode();var tracker=s.Gateways[0].Services.GetRequiredService<TelemetryDropTracker>();
        await sink.WaitAsync(()=>tracker.ExportFailedCount>0);sink.Fail=false;
        using(var later=await s.RequestAsync())later.EnsureSuccessStatusCode();await sink.WaitAsync(()=>sink.Logs.Length>0&&tracker.LastSuccess.Any(x=>x.Key=="logs"));
        Assert.True(tracker.ExportFailedCount>0);Assert.Equal("Completed",Attribute(sink.Logs.Last(),"webapi.outcome"));
    }
    [Fact] public async Task HealthObserverReadsActualYarpStateUsingManagementIds()
    {
        var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());
        var services=s.Gateways[0].Services;var generation=services.GetRequiredService<RuntimeGenerationStore>().Current!;
        var runtime=Assert.Single(generation.Snapshot.Clusters);var lookup=services.GetRequiredService<IProxyStateLookup>();
        Assert.True(lookup.TryGetCluster(generation.Envelope.DeploymentSequence+":"+runtime.Id,out var cluster));
        var destination=cluster!.Destinations[s.Control.Destination.Id.ToString()];destination.Health.Active=DestinationHealth.Unhealthy;
        var observed=Assert.Single(services.GetRequiredService<GatewayHealthObserver>().Read());
        Assert.Equal("Unhealthy",observed.Health);Assert.Equal(s.Control.Cluster.Id,observed.ClusterId);Assert.Equal(s.Control.Environment.Id,observed.EnvironmentId);Assert.Equal(s.Control.Destination.Id,observed.DestinationId);
        destination.Health.Active=DestinationHealth.Unknown;destination.Health.Passive=DestinationHealth.Unknown;
        Assert.Equal("Unknown",Assert.Single(services.GetRequiredService<GatewayHealthObserver>().Read()).Health);
    }
    [Fact] public async Task CollectorResponseIsBoundedBeforeReadingPayload()
    {
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();await using var app=builder.Build();
        app.MapPost("/v1/logs",()=>Results.Json(new{padding=new string('x',70000)}));await app.StartAsync();
        var settings=new TelemetrySettings(true,new Uri(GatewayFixture.Url(app)),null,null,1,2048,20,100);
        var gateway=new GatewaySettings(Guid.NewGuid(),"synthetic-node","unused","unused",new Uri("http://127.0.0.1"),false,Guid.NewGuid());
        using var sink=new OtlpJsonTelemetrySink(settings,gateway);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>sink.ExportAsync("logs",[JsonSerializer.SerializeToElement(new{body=new{stringValue="synthetic-test"}})],CancellationToken.None));
    }
    [Fact] public async Task EmptyDestinationPoolDoesNotFabricateClientSpan()
    {
        var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());
        var services=s.Gateways[0].Services;var generation=services.GetRequiredService<RuntimeGenerationStore>().Current!;var runtime=Assert.Single(generation.Snapshot.Clusters);
        Assert.True(services.GetRequiredService<IProxyStateLookup>().TryGetCluster(generation.Envelope.DeploymentSequence+":"+runtime.Id,out var cluster));
        cluster!.DestinationsState=new ClusterDestinationsState(cluster.Destinations.Values.ToArray(),[]);
        using var response=await s.RequestAsync();Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);await sink.WaitAsync(()=>sink.Logs.Length==1);
        Assert.Equal("None",Attribute(sink.Logs[0],"webapi.destination.id"));
        Assert.DoesNotContain(sink.Items,x=>x.Signal=="traces"&&x.Item.GetProperty("kind").GetInt32()==3);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
using static WebApi.Gateway.Tests.Support.RecordingTelemetrySink;
namespace WebApi.Gateway.Tests;
public sealed class NetworkPolicyCacheTests
{
    private static void Telemetry(CachePipelineFixture fixture,RecordingTelemetrySink sink)
    {var prior=fixture.F.ConfigureGateway;fixture.F.ConfigureGateway=b=>{prior?.Invoke(b);b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:TraceSampleRatio"]="1";b.Configuration["Observability:BatchDelayMs"]="20";b.Services.AddSingleton<ITelemetryBatchSink>(sink);};}
    private static async Task<HttpResponseMessage> Get(CachePipelineFixture fixture,string? origin,string path="/orders",int node=0)
    {using var request=new HttpRequestMessage(HttpMethod.Get,path);request.Headers.Host="api.example.test";request.Headers.Add("X-API-Key",fixture.F.Credential);if(origin is not null)request.Headers.Add("Origin",origin);return await fixture.F.Clients[node].SendAsync(request);}
    private static string AppendConfig(bool headers=true)
    {var root=JsonNode.Parse(JsonResponseBodyPipelineTests.Config(headers:headers,operations:"[{\"op\":\"add\",\"path\":\"/items/-\",\"value\":\"mapped\"}]"))!;return root.ToJsonString();}
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Cache_CorsOriginsAndNoOrigin_AreSeparateFinalJsonPartitions(bool wildcard)
    {
        var sink=new RecordingTelemetrySink();await using var fixture=new CachePipelineFixture();Telemetry(fixture,sink);fixture.Respond=async(ctx,n)=>{ctx.Response.Headers.Vary="Origin";await ctx.Response.WriteAsJsonAsync(new{items=new[]{"original"},origin=ctx.Request.Headers.Origin.ToString(),backend=n});};await fixture.Init();var policy=await CorsPipelineTests.BindAsync(fixture.F,wildcard:wildcard);
        if(!wildcard){await using var db=fixture.F.Control.Context();var saved=await db.Set<Policy>().SingleAsync(p=>p.Id==policy.Id);var config=JsonNode.Parse(saved.Config)!;config["allowedOrigins"]=JsonSerializer.SerializeToNode(new[]{"https://portal.example.com","https://partner.example.com"});saved.Config=config.ToJsonString();await db.SaveChangesAsync();}
        await TransformPipelineTests.BindAsync(fixture.F,AppendConfig());await fixture.F.ApplyBothAsync(await fixture.F.PublishAsync());
        var partitions=0;
        foreach(var origin in new[]{"https://portal.example.com","https://partner.example.com",null})
        {
            byte[]? first=null;
            for(var read=0;read<2;read++)
            {using var response=await Get(fixture,origin,node:read);response.EnsureSuccessStatusCode();var bytes=await response.Content.ReadAsByteArrayAsync();if(first is null)first=bytes;else Assert.Equal(first,bytes);using var json=JsonDocument.Parse(bytes);Assert.Equal(new[]{"original","mapped"},json.RootElement.GetProperty("items").EnumerateArray().Select(x=>x.GetString()));Assert.Equal(origin??"",json.RootElement.GetProperty("origin").GetString());Assert.Equal("inventory",response.Headers.GetValues("X-Upstream-Service").Single());Assert.Contains("Origin",response.Headers.Vary);if(origin is null)Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));else Assert.Equal(wildcard?"*":origin,response.Headers.GetValues("Access-Control-Allow-Origin").Single());Assert.Equal(bytes.Length,response.Content.Headers.ContentLength);if(read==0)await fixture.WaitWrites(++partitions);}
        }
        await sink.WaitAsync(()=>sink.Logs.Length==6);Assert.Equal(3,fixture.Calls);Assert.Equal(3,fixture.Stores.Sum(s=>s.Writes));Assert.Equal(3,sink.Logs.Count(l=>Attribute(l,"webapi.cache.disposition")=="Hit"));Assert.All(sink.Logs,l=>Assert.Equal("Applied",Attribute(l,"webapi.policy.transform.decision")));var all=string.Join("\n",sink.Items.Select(i=>i.Item.GetRawText()));Assert.DoesNotContain("https://portal.example.com",all);Assert.DoesNotContain("https://partner.example.com",all);
    }
    [Fact]
    public async Task Cache_UnsupportedOtherVary_IsStillBypassWithCorsPartition()
    {await using var fixture=new CachePipelineFixture();fixture.Respond=async(ctx,n)=>{ctx.Response.Headers.Vary="Origin, X-Unconfigured";await ctx.Response.WriteAsJsonAsync(new{items=new[]{"original"},backend=n});};await fixture.Init();await CorsPipelineTests.BindAsync(fixture.F);await TransformPipelineTests.BindAsync(fixture.F,AppendConfig());await fixture.F.ApplyBothAsync(await fixture.F.PublishAsync());for(var i=0;i<2;i++){using var response=await Get(fixture,"https://portal.example.com");response.EnsureSuccessStatusCode();Assert.Contains("X-Unconfigured",response.Headers.Vary);}Assert.Equal(2,fixture.Calls);Assert.All(fixture.Stores,s=>Assert.Equal(0,s.Writes));}
    [Fact]
    public async Task Cache_RequestOnlyTransform_HitBypassesAndRejectedPlanNeverReadsStore()
    {
        var sink=new RecordingTelemetrySink();await using var fixture=new CachePipelineFixture();Telemetry(fixture,sink);fixture.Respond=async(ctx,n)=>await ctx.Response.WriteAsJsonAsync(new{path=ctx.Request.Path.Value,query=ctx.Request.QueryString.Value,partner=ctx.Request.Headers["X-Partner-Version"].ToString()});await fixture.Init();await TransformPipelineTests.RouteAsync(fixture.F,"/{**path}");var root=JsonNode.Parse(TransformPipelineTests.Config("/orders","/internal",true))!;root["responseHeaders"]=new JsonArray();await TransformPipelineTests.BindAsync(fixture.F,root.ToJsonString());await fixture.F.ApplyBothAsync(await fixture.F.PublishAsync());
        for(var i=0;i<2;i++){using var response=await Get(fixture,null);response.EnsureSuccessStatusCode();var json=(await response.Content.ReadFromJsonAsync<JsonElement>());Assert.Equal("/internal/",json.GetProperty("path").GetString());Assert.Equal("v2",json.GetProperty("partner").GetString());}
        await sink.WaitAsync(()=>sink.Logs.Length==2);Assert.Equal(1,fixture.Calls);var hit=Assert.Single(sink.Logs,l=>Attribute(l,"webapi.cache.disposition")=="Hit");Assert.Equal("Bypass",Attribute(hit,"webapi.policy.transform.decision"));Assert.Equal("cache_hit",Attribute(hit,"webapi.policy.transform.rejection_reason"));var reads=fixture.Stores.Sum(s=>s.Reads.Count);
        using var failed=await Get(fixture,null,"/other");Assert.Equal(HttpStatusCode.UnprocessableEntity,failed.StatusCode);Assert.Equal(reads,fixture.Stores.Sum(s=>s.Reads.Count));Assert.Equal(1,fixture.Calls);
    }
    [Fact]
    public async Task Cache_NetworkPolicyPublishAndRollback_AlwaysUseColdSequencePartitions()
    {
        await using var fixture=new CachePipelineFixture();fixture.Respond=async(ctx,n)=>{ctx.Response.Headers.Vary="Origin";await ctx.Response.WriteAsJsonAsync(new{items=new[]{"original"},backend=n});};await fixture.Init();await CorsPipelineTests.BindAsync(fixture.F);var policy=await TransformPipelineTests.BindAsync(fixture.F,AppendConfig());var original=await fixture.F.PublishAsync();await fixture.F.ApplyBothAsync(original);
        using(var first=await Get(fixture,"https://portal.example.com")){first.EnsureSuccessStatusCode();Assert.Contains("mapped",await first.Content.ReadAsStringAsync());}await fixture.WaitWrites(1);using(var hit=await Get(fixture,"https://portal.example.com",node:1)){hit.EnsureSuccessStatusCode();}Assert.Equal(1,fixture.Calls);
        await using(var db=fixture.F.Control.Context()){var saved=await db.Set<Policy>().SingleAsync(p=>p.Id==policy.Id);saved.Config=AppendConfig().Replace("\"mapped\"","\"new\"",StringComparison.Ordinal);saved.VersionNo++;await db.SaveChangesAsync();}
        var next=await fixture.F.PublishAsync();await fixture.F.ApplyBothAsync(next);using(var fresh=await Get(fixture,"https://portal.example.com",node:1)){fresh.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());Assert.Equal(new[]{"original","new"},json.RootElement.GetProperty("items").EnumerateArray().Select(i=>i.GetString()));Assert.Equal(2,json.RootElement.GetProperty("backend").GetInt32());}await fixture.WaitWrites(2);
        var rollback=await fixture.Rollback(next,original.Envelope.ConfigVersion);await fixture.F.ApplyBothAsync(rollback);using(var restored=await Get(fixture,"https://portal.example.com")){restored.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await restored.Content.ReadAsStringAsync());Assert.Equal(new[]{"original","mapped"},json.RootElement.GetProperty("items").EnumerateArray().Select(i=>i.GetString()));Assert.Equal(3,json.RootElement.GetProperty("backend").GetInt32());Assert.Equal(rollback.Envelope.DeploymentSequence.ToString(),restored.Headers.GetValues("X-WebApi-Deployment-Sequence").Single());}Assert.Equal(3,fixture.Calls);
    }

}

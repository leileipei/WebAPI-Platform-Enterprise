using System.Net;
using Microsoft.AspNetCore.Http;
using WebApi.Gateway.Tests.Support;
using Xunit;
using SigningKey = WebApi.Gateway.Tests.JwtTokenVerifierTests.KeyFixture;
namespace WebApi.Gateway.Tests;
public sealed class ResponseCachePipelineTests
{
    [Fact] public async Task NodeAFillsNodeBHitsWithFreshTraceAndAge()
    {
        await using var s=new CachePipelineFixture();await s.Init();using var first=await s.Get();Assert.Equal("business-data-1",await first.Content.ReadAsStringAsync());await s.WaitWrites(1);await Task.Delay(1100);
        using var second=await s.Get(1);Assert.Equal("business-data-1",await second.Content.ReadAsStringAsync());Assert.Equal(1,s.Calls);
        Assert.NotEqual(first.Headers.GetValues("X-WebApi-Trace-Id").Single(),second.Headers.GetValues("X-WebApi-Trace-Id").Single());Assert.NotEqual("upstream-private-trace",second.Headers.GetValues("X-WebApi-Trace-Id").Single());Assert.True(second.Headers.Age>=TimeSpan.FromSeconds(1));
        Assert.Equal(first.Headers.GetValues("X-WebApi-Deployment-Sequence"),second.Headers.GetValues("X-WebApi-Deployment-Sequence"));GatewayPlatformHeaderBoundaryTests.AssertPlatformResponse(first);GatewayPlatformHeaderBoundaryTests.AssertPlatformResponse(second);
    }
    [Theory][InlineData(false)][InlineData(true)]public async Task JwtUsersIsolatedAndBearerRefreshPartitionIsExplicit(bool forward)
    {
        using var key=new SigningKey();await using var s=new CachePipelineFixture();await s.Init(jwt:key,forward:forward);
        using var first=await s.Get(token:CachePipelineFixture.Token(key));Assert.Equal("business-data-1",await first.Content.ReadAsStringAsync());await s.WaitWrites(1);
        using var other=await s.Get(1,token:CachePipelineFixture.Token(key,"user-2"));Assert.Equal("business-data-2",await other.Content.ReadAsStringAsync());await s.WaitWrites(2);
        using var refresh=await s.Get(1,token:CachePipelineFixture.Token(key,nonce:"refresh"));Assert.Equal(forward?"business-data-3":"business-data-1",await refresh.Content.ReadAsStringAsync());Assert.Equal(forward?3:2,s.Calls);
        using var bad=await s.Get(1,token:"invalid");Assert.Equal(HttpStatusCode.Unauthorized,bad.StatusCode);
    }
    [Fact]public async Task OversizeContinuesStreamingWithoutCacheEntry()
    {
        await using var s=new CachePipelineFixture{Respond=async(c,n)=>{var bytes=new byte[2048];Array.Fill<byte>(bytes,42);for(var i=0;i<8;i++){await c.Response.BodyWriter.WriteAsync(bytes);await c.Response.BodyWriter.FlushAsync();}}};await s.Init(maxEntry:1024);
        for(var i=0;i<2;i++){using var response=await s.Get(i);Assert.Equal(16384,(await response.Content.ReadAsByteArrayAsync()).Length);}Assert.Equal(2,s.Calls);Assert.Equal(0,s.Stores.Sum(x=>x.Writes));
    }
    [Fact]public async Task TruncatedBodyNeverStores()
    {
        await using var s=new CachePipelineFixture{Respond=async(c,n)=>{c.Response.ContentLength=100;await c.Response.WriteAsync("partial");await c.Response.Body.FlushAsync();c.Abort();}};await s.Init();
        for(var i=0;i<2;i++){try{using var r=await s.Get(i);Assert.Equal(HttpStatusCode.BadGateway,r.StatusCode);}catch(HttpRequestException){}}Assert.Equal(2,s.Calls);Assert.Equal(0,s.Stores.Sum(x=>x.Writes));
    }
    [Fact]public async Task ClientAbortNeverStoresAndFirstChunkDoesNotWaitForCompletion()
    {
        var chunk=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var aborted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var s=new CachePipelineFixture{Respond=async(c,n)=>{if(n>1){await c.Response.WriteAsync("complete");return;}c.Response.ContentLength=10000;await c.Response.WriteAsync("first-chunk");await c.Response.Body.FlushAsync();chunk.TrySetResult();try{await Task.Delay(Timeout.Infinite,c.RequestAborted);}catch(OperationCanceledException){aborted.TrySetResult();}}};await s.Init();
        using var cancel=new CancellationTokenSource();using var first=await s.Get(ct:cancel.Token,option:HttpCompletionOption.ResponseHeadersRead);await chunk.Task.WaitAsync(TimeSpan.FromSeconds(5));var stream=await first.Content.ReadAsStreamAsync();var bytes=new byte[11];Assert.Equal(11,await stream.ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));cancel.Cancel();first.Dispose();await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,s.Stores.Sum(x=>x.Writes));using var next=await s.Get(1);Assert.Equal("complete",await next.Content.ReadAsStringAsync());Assert.Equal(2,s.Calls);
    }
    [Fact]public async Task PublishRollbackSequenceNeverReusesHistoricalCache()
    {
        await using var s=new CachePipelineFixture();await s.Init();using var first=await s.Get();Assert.Equal("business-data-1",await first.Content.ReadAsStringAsync());await s.WaitWrites(1);
        var second=await s.F.PublishAsync();await s.F.ApplyBothAsync(second);using var fresh=await s.Get(1);Assert.Equal("business-data-2",await fresh.Content.ReadAsStringAsync());await s.WaitWrites(2);
        var rollback=await s.Rollback(second,1);await s.F.ApplyBothAsync(rollback);using var restored=await s.Get();Assert.Equal("business-data-3",await restored.Content.ReadAsStringAsync());Assert.Equal(3,s.Calls);Assert.Equal("3",restored.Headers.GetValues("X-WebApi-Deployment-Sequence").Single());
    }
    [Fact]public async Task OriginAgeAndExpiryPreventStaleCrossNodeHit()
    {
        await using var s=new CachePipelineFixture{Respond=async(c,n)=>{c.Response.Headers.CacheControl="public,max-age=60";c.Response.Headers.Age="59";await c.Response.WriteAsync("age-"+n);}};await s.Init();
        using var first=await s.Get();Assert.Equal("age-1",await first.Content.ReadAsStringAsync());await s.WaitWrites(1);using var hit=await s.Get(1);Assert.Equal("age-1",await hit.Content.ReadAsStringAsync());Assert.True(hit.Headers.Age>=TimeSpan.FromSeconds(59));
        await Task.Delay(1100);using var expired=await s.Get(1);Assert.Equal("age-2",await expired.Content.ReadAsStringAsync());Assert.Equal(2,s.Calls);
    }
    [Theory][InlineData("private")][InlineData("no-store")][InlineData("no-cache")][InlineData("cookie")][InlineData("vary")][InlineData("304")][InlineData("gzip")][InlineData("sse")][InlineData("trailer")]
    public async Task UnsafeResponseAlwaysFlowsWithoutStoredEntry(string reason)
    {
        await using var s=new CachePipelineFixture{Respond=async(c,n)=>{
            if(reason is "private" or "no-store" or "no-cache")c.Response.Headers.CacheControl=reason+",max-age=60";
            if(reason=="cookie")c.Response.Headers.SetCookie="business=private";
            if(reason=="vary")c.Response.Headers.Vary="X-Unregistered";
            if(reason=="gzip")c.Response.Headers.ContentEncoding="gzip";
            if(reason=="sse")c.Response.ContentType="text/event-stream";
            if(reason=="trailer")c.Response.Headers.Trailer="X-Checksum";
            if(reason=="304"){c.Response.StatusCode=304;return;}await c.Response.WriteAsync("unshared-"+n);
        }};await s.Init();for(var i=0;i<2;i++){using var r=await s.Get(i);Assert.Equal(reason=="304"?HttpStatusCode.NotModified:HttpStatusCode.OK,r.StatusCode);if(reason!="304")Assert.Equal("unshared-"+(i+1),await r.Content.ReadAsStringAsync());}Assert.Equal(2,s.Calls);Assert.Equal(0,s.Stores.Sum(x=>x.Writes));
    }

}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class NetworkPolicyRetryTests
{
    private sealed class CountRate : IRateLimitStore
    {public int Calls;public ValueTask<RateLimitDecision> TakeAsync(RateLimitRequest request,CancellationToken ct){Interlocked.Increment(ref Calls);return ValueTask.FromResult(new RateLimitDecision(RateLimitDecisionKind.Allowed,1));}}
    [Fact]
    public async Task Retry_ThreePhysicalCopies_KeepOneRewriteAndOneRateCharge()
    {
        var rate=new CountRate();var calls=0;var seen=new System.Collections.Concurrent.ConcurrentQueue<(string Path,string Query,string Header)>();await using var f=new GatewayFixture{ConfigureGateway=b=>b.Services.AddSingleton<IRateLimitStore>(rate),ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{var n=Interlocked.Increment(ref calls);seen.Enqueue((ctx.Request.Path.Value!,ctx.Request.QueryString.Value!,ctx.Request.Headers["X-Partner-Version"].ToString()));ctx.Response.StatusCode=n<3?503:200;await ctx.Response.WriteAsJsonAsync(new{ok=true});})};await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/gateway/stock");await TransformPipelineTests.BindAsync(f,TransformPipelineTests.Config("/gateway","/internal/v2",true));await RetryPipelineTests.Bind(f,RetryPipelineTests.Config());await using(var db=f.Control.Context()){var p=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="Rate",Type="rate_limit",Config="{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":1,\"windowMs\":600000,\"burst\":3,\"redisFailureMode\":\"Reject\"}"};db.Add(p);db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=p.Id});await db.SaveChangesAsync();}await f.ApplyBothAsync(await f.PublishAsync());using var response=await f.RequestAsync(path:"/gateway/stock?sku=A&sku=B&debug=1");response.EnsureSuccessStatusCode();Assert.Equal(3,calls);Assert.Equal(1,rate.Calls);Assert.All(seen,item=>{Assert.Equal("/internal/v2/stock",item.Path);Assert.Equal("?sku=A&sku=B&region=cn",item.Query);Assert.Equal("v2",item.Header);});Assert.Equal("inventory",response.Headers.GetValues("X-Upstream-Service").Single());
    }
    [Fact]
    public async Task Retry_ActualHalfOpenProbe_IsOnePhysicalCopyWithSamePlanAndOneRateCharge()
    {
        var clock=new RetryPipelineTests.DelayClock();var rate=new CountRate();var calls=0;var seen=new System.Collections.Concurrent.ConcurrentQueue<(string Path,string Query,string Header)>();await using var f=new GatewayFixture{ConfigureGateway=b=>{b.Services.AddSingleton<TimeProvider>(clock);b.Services.AddSingleton<IRateLimitStore>(rate);},ConfigureBackendA=a=>a.Use(async(HttpContext ctx,RequestDelegate next)=>{var n=Interlocked.Increment(ref calls);seen.Enqueue((ctx.Request.Path.Value!,ctx.Request.QueryString.Value!,ctx.Request.Headers["X-Partner-Version"].ToString()));ctx.Response.StatusCode=n<=3?503:200;await ctx.Response.WriteAsJsonAsync(new{ok=true});})};await f.InitializeAsync();await TransformPipelineTests.RouteAsync(f,"/gateway/stock");await TransformPipelineTests.BindAsync(f,TransformPipelineTests.Config("/gateway","/internal/v2",true));await RetryPipelineTests.Bind(f,RetryPipelineTests.Config(),circuit:true);
        await using(var db=f.Control.Context()){var c=await db.Set<Policy>().SingleAsync(p=>p.Type=="circuit_breaker");c.Config="{\"samplingWindowMs\":30000,\"minimumRequests\":1,\"failureRatio\":1,\"openDurationMs\":1000,\"halfOpenMaxRequests\":1,\"halfOpenSuccesses\":1,\"failureStatusCodes\":[503],\"countTimeouts\":true,\"countConnectionFailures\":true}";var ratePolicy=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="Rate",Type="rate_limit",Config="{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":1,\"windowMs\":600000,\"burst\":3,\"redisFailureMode\":\"Reject\"}"};db.Add(ratePolicy);db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=ratePolicy.Id});await db.SaveChangesAsync();}await f.ApplyBothAsync(await f.PublishAsync());
        using(var first=await f.RequestAsync(path:"/gateway/stock?sku=A&sku=B&debug=1"))Assert.Equal(HttpStatusCode.ServiceUnavailable,first.StatusCode);Assert.Equal(3,calls);clock.Offset=2;using(var probe=await f.RequestAsync(path:"/gateway/stock?sku=A&sku=B&debug=1"))probe.EnsureSuccessStatusCode();Assert.Equal(4,calls);Assert.Equal(2,rate.Calls);Assert.All(seen,item=>{Assert.Equal("/internal/v2/stock",item.Path);Assert.Equal("?sku=A&sku=B&region=cn",item.Query);Assert.Equal("v2",item.Header);});
    }

}

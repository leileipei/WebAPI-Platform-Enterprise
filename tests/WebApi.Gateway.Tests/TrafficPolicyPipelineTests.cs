using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Domain.Policies;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.TestBackend;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class TrafficPolicyPipelineTests
{
    private const string Rate="{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":1,\"windowMs\":600000,\"burst\":3,\"redisFailureMode\":\"Reject\"}";
    private const string Circuit="{\"samplingWindowMs\":30000,\"minimumRequests\":2,\"failureRatio\":0.5,\"openDurationMs\":30000,\"halfOpenMaxRequests\":1,\"halfOpenSuccesses\":3,\"failureStatusCodes\":[503],\"countTimeouts\":true,\"countConnectionFailures\":true}";
    private sealed class RecordingLimiter : IRateLimitStore
    {public System.Collections.Concurrent.ConcurrentBag<RateLimitRequest> Requests=[];public RateLimitDecisionKind Result=RateLimitDecisionKind.Allowed;public ValueTask<RateLimitDecision> TakeAsync(RateLimitRequest request,CancellationToken ct) {Requests.Add(request);return ValueTask.FromResult(new RateLimitDecision(Result,1));}}
    private static GatewayFixture Fixture(RecordingLimiter limiter,Action<Microsoft.AspNetCore.Builder.WebApplication>? backend=null)=>new() {ConfigureGateway=b=>b.Services.AddSingleton<IRateLimitStore>(limiter),ConfigureBackendA=backend};
    private static async Task<Policy> Bind(GatewayFixture s,string type,string config)
    {await using var db=s.Control.Context();var p=new Policy {OrganizationId=s.Control.Organization.Id,ProjectId=s.Control.Project.Id,Name="流量策略",Type=type,Config=config};db.Add(p);db.Add(new RoutePolicyBinding {RouteId=s.RouteId,PolicyId=p.Id});(await db.Set<ApiRoute>().SingleAsync()).Revision++;await db.SaveChangesAsync();return p;}
    [Fact] public async Task TelemetryOffStillPartitionsByAuthenticatedApp()
    {var limiter=new RecordingLimiter();await using var s=Fixture(limiter);await s.InitializeAsync();await Bind(s,"rate_limit",Rate);await s.ApplyBothAsync(await s.PublishAsync());using var response=await s.RequestAsync();response.EnsureSuccessStatusCode();Assert.Equal(s.Control.Application.Id,Assert.Single(limiter.Requests).ApplicationId);Assert.Equal("ApplicationRoute",limiter.Requests.Single().Configuration.KeyBy);}
    [Fact] public async Task AuthRejectNeverConsumesOrTripsCircuit()
    {var limiter=new RecordingLimiter();var hits=0;await using var s=Fixture(limiter,a=>a.Use(async(ctx,next)=>{Interlocked.Increment(ref hits);await next();}));await s.InitializeAsync();await Bind(s,"rate_limit",Rate);await Bind(s,"circuit_breaker",Circuit);await s.ApplyBothAsync(await s.PublishAsync());using var denied=await s.RequestAsync(key:"invalid.invalid");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);Assert.Empty(limiter.Requests);Assert.Equal(0,hits);}
    [Fact] public async Task LimitedRequestNeverTouchesBackend()
    {var limiter=new RecordingLimiter {Result=RateLimitDecisionKind.Exceeded};var hits=0;await using var s=Fixture(limiter,a=>a.Use(async(ctx,next)=>{Interlocked.Increment(ref hits);await next();}));await s.InitializeAsync();await Bind(s,"rate_limit",Rate);await s.ApplyBothAsync(await s.PublishAsync());using var response=await s.RequestAsync();Assert.Equal(HttpStatusCode.TooManyRequests,response.StatusCode);Assert.Equal("1",response.Headers.GetValues("Retry-After").Single());Assert.Equal(0,hits);}
    [Theory] [InlineData("Reject",503,0)] [InlineData("Allow",200,1)]
    public async Task RedisRejectAndAllowProduceDifferentFacts(string mode,int status,int calls)
    {var limiter=new RecordingLimiter {Result=RateLimitDecisionKind.StoreUnavailable};var hits=0;await using var s=Fixture(limiter,a=>a.Use(async(ctx,next)=>{Interlocked.Increment(ref hits);await next();}));await s.InitializeAsync();await Bind(s,"rate_limit",Rate.Replace("Reject",mode));await s.ApplyBothAsync(await s.PublishAsync());using var response=await s.RequestAsync();Assert.Equal(status,(int)response.StatusCode);Assert.Equal(calls,hits);}
    [Fact] public async Task ConsumedTokenNotRefundedAfterCircuitReject()
    {var limiter=new RecordingLimiter();var hits=0;await using var s=Fixture(limiter,a=>a.Use(async(ctx,next)=>{Interlocked.Increment(ref hits);ctx.Response.StatusCode=503;await next();}));await s.InitializeAsync();await Bind(s,"rate_limit",Rate);await Bind(s,"circuit_breaker",Circuit);await s.ApplyBothAsync(await s.PublishAsync());for(var i=0;i<3;i++) {using var response=await s.RequestAsync();Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);}Assert.Equal(3,limiter.Requests.Count);Assert.Equal(2,hits);}
    [Fact] public async Task LateOldRequestUsesOldPolicyGeneration()
    {var limiter=new RecordingLimiter();await using var s=Fixture(limiter);await s.InitializeAsync();var policy=await Bind(s,"rate_limit",Rate);var first=await s.PublishAsync();await s.ApplyBothAsync(first);var probe=s.BackendA.Services.GetRequiredService<BackendProbe>();probe.HoldNext();var held=s.RequestAsync();await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));var oldId=limiter.Requests.Single().RuntimePolicyId;await using(var db=s.Control.Context()) {var p=await db.Set<Policy>().SingleAsync(x=>x.Id==policy.Id);p.Config=Rate.Replace("\"burst\":3","\"burst\":4");p.VersionNo++;await db.SaveChangesAsync();}var second=await s.PublishAsync(1);await s.ApplyBothAsync(second);using var current=await s.RequestAsync();current.EnsureSuccessStatusCode();Assert.Contains(limiter.Requests,r=>r.RuntimePolicyId!=oldId);probe.Release();using var previous=await held;Assert.Equal(first.Envelope.DeploymentSequence,(await previous.Content.ReadFromJsonAsync<BackendResponse>())!.DeploymentSequence);}
    [Fact] public void ClientAbortAndNoDestinationNotCountedAsUpstreamFailure()
    {var config=new CircuitBreakerConfiguration(30000,2,0.5,30000,1,3,[503],true,true);var context=new DefaultHttpContext();context.Response.StatusCode=503;Assert.Equal(CircuitOutcome.Cancelled,CircuitOutcomeClassifier.Classify(context,config,false));using var source=new CancellationTokenSource();source.Cancel();context.RequestAborted=source.Token;Assert.Equal(CircuitOutcome.Cancelled,CircuitOutcomeClassifier.Classify(context,config,true));}
    [Fact] public async Task RetiredGenerationKeepsCircuitUntilLastRequestReleases()
    {var limiter=new RecordingLimiter();await using var s=Fixture(limiter);await s.InitializeAsync();await Bind(s,"circuit_breaker",Circuit);var first=await s.PublishAsync();await s.ApplyBothAsync(first);var registry=s.Gateways[0].Services.GetRequiredService<CircuitStateRegistry>();Assert.Equal(1,registry.Count);var store=s.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>();using var lease=store.Acquire(first.Envelope.DeploymentSequence);await using(var db=s.Control.Context()) await db.Set<RoutePolicyBinding>().ExecuteDeleteAsync();var second=await s.PublishAsync();await s.ApplyBothAsync(second);Assert.Equal(1,registry.Count);lease.Dispose();Assert.Equal(0,registry.Count);}
    [Fact] public async Task UnchangedRuntimeIdentityHotPublishKeepsOpenCircuit()
    {var limiter=new RecordingLimiter();var hits=0;await using var s=Fixture(limiter,a=>a.Use(async(ctx,next)=>{Interlocked.Increment(ref hits);ctx.Response.StatusCode=503;await next();}));await s.InitializeAsync();await Bind(s,"circuit_breaker",Circuit);await s.ApplyBothAsync(await s.PublishAsync());for(var i=0;i<2;i++) {using var failed=await s.RequestAsync();Assert.Equal(HttpStatusCode.ServiceUnavailable,failed.StatusCode);}await s.ApplyBothAsync(await s.PublishAsync());using var denied=await s.RequestAsync();Assert.Equal(HttpStatusCode.ServiceUnavailable,denied.StatusCode);Assert.Contains("circuit_open",await denied.Content.ReadAsStringAsync());Assert.Equal(2,hits);}

}

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Gateway.Observability;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
using static WebApi.Gateway.Tests.Support.RecordingTelemetrySink;
namespace WebApi.Gateway.Tests;
public sealed class PolicyTelemetryTests
{
    private const string Rate="{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":1,\"windowMs\":600000,\"burst\":3,\"redisFailureMode\":\"Reject\"}";
    private const string Circuit="{\"samplingWindowMs\":30000,\"minimumRequests\":2,\"failureRatio\":0.5,\"openDurationMs\":30000,\"halfOpenMaxRequests\":1,\"halfOpenSuccesses\":3,\"failureStatusCodes\":[503],\"countTimeouts\":true,\"countConnectionFailures\":true}";
    private sealed class Limiter(RateLimitDecisionKind result) : IRateLimitStore {public ValueTask<RateLimitDecision> TakeAsync(RateLimitRequest request,CancellationToken ct)=>ValueTask.FromResult(new RateLimitDecision(result,1));}
    private static GatewayFixture Fixture(RecordingTelemetrySink sink,RateLimitDecisionKind result=RateLimitDecisionKind.Allowed)=>new() {ConfigureGateway=b=>{b.Configuration["Observability:Enabled"]="true";b.Configuration["Observability:TraceSampleRatio"]="1";b.Configuration["Observability:BatchDelayMs"]="20";b.Configuration["Observability:MetricExportIntervalMs"]="100";b.Configuration["TrafficPolicies:RedisConnection"]="redis:6379,password=synthetic-redis-secret";b.Services.AddSingleton<ITelemetryBatchSink>(sink);b.Services.AddSingleton<IRateLimitStore>(new Limiter(result));}};
    private static async Task<Policy> Bind(GatewayFixture s,string type,string config)
    {await using var db=s.Control.Context();var p=new Policy {OrganizationId=s.Control.Organization.Id,ProjectId=s.Control.Project.Id,Name="策略",Type=type,Config=config};db.Add(p);db.Add(new RoutePolicyBinding {RouteId=s.RouteId,PolicyId=p.Id});(await db.Set<ApiRoute>().SingleAsync()).Revision++;await db.SaveChangesAsync();return p;}
    [Fact] public async Task PolicyDecisionsVisibleWithoutSecrets()
    {var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);await s.InitializeAsync();var rate=await Bind(s,"rate_limit",Rate);var circuit=await Bind(s,"circuit_breaker",Circuit);await s.ApplyBothAsync(await s.PublishAsync());using var response=await s.RequestAsync();response.EnsureSuccessStatusCode();await sink.WaitAsync(()=>sink.Logs.Length==1&&sink.Items.Any(x=>x.Signal=="traces"&&Attribute(x.Item,"webapi.policy.rate_limit.id")==rate.Id.ToString()));var log=Assert.Single(sink.Logs);Assert.Equal(rate.Id.ToString(),Attribute(log,"webapi.policy.rate_limit.id"));Assert.Equal(circuit.Id.ToString(),Attribute(log,"webapi.policy.circuit_breaker.id"));Assert.Equal("Allowed",Attribute(log,"webapi.policy.rate_limit.decision"));Assert.Equal("1",Attribute(log,"webapi.policy.rate_limit.revision"));var signals=string.Join("\n",sink.Items.Select(x=>x.Item.GetRawText()));Assert.DoesNotContain(s.Credential,signals);Assert.DoesNotContain("synthetic-redis-secret",signals);Assert.DoesNotContain(":policy:",signals);}
    [Fact] public async Task Gateway503HasNoFakeDestination()
    {var sink=new RecordingTelemetrySink();await using var s=Fixture(sink,RateLimitDecisionKind.StoreUnavailable);await s.InitializeAsync();await Bind(s,"rate_limit",Rate);await s.ApplyBothAsync(await s.PublishAsync());using var response=await s.RequestAsync();Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);await sink.WaitAsync(()=>sink.Logs.Length==1);var log=sink.Logs[0];Assert.Equal("None",Attribute(log,"webapi.destination.id"));Assert.Equal("StoreRejected",Attribute(log,"webapi.policy.rate_limit.decision"));Assert.Equal("rate_limit_store_unavailable",Attribute(log,"webapi.policy.rate_limit.rejection_reason"));}
    [Fact] public async Task NoRevisionOrAppDimensionOnPolicyCounter()
    {var sink=new RecordingTelemetrySink();await using var s=Fixture(sink);await s.InitializeAsync();var p=await Bind(s,"rate_limit",Rate);await s.ApplyBothAsync(await s.PublishAsync());using var response=await s.RequestAsync();response.EnsureSuccessStatusCode();await sink.WaitAsync(()=>sink.Items.Any(x=>x.Signal=="metrics"&&x.Item.GetProperty("name").GetString()=="webapi_gateway_policy_decisions_total"));var metric=sink.Items.First(x=>x.Signal=="metrics"&&x.Item.GetProperty("name").GetString()=="webapi_gateway_policy_decisions_total").Item;var point=metric.GetProperty("sum").GetProperty("dataPoints")[0];var keys=point.GetProperty("attributes").EnumerateArray().Select(x=>x.GetProperty("key").GetString()).Order().ToArray();Assert.Equal(new[]{"webapi.environment.id","webapi.node.name","webapi.policy.decision","webapi.policy.id","webapi.policy.type"},keys);Assert.Equal(p.Id.ToString(),Attribute(point,"webapi.policy.id"));}
}

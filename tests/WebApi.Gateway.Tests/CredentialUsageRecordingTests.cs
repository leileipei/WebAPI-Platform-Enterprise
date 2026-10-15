using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.CredentialUsage;
using WebApi.Gateway.CredentialUsage;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Gateway.Tests;

public sealed class CredentialUsageRecordingTests
{
    private static (ICredentialUsageRecorder Recorder,Func<DateTimeOffset,IReadOnlyList<UsageObservation>> Drain,Func<DateTimeOffset,IReadOnlyList<UsageGap>> Gaps,Func<long> Dropped) Accumulator(bool enabled=true,int capacity=100)
    {
        var type=typeof(ICredentialUsageRecorder).Assembly.GetType("WebApi.Gateway.CredentialUsage.CredentialUsageAccumulator");Assert.NotNull(type);
        var recorder=Assert.IsAssignableFrom<ICredentialUsageRecorder>(Activator.CreateInstance(type,new CredentialUsageOptions(enabled,15,1000,capacity,8*1024*1024,1000,256*1024,24)));
        var drain=type.GetMethod("Drain");var gaps=type.GetMethod("DrainGaps");var dropped=type.GetProperty("DroppedCount");Assert.NotNull(drain);Assert.NotNull(gaps);Assert.NotNull(dropped);
        return(recorder,drain.CreateDelegate<Func<DateTimeOffset,IReadOnlyList<UsageObservation>>>(recorder),gaps.CreateDelegate<Func<DateTimeOffset,IReadOnlyList<UsageGap>>>(recorder),()=>(long)dropped.GetValue(recorder)!);
    }
    private static UsageObservation Observation()
    {
        var now=DateTimeOffset.Parse("2026-10-15T02:30:00Z");return new(Guid.NewGuid(),new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),now.AddHours(-1),now.AddHours(1),new(Guid.NewGuid(),1,1,new string('a',64)),new(2026,10,15,2,0,0,TimeSpan.Zero),1,now,now));
    }
    [Fact]
    public void Accumulator_sums_full_key_and_preserves_observed_extrema()
    {
        var s=Accumulator();var observation=Observation();Assert.True(s.Recorder.TryRecord(observation));var earlier=observation.Item.FirstObservedAt.AddSeconds(-1);var later=observation.Item.FirstObservedAt.AddSeconds(1);
        Assert.True(s.Recorder.TryRecord(observation with{Item=observation.Item with{FirstObservedAt=earlier,LastObservedAt=earlier}}));Assert.True(s.Recorder.TryRecord(observation with{Item=observation.Item with{FirstObservedAt=later,LastObservedAt=later}}));
        var total=Assert.Single(s.Drain(later));Assert.Equal(3,total.Item.Count);Assert.Equal(earlier,total.Item.FirstObservedAt);Assert.Equal(later,total.Item.LastObservedAt);Assert.Empty(s.Drain(later));Assert.Equal(0,s.Dropped());
    }
    [Fact]
    public void Accumulator_capacity_does_not_drop_loss_markers_or_existing_keys()
    {
        var s=Accumulator();var observation=Observation();var existing=observation with{Item=observation.Item with{CredentialId=Guid.NewGuid()}};Assert.True(s.Recorder.TryRecord(existing));for(var i=1;i<100;i++)Assert.True(s.Recorder.TryRecord(observation with{Item=observation.Item with{CredentialId=Guid.NewGuid()}}));
        Assert.True(s.Recorder.TryRecord(existing));Assert.False(s.Recorder.TryRecord(observation));Assert.Equal(1,s.Dropped());var snapshot=s.Drain(observation.Item.LastObservedAt);Assert.Equal(100,snapshot.Count);Assert.Equal(101,snapshot.Sum(x=>x.Item.Count));
        var gap=Assert.Single(s.Gaps(observation.Item.LastObservedAt.AddSeconds(1)));Assert.Equal("AccumulatorFull",gap.Reason);Assert.True(gap.To>gap.From);Assert.Empty(s.Gaps(observation.Item.LastObservedAt.AddSeconds(1)));
        Assert.True(s.Recorder.TryRecord(observation));Assert.Single(s.Drain(observation.Item.LastObservedAt));
    }
    [Fact]
    public void Accumulator_full_keys_keep_source_grant_environment_and_hour_separate()
    {
        var s=Accumulator();var a=Observation();var next=a.Item.LastObservedAt.AddMinutes(30);
        foreach(var item in new[]{a,a with{EnvironmentId=Guid.NewGuid()},a with{Item=a.Item with{Source=a.Item.Source with{DeploymentSequence=2}}},a with{Item=a.Item with{GrantValidFrom=a.Item.GrantValidFrom.AddMinutes(-1)}},a with{Item=a.Item with{HourStart=a.Item.HourStart.AddHours(1),FirstObservedAt=next,LastObservedAt=next}}})Assert.True(s.Recorder.TryRecord(item));
        Assert.Equal(5,s.Drain(next).Count);
    }
    [Fact]
    public async Task Concurrent_record_and_single_writer_drain_neither_lose_nor_repeat_counts()
    {
        var s=Accumulator();var a=Observation();long handedOff=0;
        var producers=Enumerable.Range(0,8).Select(_=>Task.Run(()=>{for(var i=0;i<1000;i++)Assert.True(s.Recorder.TryRecord(a));})).ToArray();
        while(!Task.WhenAll(producers).IsCompleted){Interlocked.Add(ref handedOff,s.Drain(a.Item.LastObservedAt).Sum(x=>x.Item.Count));await Task.Yield();}
        await Task.WhenAll(producers);handedOff+=s.Drain(a.Item.LastObservedAt).Sum(x=>x.Item.Count);Assert.Equal(8000,handedOff);Assert.Equal(0,s.Dropped());
    }
    [Fact]
    public void Disabled_collection_emits_neither_calls_nor_false_loss_markers()
    {
        var s=Accumulator(enabled:false);var a=Observation();Assert.False(s.Recorder.TryRecord(a));Assert.Empty(s.Drain(a.Item.LastObservedAt));Assert.Empty(s.Gaps(a.Item.LastObservedAt));Assert.Equal(0,s.Dropped());
    }
    private sealed class Recorder : ICredentialUsageRecorder
    {
        public readonly ConcurrentQueue<UsageObservation> Observations=[];
        public int Attempts;public string? Failure;
        public bool TryRecord(UsageObservation observation){Interlocked.Increment(ref Attempts);if(Failure=="throw")throw new IOException("synthetic usage collector failure");if(Failure=="full")return false;Observations.Enqueue(observation);return true;}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Authorized_request_counts_once_for_success_or_three_failed_backend_attempts(bool fail)
    {
        var recorder=new Recorder();var calls=0;
        await using var f=new GatewayFixture{ConfigureGateway=b=>b.Services.AddSingleton<ICredentialUsageRecorder>(recorder),ConfigureBackendA=a=>a.Use(async(c,next)=>{if(c.Request.Path=="/health"){await next();return;}Interlocked.Increment(ref calls);c.Response.StatusCode=fail?503:200;await c.Response.WriteAsync("synthetic upstream");})};
        await f.InitializeAsync();if(fail)await RetryPipelineTests.Bind(f,RetryPipelineTests.Config());var config=await f.PublishAsync();await f.ApplyBothAsync(config);
        using var response=await f.RequestAsync();Assert.Equal(fail?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK,response.StatusCode);Assert.Equal(fail?3:1,calls);
        var observed=Assert.Single(recorder.Observations);Assert.Equal(1,observed.Item.Count);Assert.Equal(f.Control.Environment.Id,observed.EnvironmentId);Assert.Equal(f.Control.Application.Id,observed.Item.ApplicationId);Assert.Equal(f.Control.Api.Id,observed.Item.ApiId);
        Assert.Equal(config.Envelope.PayloadHash,observed.Item.Source.PayloadHash);Assert.Equal(config.Envelope.ReleaseId,observed.Item.Source.ReleaseId);Assert.Equal(config.Envelope.ConfigVersion,observed.Item.Source.ConfigVersion);Assert.Equal(config.Envelope.DeploymentSequence,observed.Item.Source.DeploymentSequence);
        Assert.True(observed.Item.FirstObservedAt>=observed.Item.GrantValidFrom);Assert.True(observed.Item.GrantExpiresAt is null||observed.Item.LastObservedAt<observed.Item.GrantExpiresAt);Assert.Equal(observed.Item.FirstObservedAt,observed.Item.LastObservedAt);
    }
    [Fact]
    public async Task Shared_cache_hit_counts_each_external_request_once()
    {
        var recorder=new Recorder();await using var s=new CachePipelineFixture();var original=s.F.ConfigureGateway;s.F.ConfigureGateway=b=>{original?.Invoke(b);b.Services.AddSingleton<ICredentialUsageRecorder>(recorder);};await s.Init();
        using var first=await s.Get();first.EnsureSuccessStatusCode();await s.WaitWrites(1);using var second=await s.Get(1);second.EnsureSuccessStatusCode();
        Assert.Equal(1,s.Calls);Assert.Equal(2,recorder.Observations.Count);Assert.All(recorder.Observations,x=>Assert.Equal(1,x.Item.Count));
    }
    [Theory][InlineData("throw")][InlineData("full")]
    public async Task Recorder_failure_never_changes_authorized_response(string failure)
    {
        var recorder=new Recorder{Failure=failure};await using var f=new GatewayFixture{ConfigureGateway=b=>b.Services.AddSingleton<ICredentialUsageRecorder>(recorder)};await f.InitializeAsync();await f.ApplyBothAsync(await f.PublishAsync());
        using var response=await f.RequestAsync();response.EnsureSuccessStatusCode();Assert.Equal(1,recorder.Attempts);Assert.Empty(recorder.Observations);
        if(failure=="throw")Assert.Equal("RecorderFailure",Assert.Single(f.Gateways[0].Services.GetRequiredService<CredentialUsageAccumulator>().DrainGaps(DateTimeOffset.UtcNow)).Reason);
    }
    [Fact]
    public async Task Quota_denial_after_authentication_still_records_once()
    {
        var recorder=new Recorder();await using var s=new CachePipelineFixture();var original=s.F.ConfigureGateway;s.F.ConfigureGateway=b=>{original?.Invoke(b);b.Services.AddSingleton<ICredentialUsageRecorder>(recorder);};await s.Init(rate:true);s.Limiter.Reject=true;
        using var response=await s.Get();Assert.Equal(HttpStatusCode.TooManyRequests,response.StatusCode);Assert.Single(recorder.Observations);Assert.Equal(0,s.Calls);
    }
    [Fact]
    public async Task Actual_accumulator_saturation_keeps_authentication_working_and_emits_gap()
    {
        await using var f=new GatewayFixture{ConfigureGateway=b=>b.Configuration["CredentialUsage:MaxInMemoryKeys"]="100"};await f.InitializeAsync();await f.ApplyBothAsync(await f.PublishAsync());
        var accumulator=f.Gateways[0].Services.GetRequiredService<CredentialUsageAccumulator>();var a=Observation();for(var i=0;i<100;i++)Assert.True(accumulator.TryRecord(a with{Item=a.Item with{CredentialId=Guid.NewGuid()}}));
        using var response=await f.RequestAsync();response.EnsureSuccessStatusCode();Assert.Equal(1,accumulator.DroppedCount);Assert.Contains(accumulator.DrainGaps(DateTimeOffset.UtcNow),x=>x.Reason=="AccumulatorFull");
    }
    [Theory][InlineData("invalid")][InlineData("expired")][InlineData("ungranted")][InlineData("anonymous")][InlineData("ip")][InlineData("preflight")]
    public async Task Denied_anonymous_and_preflight_requests_do_not_record(string kind)
    {
        var recorder=new Recorder();await using var f=new GatewayFixture{ConfigureGateway=b=>b.Services.AddSingleton<ICredentialUsageRecorder>(recorder)};await f.InitializeAsync();
        await using(var db=f.Control.Context())
        {
            if(kind=="expired")await db.Set<ApplicationCredential>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.ExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
            if(kind=="ungranted")await db.Set<ApplicationApiPermission>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.ValidFrom,DateTimeOffset.UtcNow.AddMinutes(5)));
            if(kind=="anonymous"){db.RemoveRange(await db.Set<RoutePolicyBinding>().Where(x=>x.RouteId==f.RouteId).ToArrayAsync());var auth=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="usage anonymous",Type="authentication",Config="{\"mode\":\"Anonymous\"}"};db.Add(auth);db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=auth.Id});await db.SaveChangesAsync();}
            if(kind=="ip"){var policy=new Policy{OrganizationId=f.Control.Organization.Id,ProjectId=f.Control.Project.Id,Name="usage IP denial",Type="ip_filter",Config="{\"mode\":\"DenyList\",\"ranges\":[\"127.0.0.0/8\",\"::1/128\"]}"};db.Add(policy);db.Add(new RoutePolicyBinding{RouteId=f.RouteId,PolicyId=policy.Id});await db.SaveChangesAsync();}
        }
        if(kind=="preflight")await CorsPipelineTests.BindAsync(f,methods:["GET"]);
        await f.ApplyBothAsync(await f.PublishAsync());
        HttpResponseMessage response;
        if(kind=="preflight"){using var request=new HttpRequestMessage(HttpMethod.Options,"/orders");request.Headers.Add("Origin","https://portal.example.com");request.Headers.Add("Access-Control-Request-Method","GET");response=await f.Clients[0].SendAsync(request);}
        else response=await f.RequestAsync(key:kind=="invalid"?"invalid.value":null);
        using(response){Assert.Equal(kind switch{"invalid" or "expired"=>HttpStatusCode.Unauthorized,"ungranted" or "ip"=>HttpStatusCode.Forbidden,"preflight"=>HttpStatusCode.NoContent,_=>HttpStatusCode.OK},response.StatusCode);}
        Assert.Equal(0,recorder.Attempts);Assert.Empty(recorder.Observations);
    }
    [Fact]
    public async Task Jwt_with_an_api_key_header_does_not_record_credential_use()
    {
        var recorder=new Recorder();using var key=new JwtTokenVerifierTests.KeyFixture();await using var s=new CachePipelineFixture();var original=s.F.ConfigureGateway;s.F.ConfigureGateway=b=>{original?.Invoke(b);b.Services.AddSingleton<ICredentialUsageRecorder>(recorder);};await s.Init(jwt:key);
        using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");request.Headers.Authorization=new("Bearer",CachePipelineFixture.Token(key));request.Headers.Add("X-API-Key",s.F.Credential);
        using var response=await s.F.Clients[0].SendAsync(request);response.EnsureSuccessStatusCode();Assert.Equal(0,recorder.Attempts);
    }
    [Fact]
    public async Task Working_revocation_does_not_change_frozen_credential_or_window_observation()
    {
        var recorder=new Recorder();await using var f=new GatewayFixture{ConfigureGateway=b=>b.Services.AddSingleton<ICredentialUsageRecorder>(recorder)};await f.InitializeAsync();var config=await f.PublishAsync();await f.ApplyBothAsync(config);
        await using(var db=f.Control.Context()){await db.Set<ApplicationCredential>().ExecuteUpdateAsync(x=>x.SetProperty(y=>y.Status,"Revoked"));await db.Set<ApplicationApiPermission>().ExecuteDeleteAsync();}
        using var response=await f.RequestAsync();response.EnsureSuccessStatusCode();Assert.Equal(config.Envelope.PayloadHash,Assert.Single(recorder.Observations).Item.Source.PayloadHash);
    }
}

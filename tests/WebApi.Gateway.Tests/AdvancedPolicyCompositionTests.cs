using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Domain.Policies;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Tests.Support;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class AdvancedPolicyCompositionTests
{
    [Fact]public async Task HitReauthsAndConsumesOneRateTokenWithoutCircuitSample()
    {
        await using var s=new CachePipelineFixture();await s.Init(circuit:true,rate:true);using var first=await s.Get();first.EnsureSuccessStatusCode();await s.WaitWrites(1);
        var state=s.F.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!.CircuitLeases[s.F.RouteId].State;Assert.Equal(1,state.SampleCount);
        using var hit=await s.Get();hit.EnsureSuccessStatusCode();Assert.Equal(1,state.SampleCount);Assert.Equal(2,s.Limiter.Requests.Count);Assert.Equal(1,s.Calls);
        using var denied=await s.Get(credential:"bad");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);Assert.Equal(2,s.Limiter.Requests.Count);
        s.Limiter.Reject=true;using var limited=await s.Get();Assert.Equal(HttpStatusCode.TooManyRequests,limited.StatusCode);Assert.Equal(3,s.Limiter.Requests.Count);Assert.Equal(1,s.Calls);
    }
    [Fact]public async Task OpenAllowsFreshHitRejectsMissAndHitNeverClosesHalfOpen()
    {
        await using var s=new CachePipelineFixture();await s.Init(circuit:true);using var first=await s.Get();first.EnsureSuccessStatusCode();await s.WaitWrites(1);
        var state=s.F.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!.CircuitLeases[s.F.RouteId].State;state.Complete(state.TryEnter(),CircuitOutcome.Failure);Assert.Equal(CircuitStatus.Open,state.Status);
        using var hit=await s.Get();hit.EnsureSuccessStatusCode();Assert.Equal(CircuitStatus.Open,state.Status);using var miss=await s.Get(path:"/orders?miss=1");Assert.True(miss.StatusCode==HttpStatusCode.ServiceUnavailable,string.Join("\n",s.Errors));Assert.Equal(1,s.Calls);
        s.Clock.Offset=2;var probe=state.TryEnter();Assert.True(probe.Probe);using var halfHit=await s.Get();halfHit.EnsureSuccessStatusCode();Assert.Equal(CircuitStatus.HalfOpen,state.Status);using var rejected=await s.Get(path:"/orders?another=1");Assert.True(rejected.StatusCode==HttpStatusCode.ServiceUnavailable,string.Join("\n",s.Errors));state.Complete(probe,CircuitOutcome.Success);Assert.Equal(CircuitStatus.Closed,state.Status);Assert.Equal(1,s.Calls);
    }
    [Fact]public async Task RetriedFinal200OnlyStoresFinalResponse()
    {
        await using var s=new CachePipelineFixture{Respond=async(c,n)=>{c.Response.StatusCode=n<3?503:200;await c.Response.WriteAsync("attempt-"+n);}};await s.Init(circuit:true,rate:true);await RetryPipelineTests.Bind(s.F,RetryPipelineTests.Config());await s.F.ApplyBothAsync(await s.F.PublishAsync());
        using var first=await s.Get();Assert.Equal("attempt-3",await first.Content.ReadAsStringAsync());await s.WaitWrites(1);using var hit=await s.Get(1);Assert.Equal("attempt-3",await hit.Content.ReadAsStringAsync());Assert.Equal(3,s.Calls);Assert.Equal(2,s.Limiter.Requests.Count);
        Assert.Equal(1,s.F.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!.CircuitLeases[s.F.RouteId].State.SampleCount);Assert.Equal(0,s.F.Gateways[1].Services.GetRequiredService<RuntimeGenerationStore>().Current!.CircuitLeases[s.F.RouteId].State.SampleCount);
    }
}

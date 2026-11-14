using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using WebApi.Gateway.Policies;
using WebApi.Gateway.Tests.Support;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class CacheDiagnosticPipelineTests
{
    private static void Connection(CachePipelineFixture fixture,string connection)
    {
        var prior=fixture.F.ConfigureGateway;
        fixture.F.ConfigureGateway=b=>{prior?.Invoke(b);b.Configuration["GatewayPolicies:Cache:RedisConnection"]=connection;};
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedColdConnectionRetainsConnectionStateAndWaitPhase(bool abortConnect)
    {
        await using var fixture=new CachePipelineFixture();
        Connection(fixture,$"127.0.0.1:1,abortConnect={abortConnect},connectTimeout=1000,asyncTimeout=1000,connectRetry=0");
        await fixture.Init();using var response=await fixture.Get();response.EnsureSuccessStatusCode();
        Assert.Equal("business-data-1",await response.Content.ReadAsStringAsync());
        Assert.Equal(CacheReadKind.Unavailable,Assert.Single(fixture.Stores[0].Reads));
        using var document=JsonDocument.Parse(fixture.CacheDiagnostics());
        Assert.True(document.RootElement.GetProperty("nodes")[0].TryGetProperty("phases",out var phases),"Missing connection/command phase diagnostics.");
        Assert.Contains(phases.EnumerateArray(),e=>e.GetProperty("Phase").GetString()=="Connect" && e.GetProperty("Outcome").GetString()=="Started");
        Assert.Contains(phases.EnumerateArray(),e=>e.GetProperty("Phase").GetString()=="Decision" && e.GetProperty("Outcome").GetString() is "BudgetElapsed" or "RedisConnection");
        if(abortConnect)
            Assert.DoesNotContain(phases.EnumerateArray(),e=>e.GetProperty("Phase").GetString()=="Command");
        else
        {
            // Either the decision expires during connect, or a disconnected multiplexer
            // is returned first and its command waits. Neither ordering implies connectivity.
            var completed=phases.EnumerateArray().Where(e=>e.GetProperty("Phase").GetString()=="Connect" && e.GetProperty("Outcome").GetString()=="Completed").ToArray();
            if(completed.Length==0)
            {
                Assert.Contains(phases.EnumerateArray(),e=>
                    e.GetProperty("Phase").GetString()=="Decision" && e.GetProperty("WaitingFor").GetString()=="Connect" ||
                    e.GetProperty("Phase").GetString()=="Connect" && e.GetProperty("Outcome").GetString() is "BudgetElapsed" or "RedisConnection");
                Assert.DoesNotContain(phases.EnumerateArray(),e=>e.GetProperty("Phase").GetString()=="Command");
            }
            else
            {
                Assert.Equal(JsonValueKind.False,Assert.Single(completed).GetProperty("ConnectionIsConnected").ValueKind);
                // Cancellation may win just after connect returns and before command dispatch.
                // The recorded false connectivity and failed decision still identify the cause.
            }
        }
        Assert.Equal(0,fixture.Stores[0].Writes);
        Assert.Equal(0,fixture.Stores[0].WriteAttempts);
        Assert.Equal(1,fixture.Calls);
    }
    [Fact]
    public async Task RealRedisCommandDenialDistinguishesReadAndWriteFromConnectionFailure()
    {
        var user="cache_diag_"+Guid.NewGuid().ToString("N");var password=Guid.NewGuid().ToString("N");
        using var admin=await ConnectionMultiplexer.ConnectAsync("redis:6379");
        await admin.GetDatabase().ExecuteAsync("ACL","SETUSER",user,"on",">"+password,"~*","+@all","-eval","-evalsha");
        try
        {
            await using var fixture=new CachePipelineFixture();
            Connection(fixture,$"redis:6379,user={user},password={password},abortConnect=false,connectTimeout=1000,asyncTimeout=1000,connectRetry=0");
            await fixture.Init();using var response=await fixture.Get();response.EnsureSuccessStatusCode();
            Assert.Equal("business-data-1",await response.Content.ReadAsStringAsync());
            var entry=new CacheEntry(new Dictionary<string,string[]>(),[1],DateTimeOffset.UtcNow,0,TimeSpan.FromSeconds(60));
            Assert.Equal(CacheWriteKind.Bypass,(await fixture.Stores[0].PutAsync(new(new string('c',64)),entry,default)).Kind);
            var raw=fixture.CacheDiagnostics();Assert.DoesNotContain(password,raw);Assert.DoesNotContain(user,raw);
            using var document=JsonDocument.Parse(raw);
            Assert.True(document.RootElement.GetProperty("nodes")[0].TryGetProperty("phases",out var phases),"Missing connection/command phase diagnostics.");
            Assert.Contains(phases.EnumerateArray(),e=>e.GetProperty("Phase").GetString()=="Connect" && e.GetProperty("Outcome").GetString()=="Completed" && e.GetProperty("ConnectionIsConnected").ValueKind==JsonValueKind.True);
            foreach(var operation in new[]{"get","put"})
                Assert.Contains(phases.EnumerateArray(),e=>e.GetProperty("Phase").GetString()=="Command" && e.GetProperty("Operation").GetString()==operation && e.GetProperty("Outcome").GetString()=="RedisServer");
            Assert.Equal(0,fixture.Stores[0].Writes);
        }
        finally {await admin.GetDatabase().ExecuteAsync("ACL","DELUSER",user);}
    }
}

using WebApi.Gateway.CredentialUsage;
using WebApi.Gateway.Workers;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.CredentialUsage;
using WebApi.Domain.CredentialUsage;
using WebApi.Gateway;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;
namespace WebApi.Gateway.Tests;

public sealed class CredentialUsageReporterTests
{
    private sealed class Wire:IStartupFilter
    {
        public string? Mode;public int Requests;public int BatchRequests;public int AbortedOnce;public readonly ConcurrentQueue<string> CoreHashes=[];public readonly ConcurrentQueue<UsageBatchRequest> Batches=[];
        private readonly ConcurrentDictionary<string,int> active=new();public int MaximumPerNode;
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>
        {
            app.Use(async(HttpContext context,RequestDelegate handler)=>
            {
                var path=context.Request.Path.Value??"";
                if(!path.Contains("/credential-usage/",StringComparison.Ordinal)){await handler(context);return;}
                Interlocked.Increment(ref Requests);var node=path.Split('/')[4];var concurrent=active.AddOrUpdate(node,1,(_,n)=>n+1);lock(active)MaximumPerNode=Math.Max(MaximumPerNode,concurrent);
                try
                {
                    if(Mode is "old-cp" or "offline" or "denied"){context.Response.StatusCode=Mode=="old-cp"?404:Mode=="offline"?503:401;return;}
                    if(path.EndsWith("/batches",StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref BatchRequests);context.Request.EnableBuffering();using var body=new MemoryStream();await context.Request.Body.CopyToAsync(body);context.Request.Body.Position=0;
                        var request=JsonSerializer.Deserialize<UsageBatchRequest>(body.ToArray(),UsageProtocolJson.Options)!;CoreHashes.Enqueue(UsageBatchRules.CoreHash(request.Core));Batches.Enqueue(request);
                        if(Mode=="lost-ack"&&request.Core.Items.Count>0&&Interlocked.CompareExchange(ref AbortedOnce,1,0)==0)
                        {
                            var original=context.Response.Body;using var buffer=new MemoryStream();context.Response.Body=buffer;
                            try{await handler(context);context.Abort();}finally{context.Response.Body=original;}return;
                        }
                    }
                    await handler(context);
                }
                finally{active.AddOrUpdate(node,0,(_,n)=>n-1);}
            });next(app);
        };
    }
    private static GatewayFixture Fixture(Wire wire)=>new(){ConfigureControl=b=>b.Services.AddSingleton<IStartupFilter>(wire),ConfigureGateway=b=>{b.Configuration["CredentialUsage:FlushIntervalSeconds"]="5";b.Configuration["CredentialUsage:MaxPendingBytes"]=(8*1024*1024).ToString();}};
    private static void RequireReporter(GatewayFixture f)
    {
        var type=typeof(GatewayApp).Assembly.GetType("WebApi.Gateway.CredentialUsage.CredentialUsageReporter");Assert.NotNull(type);Assert.NotNull(f.Gateways[0].Services.GetService(type));
    }
    private static async Task WaitAsync(Func<Task<bool>> ready,int seconds=20)
    {var clock=Stopwatch.StartNew();while(clock.Elapsed<TimeSpan.FromSeconds(seconds)){if(await ready())return;await Task.Delay(100);}Assert.Fail("Credential usage reporter did not reach its real HTTP/PG condition within the bounded observation window.");}
    private static async Task<long> CountAsync(GatewayFixture f)
    {await using var db=f.Control.Context();return await db.Set<CredentialUsageHourly>().Where(x=>x.EnvironmentId==f.Control.Environment.Id).SumAsync(x=>x.Count);}
    [Fact]
    public async Task New_gateway_reports_real_authorized_call_and_idle_pulses_with_one_inflight_per_node()
    {
        var wire=new Wire();await using var f=Fixture(wire);await f.InitializeAsync();RequireReporter(f);await f.ApplyBothAsync(await f.PublishAsync());using var response=await f.RequestAsync();response.EnsureSuccessStatusCode();
        await WaitAsync(async()=>await CountAsync(f)==1);await WaitAsync(async()=>{await using var db=f.Control.Context();return await db.Set<CredentialUsageCoverage>().Select(x=>x.NodeId).Distinct().CountAsync()==2;});
        Assert.Equal(1,wire.MaximumPerNode);Assert.Contains(wire.Batches,x=>x.Core.Items.Count==0&&x.Core.Intervals.Count>0);await using var after=f.Control.Context();Assert.Equal(2,await after.Set<GatewayNode>().CountAsync());Assert.All(await after.Set<CredentialUsageCollector>().ToArrayAsync(),x=>Assert.True(x.AcceptedSequence>0));
    }
    [Fact]
    public async Task Lost_ack_replays_identical_core_and_does_not_repeat_count()
    {
        var wire=new Wire{Mode="lost-ack"};await using var f=Fixture(wire);await f.InitializeAsync();RequireReporter(f);await f.ApplyBothAsync(await f.PublishAsync());using var response=await f.RequestAsync();response.EnsureSuccessStatusCode();
        await WaitAsync(()=>Task.FromResult(wire.AbortedOnce==1));await WaitAsync(()=>Task.FromResult(wire.CoreHashes.GroupBy(x=>x).Any(g=>g.Count()>1)));
        Assert.Equal(1,await CountAsync(f));Assert.Equal(1,wire.MaximumPerNode);
    }
    [Fact]
    public async Task Old_endpoint_404_keeps_business_available_and_private_queue_bounded()
    {
        var wire=new Wire{Mode="old-cp"};await using var f=Fixture(wire);await f.InitializeAsync();RequireReporter(f);await f.ApplyBothAsync(await f.PublishAsync());
        using var response=await f.RequestAsync();response.EnsureSuccessStatusCode();await WaitAsync(()=>Task.FromResult(wire.Requests>0));await Task.Delay(1200);Assert.InRange(wire.Requests,1,8);
        var settings=f.Gateways[0].Services.GetRequiredService<GatewaySettings>();var checkpoint=Path.Combine(settings.LkgDirectory,"credential-usage","checkpoint.json");await WaitAsync(()=>Task.FromResult(File.Exists(checkpoint)));Assert.InRange(new FileInfo(checkpoint).Length,1,10*1024*1024);Assert.Equal(0,await CountAsync(f));
    }
    [Fact]
    public async Task Server_watermark_ahead_cannot_relabel_or_recount_unproven_delta()
    {
        var wire=new Wire();await using var f=Fixture(wire);await f.InitializeAsync();RequireReporter(f);await f.ApplyBothAsync(await f.PublishAsync());using(var first=await f.RequestAsync())first.EnsureSuccessStatusCode();await WaitAsync(async()=>await CountAsync(f)==1);
        var node=f.Gateways[0].Services.GetRequiredService<WebApi.Gateway.Workers.NodeClient>().NodeId!.Value;Guid collector;
        await using(var db=f.Control.Context()){collector=await db.Set<CredentialUsageCollector>().Where(x=>x.NodeId==node).Select(x=>x.CollectorId).SingleAsync();await db.Set<CredentialUsageCollector>().Where(x=>x.NodeId==node&&x.CollectorId==collector).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.AcceptedSequence,99L));}
        using(var rejected=await f.RequestAsync())rejected.EnsureSuccessStatusCode();await WaitAsync(async()=>{await using var db=f.Control.Context();return await db.Set<CredentialUsageCollector>().AnyAsync(x=>x.NodeId==node&&x.CollectorId!=collector);});
        using(var current=await f.RequestAsync())current.EnsureSuccessStatusCode();await WaitAsync(async()=>await CountAsync(f)==2);Assert.Equal(2,await CountAsync(f));Assert.Contains(wire.Batches.SelectMany(x=>x.Core.Gaps),g=>g.Reason=="CheckpointRewind");
    }
    [Fact]
    public async Task Restart_rebinds_old_immutable_producer_and_recovers_offline_pending_once()
    {
        var wire=new Wire{Mode="offline"};await using var f=Fixture(wire);await f.InitializeAsync();RequireReporter(f);await f.ApplyBothAsync(await f.PublishAsync());using(var response=await f.RequestAsync())response.EnsureSuccessStatusCode();
        var old=f.Gateways[0].Services.GetRequiredService<GatewaySettings>().InstanceId;await Task.Delay(5500);await f.RestartAsync(0);var current=f.Gateways[0].Services.GetRequiredService<GatewaySettings>().InstanceId;Assert.NotEqual(old,current);
        wire.Mode=null;await WaitAsync(async()=>await CountAsync(f)==1);
        Assert.Contains(wire.Batches,x=>x.SenderInstanceId==current&&x.Core.ProducerInstanceId==old&&x.Core.Items.Count>0);Assert.Equal(1,await CountAsync(f));
    }
    [Fact]
    public async Task Rejected_identity_stops_ineffective_retry_and_shutdown_is_bounded()
    {
        var wire=new Wire{Mode="denied"};await using var f=Fixture(wire);await f.InitializeAsync();RequireReporter(f);await f.ApplyBothAsync(await f.PublishAsync());using(var response=await f.RequestAsync())response.EnsureSuccessStatusCode();
        await WaitAsync(()=>Task.FromResult(wire.Requests>=2));var requests=wire.Requests;await Task.Delay(1500);Assert.Equal(requests,wire.Requests);
        var stopwatch=Stopwatch.StartNew();await f.Gateways[0].StopAsync();Assert.True(stopwatch.Elapsed<TimeSpan.FromSeconds(6));Assert.Equal(0,await CountAsync(f));
    }

    private sealed class Observations:ICredentialUsageRecorder
    {public readonly ConcurrentQueue<UsageObservation> Values=[];public bool TryRecord(UsageObservation value){Values.Enqueue(value);return true;}}
    [Fact]
    public async Task Multiple_hash_proven_already_applied_receipts_do_not_discard_later_pending_counts()
    {
        var wire=new Wire();var observations=new Observations();await using var f=Fixture(wire);var configure=f.ConfigureGateway;f.ConfigureGateway=b=>{configure!(b);b.Services.AddSingleton<ICredentialUsageRecorder>(observations);};
        await f.InitializeAsync();await f.ApplyBothAsync(await f.PublishAsync());for(var i=0;i<3;i++){using var response=await f.RequestAsync();response.EnsureSuccessStatusCode();}
        var settings=f.Gateways[0].Services.GetRequiredService<GatewaySettings>();var node=f.Gateways[0].Services.GetRequiredService<NodeClient>().NodeId!.Value;var options=f.Gateways[0].Services.GetRequiredService<CredentialUsageOptions>();
        await f.Gateways[0].StopAsync();await f.Gateways[0].DisposeAsync();var now=DateTimeOffset.UtcNow;var collector=Guid.NewGuid();
        var pending=observations.Values.Select((x,i)=>new UsageBatchCore("1.0",settings.InstanceId,collector,i+1,now,now,[],[],[x.Item])).ToArray();Assert.Equal(3,pending.Length);
        using(var journal=new UsageJournal(settings,options)){await journal.LoadAsync();await journal.SaveAsync(new(settings.EnvironmentId,settings.NodeName,collector,4,now,[],pending,[]));}
        using(var sender=new HttpClient{BaseAddress=f.Control.Client.BaseAddress,Timeout=TimeSpan.FromSeconds(3)})
        {
            sender.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",File.ReadAllText(settings.SecretFile).Trim());
            using(var bind=await sender.PostAsJsonAsync($"/internal/v1/nodes/{node}/credential-usage/collectors",new UsageCollectorRequest("1.0",settings.InstanceId,collector),UsageProtocolJson.Options))bind.EnsureSuccessStatusCode();
            foreach(var batch in pending.Take(2)){using var applied=await sender.PostAsJsonAsync($"/internal/v1/nodes/{node}/credential-usage/batches",new UsageBatchRequest(settings.InstanceId,batch),UsageProtocolJson.Options);applied.EnsureSuccessStatusCode();}
        }
        Assert.Equal(2,await CountAsync(f));f.Gateways[0]=await f.StartGatewayAsync(0);f.Clients[0].Dispose();f.Clients[0]=new HttpClient{BaseAddress=new Uri(GatewayFixture.Url(f.Gateways[0]))};
        await WaitAsync(async()=>await CountAsync(f)==3);await using var db=f.Control.Context();Assert.Equal(3,await db.Set<CredentialUsageCollector>().Where(x=>x.NodeId==node&&x.CollectorId==collector).Select(x=>x.AcceptedSequence).SingleAsync());
    }

}

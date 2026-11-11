using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Contracts.Applications;
using WebApi.Contracts.Common;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using Xunit;

namespace WebApi.Gateway.Tests;

public sealed class CredentialWindowActivationTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcTicks;
        public void Set(DateTimeOffset value)=>Interlocked.Exchange(ref ticks,value.UtcTicks);
        public override DateTimeOffset GetUtcNow()=>new(Interlocked.Read(ref ticks),TimeSpan.Zero);
    }

    private static GatewayFixture Fixture(Clock clock,Action backendHit)=>new()
    {
        ConfigureGateway=b=>b.Services.AddSingleton<TimeProvider>(clock),
        ConfigureBackendA=a=>a.Use(async(ctx,next)=>
        {
            if(ctx.Request.Path=="/orders")backendHit();
            await next();
        })
    };

    private static async Task AssertCallsAsync(GatewayFixture f,HttpStatusCode expected,Func<int> hits)
    {
        var before=hits();
        for(var node=0;node<2;node++)
        {
            using var response=await f.RequestAsync(node);
            Assert.Equal(expected,response.StatusCode);
            if(expected==HttpStatusCode.Unauthorized)
                Assert.Equal("invalid_api_key",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(expected==HttpStatusCode.OK?2:0,hits()-before);
    }

    [Fact]
    public async Task Published_window_includes_exact_start_and_excludes_exact_expiry_on_both_nodes()
    {
        var clock=new Clock();var hits=0;
        await using var f=Fixture(clock,()=>Interlocked.Increment(ref hits));
        await f.InitializeAsync();
        var from=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).AddHours(1);
        var end=from.AddHours(1);
        await using(var db=f.Control.Context())
        {
            var key=await db.Set<ApplicationCredential>().SingleAsync();key.ValidFrom=from;key.ExpiresAt=end;
            var grant=await db.Set<ApplicationApiPermission>().SingleAsync();grant.ValidFrom=from.AddHours(-1);grant.ExpiresAt=end.AddHours(1);
            await db.SaveChangesAsync();
        }
        await f.ApplyBothAsync(await f.PublishAsync());
        foreach(var point in new[]{(from.AddTicks(-1),HttpStatusCode.Unauthorized),(from,HttpStatusCode.OK),
                    (end.AddTicks(-1),HttpStatusCode.OK),(end,HttpStatusCode.Unauthorized),(end.AddTicks(1),HttpStatusCode.Unauthorized)})
        {
            clock.Set(point.Item1);
            await AssertCallsAsync(f,point.Item2,()=>Volatile.Read(ref hits));
        }
        Assert.Equal(4,hits);
    }

    [Theory]
    [InlineData("extend")]
    [InlineData("shorten")]
    [InlineData("future")]
    [InlineData("revoke")]
    public async Task Working_window_or_revocation_changes_require_republish_before_both_nodes_change(string change)
    {
        var clock=new Clock();var hits=0;
        await using var f=Fixture(clock,()=>Interlocked.Increment(ref hits));
        await f.InitializeAsync();
        Guid id;long appRevision;
        await using(var db=f.Control.Context())
        {
            id=await db.Set<ApplicationCredential>().Select(x=>x.Id).SingleAsync();
            appRevision=await db.Set<ApplicationRecord>().Select(x=>x.Revision).SingleAsync();
            var grant=await db.Set<ApplicationApiPermission>().SingleAsync();
            grant.ExpiresAt=DateTimeOffset.UtcNow.AddDays(3);
            await db.SaveChangesAsync();
        }
        using var originalResponse=await f.Control.Client.GetAsync($"/api/v1/credentials/{id}");
        originalResponse.EnsureSuccessStatusCode();
        var original=(await originalResponse.Content.ReadFromJsonAsync<CredentialDto>())!;
        var from=change=="future"?clock.GetUtcNow().AddHours(1):original.ValidFrom;
        var end=change switch {"extend"=>original.ExpiresAt.AddHours(1),"shorten"=>clock.GetUtcNow().AddHours(1),_=>original.ExpiresAt};
        var status=change=="revoke"?"Revoked":"Active";
        var first=await f.PublishAsync();await f.ApplyBothAsync(first);
        await AssertCallsAsync(f,HttpStatusCode.OK,()=>Volatile.Read(ref hits));
        var stores=f.Gateways.Select(g=>g.Services.GetRequiredService<RuntimeGenerationStore>()).ToArray();
        var generations=stores.Select(s=>s.Current!).ToArray();
        using var preview=await f.Control.WriteAsync(HttpMethod.Post,$"/api/v1/credentials/{id}/impact",
            new CredentialImpactRequest(from,end,status),RevisionTag.Format(original.Revision));
        preview.EnsureSuccessStatusCode();
        var hash=(await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("impactHash").GetString()!;
        using var update=await f.Control.WriteAsync(HttpMethod.Put,$"/api/v1/credentials/{id}",
            new CredentialUpdateRequest(from,end,status,new CredentialImpactConfirmation(hash,true)),RevisionTag.Format(original.Revision));
        update.EnsureSuccessStatusCode();
        using var readback=await f.Control.Client.GetAsync($"/api/v1/credentials/{id}");
        readback.EnsureSuccessStatusCode();
        var saved=(await readback.Content.ReadFromJsonAsync<CredentialDto>())!;
        Assert.Equal((from,end,status,original.Revision+1),(saved.ValidFrom,saved.ExpiresAt,saved.Status,saved.Revision));
        Assert.Equal("WorkingConfiguration",saved.Activation);
        Assert.Equal(RevisionTag.Format(saved.Revision),readback.Headers.ETag?.ToString());
        await using(var db=f.Control.Context())
        {
            var key=await db.Set<ApplicationCredential>().AsNoTracking().SingleAsync();
            Assert.Equal((from,end,status,saved.Revision),(key.ValidFrom,key.ExpiresAt,key.Status,key.Revision));
            Assert.Equal(appRevision+1,await db.Set<ApplicationRecord>().Select(x=>x.Revision).SingleAsync());
            var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync();
            Assert.Equal(first.Envelope.ConfigVersion,env.DesiredConfigVersion);
            Assert.Equal(first.Envelope.DeploymentSequence,env.DeploymentSequence);
            Assert.Equal(first.Payload,(await db.Set<GatewayConfigSnapshot>().SingleAsync()).PayloadBytes);
        }
        for(var node=0;node<2;node++)
        {
            Assert.Same(generations[node],stores[node].Current);
            Assert.Equal(first.Payload,stores[node].Current!.Payload.ToArray());
        }
        // Pick an instant where the old and proposed windows make opposite decisions.
        clock.Set(change switch {"extend"=>original.ExpiresAt,"shorten"=>end,"future"=>from.AddTicks(-1),_=>clock.GetUtcNow()});
        await AssertCallsAsync(f,change=="extend"?HttpStatusCode.Unauthorized:HttpStatusCode.OK,()=>Volatile.Read(ref hits));
        var second=await f.PublishAsync();await f.ApplyBothAsync(second);
        Assert.True(second.Envelope.ConfigVersion>first.Envelope.ConfigVersion);
        Assert.True(second.Envelope.DeploymentSequence>first.Envelope.DeploymentSequence);
        Assert.NotEqual(first.Envelope.PayloadHash,second.Envelope.PayloadHash);
        for(var node=0;node<2;node++)
        {
            Assert.NotSame(generations[node],stores[node].Current);
            Assert.Equal(second.Envelope,stores[node].Current!.Envelope);
            var publishedKey=Assert.Single(Assert.Single(stores[node].Current!.Snapshot.Applications).Credentials);
            Assert.Equal((id,from,end,status),(publishedKey.Id,publishedKey.ValidFrom,publishedKey.ExpiresAt,publishedKey.Status));
        }
        await AssertCallsAsync(f,change=="extend"?HttpStatusCode.OK:HttpStatusCode.Unauthorized,()=>Volatile.Read(ref hits));
        if(change=="future")
        {
            clock.Set(from);
            await AssertCallsAsync(f,HttpStatusCode.OK,()=>Volatile.Read(ref hits));
        }
    }
}

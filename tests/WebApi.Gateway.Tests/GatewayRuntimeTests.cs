using System.Net;
using Microsoft.AspNetCore.Builder;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Gateway.Configuration;
using WebApi.Gateway;
using WebApi.Gateway.Workers;
using WebApi.Gateway.Tests.Support;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.TestBackend;
using WebApi.Gateway.Storage;
using WebApi.Contracts.Runtime;
using Yarp.ReverseProxy.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
using WebApi.Contracts.Common;
using WebApi.Domain.Runtime;
using Xunit;
namespace WebApi.Gateway.Tests;
public sealed class GatewayRuntimeTests
{
    [Theory]
    [InlineData("valid",200)] [InlineData("missing",401)] [InlineData("unknown",401)] [InlineData("wrong-secret",401)]
    [InlineData("case",401)] [InlineData("empty",401)] [InlineData("multi-segment",401)] [InlineData("revoked",401)]
    [InlineData("future",401)] [InlineData("expired",401)] [InlineData("disabled",401)]
    [InlineData("expired-grant",403)] [InlineData("future-grant",403)] [InlineData("any-valid-window",200)]
    public async Task IndexedAuthPreservesStatusAndTimeBoundaries(string kind,int status)
    {
        var hits=0;await using var f=new GatewayFixture {ConfigureBackendA=a=>a.Use(async(ctx,next)=>{if(ctx.Request.Path=="/orders")Interlocked.Increment(ref hits);await next();})};
        await f.InitializeAsync();var now=DateTimeOffset.UtcNow;
        await using(var db=f.Control.Context()) {
            var credential=await db.Set<ApplicationCredential>().SingleAsync();
            if(kind=="revoked")credential.Status="Revoked";
            if(kind=="future")credential.ValidFrom=now.AddHours(1);
            if(kind=="expired"){credential.ValidFrom=now.AddHours(-2);credential.ExpiresAt=now.AddHours(-1);}
            if(kind=="disabled")(await db.Set<ApplicationRecord>().SingleAsync()).Status="Disabled";
            var grant=await db.Set<ApplicationApiPermission>().SingleAsync();
            if(kind is "expired-grant" or "any-valid-window"){grant.ValidFrom=now.AddDays(-2);grant.ExpiresAt=now.AddDays(-1);}
            if(kind=="future-grant")grant.ValidFrom=now.AddHours(1);
            await db.SaveChangesAsync();
        }
        var published=await f.PublishAsync();
        if(kind=="any-valid-window") {
            var root=JsonNode.Parse(published.Payload)!;var windows=root["applications"]![0]!["permissions"]!.AsArray();
            windows.Add(JsonSerializer.SerializeToNode(new RuntimePermission(f.Control.Api.Id,now.AddHours(-1),null),CanonicalJson.Options));
            windows.Add(JsonSerializer.SerializeToNode(new RuntimePermission(f.Control.Api.Id,now.AddDays(1),now.AddDays(2)),CanonicalJson.Options));
            var payload=Encoding.UTF8.GetBytes(root.ToJsonString(CanonicalJson.Options));SnapshotValidator.ParsePayload(payload,f.Control.Environment.Id);
            var envelope=published.Envelope with {PayloadHash=Convert.ToHexStringLower(SHA256.HashData(payload)),SizeBytes=payload.Length};
            foreach(var gateway in f.Gateways)Assert.True((await gateway.Services.GetRequiredService<SnapshotActivation>().ApplyAsync(envelope,payload)).Applied);
        } else await f.ApplyBothAsync(published);
        var parts=f.Credential.Split('.');
        var key=kind switch {"unknown"=>"ak_isolated_unknown.fake-secret","wrong-secret"=>parts[0]+".wrong-secret","case"=>parts[0].ToUpperInvariant()+"."+parts[1],"empty"=>"","multi-segment"=>f.Credential+".extra",_=>f.Credential};
        foreach(var node in new[]{0,1}) {
            using var request=new HttpRequestMessage(HttpMethod.Get,"/orders");if(kind!="missing")request.Headers.TryAddWithoutValidation("X-API-Key",key);
            using var response=await f.Clients[node].SendAsync(request);Assert.Equal((HttpStatusCode)status,response.StatusCode);
            if(status!=200)Assert.Equal(status==401?"invalid_api_key":"api_not_granted",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            if(kind=="any-valid-window")Assert.Equal(3,f.Gateways[node].Services.GetRequiredService<RuntimeGenerationStore>().Current!.Indexes.PermissionWindows[(f.Control.Application.Id,f.Control.Api.Id)].Count);
        }
        Assert.Equal(status==200?2:0,hits);
    }

    [Fact]
    public async Task HeldOldRequestUsesOldIndexesAndRetiredGenerationCollects()
    {
        await using var f=new GatewayFixture();await f.InitializeAsync();var first=await f.PublishAsync();await f.ApplyBothAsync(first);
        var store=f.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>();var oldIndexes=store.Current!.Indexes;var oldKey=f.Credential;
        var probe=f.BackendA.Services.GetRequiredService<BackendProbe>();probe.HoldNext();var waiting=f.RequestAsync();
        try {
            await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));await f.RotateCredentialAsync();var next=await f.PublishAsync(1);await f.ApplyBothAsync(next);
            Assert.NotSame(oldIndexes,store.Current!.Indexes);using(var held=store.Acquire(first.Envelope.DeploymentSequence))Assert.Same(oldIndexes,held.Generation.Indexes);
            using var rejected=await f.RequestAsync(key:oldKey);Assert.Equal(HttpStatusCode.Unauthorized,rejected.StatusCode);
            probe.Release();using var oldResponse=await waiting;Assert.Equal(HttpStatusCode.OK,oldResponse.StatusCode);
            Assert.Equal(first.Envelope.DeploymentSequence,(await oldResponse.Content.ReadFromJsonAsync<BackendResponse>())!.DeploymentSequence);
            var until=DateTimeOffset.UtcNow.AddSeconds(5);while(DateTimeOffset.UtcNow<until){try{using var held=store.Acquire(first.Envelope.DeploymentSequence);}catch(InvalidOperationException){break;}await Task.Delay(20);}
            Assert.Throws<InvalidOperationException>(()=>store.Acquire(first.Envelope.DeploymentSequence));
        }finally{probe.Release();}
    }

    [Theory] [InlineData("2.0")] [InlineData("2.1")] [InlineData("2.2")]
    public async Task HistoricalLoadAndRollbackRetainExactPayloadWithIndexes(string schema)
    {
        await using var f=new GatewayFixture();await f.InitializeAsync();var published=await f.PublishAsync();var historical=AdaptPacket(published,schema,1,1);
        SnapshotValidator.ParsePayload(historical.Payload,f.Control.Environment.Id);
        foreach(var gateway in f.Gateways)Assert.True((await gateway.Services.GetRequiredService<SnapshotActivation>().ApplyAsync(historical.Envelope,historical.Payload)).Applied);
        var next=AdaptPacket(published,"2.2",2,2);foreach(var gateway in f.Gateways)Assert.True((await gateway.Services.GetRequiredService<SnapshotActivation>().ApplyAsync(next.Envelope,next.Payload)).Applied);
        var rollback=historical with {Envelope=historical.Envelope with {DeploymentSequence=3}};
        foreach(var gateway in f.Gateways) {
            Assert.True((await gateway.Services.GetRequiredService<SnapshotActivation>().ApplyAsync(rollback.Envelope,rollback.Payload)).Applied);
            var current=gateway.Services.GetRequiredService<RuntimeGenerationStore>().Current!;var saved=(await gateway.Services.GetRequiredService<LkgStore>().ReadAsync())!;
            Assert.Equal(schema,current.Snapshot.SchemaVersion);Assert.Equal(historical.Payload,current.Payload.ToArray());Assert.Equal(historical.Payload,saved.Payload);
            Assert.Equal(historical.Envelope.PayloadHash,saved.Envelope.PayloadHash);Assert.Single(current.Indexes.Routes);
        }
        await f.RestartAsync(0);var restored=f.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!;
        Assert.Equal(historical.Envelope.PayloadHash,restored.Envelope.PayloadHash);Assert.Equal(historical.Payload,restored.Payload.ToArray());
        foreach(var node in new[]{0,1}){using var response=await f.RequestAsync(node);Assert.Equal(HttpStatusCode.OK,response.StatusCode);}
    }

    private static DesiredConfigResponse AdaptPacket(DesiredConfigResponse source,string schema,long sequence,long version)
    {
        var root=JsonNode.Parse(source.Payload)!.AsObject();var originalSchema=root["schemaVersion"]!.GetValue<string>();root["schemaVersion"]=schema;root["configVersion"]=version;
        foreach(var route in root["routes"]!.AsArray().OfType<JsonObject>()) {if(schema!="2.2")route.Remove("authenticationMode");if(schema=="2.0")route.Remove("policyBindings");}
        if(schema=="2.0")foreach(var policy in root["policies"]!.AsArray().OfType<JsonObject>()){policy.Remove("sourcePolicyId");policy.Remove("sourceRevision");}
        var payload=schema==originalSchema&&version==source.Envelope.ConfigVersion?source.Payload.ToArray():Encoding.UTF8.GetBytes(root.ToJsonString(CanonicalJson.Options));
        return new(source.Envelope with {DeploymentSequence=sequence,ConfigVersion=version,PayloadHash=Convert.ToHexStringLower(SHA256.HashData(payload)),SizeBytes=payload.Length},payload);
    }

    [Fact]
    public async Task InvalidCredentialIndexInputKeepsPreviousRuntimeLkgAndSuccessAcks()
    {
        await using var f=new GatewayFixture();await f.InitializeAsync();var first=await f.PublishAsync();await f.ApplyBothAsync(first);var store=f.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>();var index=store.Current!.Indexes;
        var second=await f.PublishAsync(1);var root=JsonNode.Parse(second.Payload)!;var credentials=root["applications"]![0]!["credentials"]!.AsArray();credentials.Add(credentials[0]!.DeepClone());
        var payload=Encoding.UTF8.GetBytes(root.ToJsonString(CanonicalJson.Options));var invalid=second with {Payload=payload,Envelope=second.Envelope with {PayloadHash=Convert.ToHexStringLower(SHA256.HashData(payload)),SizeBytes=payload.Length}};
        var result=await f.Gateways[0].Services.GetRequiredService<ConfigWatcher>().AcceptAsync(invalid);Assert.False(result.Applied);Assert.Equal("invalid_snapshot",result.ErrorCode);
        Assert.Same(index,store.Current!.Indexes);var saved=(await f.Gateways[0].Services.GetRequiredService<LkgStore>().ReadAsync())!;Assert.Equal(first.Envelope,saved.Envelope);Assert.Equal(first.Payload,saved.Payload);
        await using var db=f.Control.Context();Assert.Equal(2,await db.Set<GatewayAck>().CountAsync(a=>a.ReleaseId==first.Envelope.ReleaseId&&a.Success));Assert.False(await db.Set<GatewayAck>().AnyAsync(a=>a.ReleaseId==second.Envelope.ReleaseId&&a.Success));
        using var response=await f.RequestAsync();Assert.Equal(HttpStatusCode.OK,response.StatusCode);
    }

    [Fact] public async Task RunningEntryRemainsConfirmedAfterRealDualGatewayRestart()
    {
        await using var s=new GatewayFixture();await s.InitializeAsync();var config=await s.PublishAsync();await s.ApplyBothAsync(config);
        using(var detail=await s.Control.Client.GetAsync($"/api/v1/environments/{s.Control.Environment.Id}"))
        {
            var e=await detail.Content.ReadFromJsonAsync<JsonElement>();using var saved=await s.Control.WriteAsync(HttpMethod.Put,$"/api/v1/environments/{s.Control.Environment.Id}",new {code=e.GetProperty("code").GetString(),name=e.GetProperty("name").GetString(),status="Active",isProduction=e.GetProperty("isProduction").GetBoolean(),releasePolicyId=e.GetProperty("releasePolicyId").ValueKind==JsonValueKind.Null?(Guid?)null:e.GetProperty("releasePolicyId").GetGuid(),sortOrder=0,gatewayPublicUrl="https://api.test",basePath="/gateway"},detail.Headers.ETag!.ToString());saved.EnsureSuccessStatusCode();
        }
        var endpoint=$"/api/v1/environments/{s.Control.Environment.Id}/apis/{s.Control.Api.Id}/access-addresses?view=running";
        using(var before=await s.Control.Client.GetAsync(endpoint))before.EnsureSuccessStatusCode();
        for(var i=0;i<2;i++){await s.RestartAsync(i);await WaitReadyAsync(s);var applied=await s.Gateways[i].Services.GetRequiredService<ConfigWatcher>().AcceptAsync(config);Assert.True(applied.Applied);}
        using(var running=await s.Control.Client.GetAsync(endpoint)){running.EnsureSuccessStatusCode();Assert.Contains("https://api.test/gateway/orders",await running.Content.ReadAsStringAsync());}
        await using var db=s.Control.Context();Assert.Equal(1,await db.Set<ReleaseRecord>().CountAsync());Assert.Equal(2,await db.Set<GatewayAck>().CountAsync());
        var node=await db.Set<GatewayNode>().FirstAsync();var metadata=System.Text.Json.Nodes.JsonNode.Parse(node.Metadata!)!;metadata["runtimeApplication"]!["payloadHash"]="wrong-hash";node.Metadata=metadata.ToJsonString();await db.SaveChangesAsync();
        using var invalid=await s.Control.Client.GetAsync(endpoint);Assert.Equal(HttpStatusCode.Conflict,invalid.StatusCode);
    }
    [Fact] public async Task RealDoubleGatewayRestartConfirmsLkgWithoutRepublishing()
    {
        await using var s=new GatewayFixture();await s.InitializeAsync();var config=await s.PublishAsync();await s.ApplyBothAsync(config);
        var oldInstances=s.Gateways.Select(g=>g.Services.GetRequiredService<GatewaySettings>().InstanceId).ToArray();
        for(var i=0;i<2;i++) {await s.RestartAsync(i);var until=DateTimeOffset.UtcNow.AddSeconds(12);while(s.Gateways[i].Services.GetRequiredService<RuntimeGenerationStore>().Current is null&&DateTimeOffset.UtcNow<until) await Task.Delay(50);var gateway=s.Gateways[i];Assert.NotEqual(oldInstances[i],gateway.Services.GetRequiredService<GatewaySettings>().InstanceId);Assert.Equal(config.Envelope,gateway.Services.GetRequiredService<RuntimeGenerationStore>().Current!.Envelope);var result=await gateway.Services.GetRequiredService<ConfigWatcher>().AcceptAsync(config);Assert.True(result.Applied);using var traffic=await s.RequestAsync(i);traffic.EnsureSuccessStatusCode();Assert.Equal("A",(await traffic.Content.ReadFromJsonAsync<BackendResponse>())!.BackendId);}
        await using var db=s.Control.Context();Assert.Equal(1,await db.Set<ReleaseRecord>().CountAsync());Assert.Equal("Succeeded",(await db.Set<ReleaseRecord>().SingleAsync()).Status);Assert.Equal(2,await db.Set<GatewayAck>().CountAsync());Assert.Equal(2,await db.Set<GatewayNodeEvent>().CountAsync(e=>e.EventType=="runtime_applied"));foreach(var node in await db.Set<GatewayNode>().ToArrayAsync()) {using var metadata=JsonDocument.Parse(node.Metadata!);Assert.Equal(node.InstanceId,metadata.RootElement.GetProperty("runtimeApplication").GetProperty("instanceId").GetString());}
    }
    [Fact] public async Task ActualRequestUsesPublishedDestination()
    {await using var s=new GatewayFixture();await s.InitializeAsync();var config=await s.PublishAsync();await s.ApplyBothAsync(config);for(var i=0;i<2;i++) {using var response=await s.RequestAsync(i);response.EnsureSuccessStatusCode();var body=await response.Content.ReadFromJsonAsync<BackendResponse>();Assert.Equal("A",body!.BackendId);Assert.Equal("GET",body.Method);Assert.Equal("/orders",body.Path);Assert.False(body.ReceivedApiKey);}await using var db=s.Control.Context();Assert.Equal("Succeeded",(await db.Set<ReleaseRecord>().SingleAsync()).Status);Assert.Equal(2,await db.Set<GatewayAck>().CountAsync());}
    [Fact] public async Task RequestUsesOneRoutingAndAuthGeneration()
    {await using var s=new GatewayFixture();await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());var oldKey=s.Credential;var probe=s.BackendA.Services.GetRequiredService<BackendProbe>();probe.HoldNext();var old=s.RequestAsync();await probe.Started.WaitAsync(TimeSpan.FromSeconds(5));await s.RotateCredentialAsync();var next=await s.PublishAsync(1);await s.ApplyBothAsync(next);using var current=await s.RequestAsync();current.EnsureSuccessStatusCode();var body=(await current.Content.ReadFromJsonAsync<BackendResponse>())!;Assert.Equal("B",body.BackendId);Assert.Equal(next.Envelope.DeploymentSequence,body.DeploymentSequence);using var rejected=await s.RequestAsync(key:oldKey);Assert.Equal(HttpStatusCode.Unauthorized,rejected.StatusCode);probe.Release();using var previous=await old;previous.EnsureSuccessStatusCode();var previousBody=(await previous.Content.ReadFromJsonAsync<BackendResponse>())!;Assert.Equal("A",previousBody.BackendId);Assert.Equal(1,previousBody.DeploymentSequence);Assert.Equal(next.Envelope.DeploymentSequence,s.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!.Envelope.DeploymentSequence);}
    [Fact] public async Task BadHashKeepsOldGenerationAndNoAck()
    {await using var s=new GatewayFixture();await s.InitializeAsync();var first=await s.PublishAsync();await s.ApplyBothAsync(first);var second=await s.PublishAsync(1);var corrupt=second.Payload.ToArray();corrupt[^1]^=1;await s.Gateways[0].Services.GetRequiredService<ConfigWatcher>().AcceptAsync(second with {Payload=corrupt});Assert.Equal(first.Envelope,s.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!.Envelope);using var traffic=await s.RequestAsync();traffic.EnsureSuccessStatusCode();Assert.Equal("A",(await traffic.Content.ReadFromJsonAsync<BackendResponse>())!.BackendId);await using var db=s.Control.Context();Assert.False(await db.Set<GatewayAck>().AnyAsync(a=>a.ReleaseId==second.Envelope.ReleaseId&&a.Success));}
    [Fact] public async Task LkgWriteDeniedKeepsOldTraffic()
    {await using var s=new GatewayFixture();await s.InitializeAsync();var first=await s.PublishAsync();await s.ApplyBothAsync(first);var second=await s.PublishAsync(1);var directory=Path.GetDirectoryName(s.Gateways[0].Services.GetRequiredService<LkgStore>().FilePath)!;if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserExecute);var result=await s.Gateways[0].Services.GetRequiredService<ConfigWatcher>().AcceptAsync(second);Assert.False(result.Applied);Assert.Equal("lkg_write_denied",result.ErrorCode);Assert.Equal(first.Envelope,(await s.Gateways[0].Services.GetRequiredService<LkgStore>().ReadAsync())!.Envelope);using var response=await s.RequestAsync();response.EnsureSuccessStatusCode();Assert.Equal("A",(await response.Content.ReadFromJsonAsync<BackendResponse>())!.BackendId);await using var db=s.Control.Context();Assert.False(await db.Set<GatewayAck>().AnyAsync(a=>a.ReleaseId==second.Envelope.ReleaseId&&a.Success));}
    private sealed class RejectSequenceFilter : IProxyConfigFilter
    {
        public long Sequence {get;set;}=2;
        public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster,CancellationToken ct)=>ValueTask.FromResult(cluster);
        public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route,ClusterConfig? cluster,CancellationToken ct) {if(route.Metadata?["deploymentSequence"]==Sequence.ToString()) throw new InvalidOperationException("Injected YARP config filter rejection.");return ValueTask.FromResult(route);}
    }
    [Fact] public async Task YarpRejectedConfigDoesNotAck()
    {var filter=new RejectSequenceFilter();await using var s=new GatewayFixture {ConfigureGateway=b=>b.Services.AddSingleton<IProxyConfigFilter>(filter)};await s.InitializeAsync();var first=await s.PublishAsync();await s.ApplyBothAsync(first);var second=await s.PublishAsync(1);var result=await s.Gateways[0].Services.GetRequiredService<ConfigWatcher>().AcceptAsync(second);Assert.False(result.Applied);Assert.Equal("yarp_rejected_config",result.ErrorCode);Assert.Equal(first.Envelope,(await s.Gateways[0].Services.GetRequiredService<LkgStore>().ReadAsync())!.Envelope);using var response=await s.RequestAsync();response.EnsureSuccessStatusCode();Assert.Equal("A",(await response.Content.ReadFromJsonAsync<BackendResponse>())!.BackendId);await using var db=s.Control.Context();Assert.False(await db.Set<GatewayAck>().AnyAsync(a=>a.ReleaseId==second.Envelope.ReleaseId&&a.Success));}
    [Fact] public async Task CredentialExpiresAtAdmission()
    {await using var s=new GatewayFixture();await s.InitializeAsync();var expires=DateTimeOffset.UtcNow.AddSeconds(5);await using(var db=s.Control.Context()) {var key=await db.Set<ApplicationCredential>().SingleAsync();key.ExpiresAt=expires;await db.SaveChangesAsync();}await s.ApplyBothAsync(await s.PublishAsync());using var gate=await s.Gateways[0].Services.GetRequiredService<AdmissionGate>().EnterAsync(CancellationToken.None);var waiting=s.RequestAsync();var delay=expires-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(30);if(delay>TimeSpan.Zero) await Task.Delay(delay);gate.Dispose();using var response=await waiting;Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);}
    [Fact] public async Task ControlPlaneAndRedisDownStillProxyFromLkg()
    {await using var s=new GatewayFixture();await s.InitializeAsync();var first=await s.PublishAsync();await s.ApplyBothAsync(first);await s.Control.StopServerAsync();s.ConfigureGateway=b=>{b.Configuration["Gateway:AutomaticUpdates"]="true";b.Configuration["Redis:Connection"]="127.0.0.1:1,abortConnect=false,connectTimeout=200,asyncTimeout=200";};await s.RestartAsync(0,false);await WaitReadyAsync(s);using var response=await s.RequestAsync();response.EnsureSuccessStatusCode();Assert.Equal("A",(await response.Content.ReadFromJsonAsync<BackendResponse>())!.BackendId);Assert.Equal(first.Envelope,s.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!.Envelope);}
    [Fact] public async Task NoLkgNoRemoteNotReady()
    {await using var s=new GatewayFixture();await s.InitializeAsync();await s.Control.StopServerAsync();s.ConfigureGateway=b=>{b.Configuration["Gateway:AutomaticUpdates"]="true";b.Configuration["Redis:Connection"]="127.0.0.1:1,abortConnect=false,connectTimeout=200,asyncTimeout=200";};await s.RestartAsync(0,false);using var ready=await s.Clients[0].GetAsync("/health/ready");Assert.Equal(HttpStatusCode.ServiceUnavailable,ready.StatusCode);using var traffic=await s.RequestAsync();Assert.Equal(HttpStatusCode.ServiceUnavailable,traffic.StatusCode);using var healthPrefixBusiness=await s.RequestAsync(path:"/health/orders");Assert.Equal(HttpStatusCode.ServiceUnavailable,healthPrefixBusiness.StatusCode);}
    private static async Task WaitReadyAsync(GatewayFixture s)
    {var until=DateTimeOffset.UtcNow.AddSeconds(12);while(DateTimeOffset.UtcNow<until) {using var ready=await s.Clients[0].GetAsync("/health/ready");if(ready.IsSuccessStatusCode) return;await Task.Delay(50);}throw new TimeoutException("LKG gateway did not become ready.");}
    [Fact] public async Task RestartRejectsNewLkgButRestoresPriorUsableCopy()
    {await using var s=new GatewayFixture();await s.InitializeAsync();var first=await s.PublishAsync();await s.ApplyBothAsync(first);var second=await s.PublishAsync(1);await s.ApplyBothAsync(second);s.ConfigureGateway=b=>b.Services.AddSingleton<IProxyConfigFilter>(new RejectSequenceFilter());await s.RestartAsync(0);await WaitReadyAsync(s);using var response=await s.RequestAsync();response.EnsureSuccessStatusCode();Assert.Equal("A",(await response.Content.ReadFromJsonAsync<BackendResponse>())!.BackendId);Assert.Equal(first.Envelope,s.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!.Envelope);}
    [Fact] public async Task RuntimeCollectionsCannotMutateAfterActivation()
    {await using var s=new GatewayFixture();await s.InitializeAsync();await s.ApplyBothAsync(await s.PublishAsync());var current=s.Gateways[0].Services.GetRequiredService<RuntimeGenerationStore>().Current!;Assert.Throws<NotSupportedException>(()=>((IList<RuntimeRoute>)current.Snapshot.Routes).Clear());Assert.Throws<NotSupportedException>(()=>((IList<string>)current.Snapshot.Routes[0].Methods).Clear());Assert.Throws<NotSupportedException>(()=>((IList<RuntimeCredential>)current.Snapshot.Applications[0].Credentials).Clear());using var traffic=await s.RequestAsync();traffic.EnsureSuccessStatusCode();}
    [Fact] public async Task RoundRobinActuallyUsesDestinationWeights()
    {await using var s=new GatewayFixture();await s.InitializeAsync();using var created=await s.Control.WriteAsync(HttpMethod.Post,$"/api/v1/clusters/{s.Control.Cluster.Id}/destinations",new {name="weighted-B",address=s.BackendUrls[1]+"/",weight=3,enabled=true});created.EnsureSuccessStatusCode();await s.ApplyBothAsync(await s.PublishAsync());var names=new List<string>();for(var i=0;i<8;i++) {using var response=await s.RequestAsync();response.EnsureSuccessStatusCode();names.Add((await response.Content.ReadFromJsonAsync<BackendResponse>())!.BackendId);}Assert.Equal(2,names.Count(x=>x=="A"));Assert.Equal(6,names.Count(x=>x=="B"));}
    [Fact] public async Task CorruptLatestFileRestoresPreviousLkg()
    {await using var s=new GatewayFixture();await s.InitializeAsync();var first=await s.PublishAsync();await s.ApplyBothAsync(first);await s.ApplyBothAsync(await s.PublishAsync(1));await File.WriteAllTextAsync(s.Gateways[0].Services.GetRequiredService<LkgStore>().FilePath,"truncated snapshot");await s.RestartAsync(0);await WaitReadyAsync(s);using var response=await s.RequestAsync();response.EnsureSuccessStatusCode();Assert.Equal("A",(await response.Content.ReadFromJsonAsync<BackendResponse>())!.BackendId);}
}

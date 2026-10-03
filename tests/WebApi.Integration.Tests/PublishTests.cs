using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Infrastructure.Releases;
using WebApi.Infrastructure.Messaging;
using WebApi.Infrastructure.Runtime;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Integration.Tests.Support;
using Xunit;
namespace WebApi.Integration.Tests;
public sealed class PublishTests
{
    private static async Task<ApiFixture> PrepareAsync()
    {
        var api=new ApiFixture();await api.InitializeAsync();await api.SeedReleaseAsync();using var login=await api.LoginAsync();login.EnsureSuccessStatusCode();
        using var route=await api.WriteAsync(HttpMethod.Post,$"/api/v1/environments/{api.Environment.Id}/routes",api.RouteBody("/orders"));route.EnsureSuccessStatusCode();
        await using var db=api.Context();for(var i=0;i<2;i++) db.Add(new GatewayNode {EnvironmentId=api.Environment.Id,NodeName="node-"+i,InstanceId=Guid.NewGuid().ToString(),AppVersion="test",IdentityHash=Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32))),LastHeartbeatAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();return api;
    }
    private static async Task<Guid> ReadyAsync(ApiFixture api)
    {
        var id=await api.CreateSubmittedReleaseAsync();var first=await api.NewReviewerAsync("ApiApprover");var second=await api.NewReviewerAsync("SecurityReviewer");
        using var a=await ApiFixture.CommandAsync(first.Client,$"/api/v1/releases/{id}/approve",new {comment="API审核"});a.EnsureSuccessStatusCode();using var b=await ApiFixture.CommandAsync(second.Client,$"/api/v1/releases/{id}/approve",new {comment="安全审核"});b.EnsureSuccessStatusCode();return id;
    }
    private static Task<HttpResponseMessage> PublishAsync(ApiFixture api,Guid id)=>ApiFixture.CommandAsync(api.Client,$"/api/v1/releases/{id}/publish");
    private static async Task BuildAsync(ApiFixture api)
    {using var scope=api.Services();Assert.True(await scope.ServiceProvider.GetRequiredService<PublishCoordinator>().BuildNextAsync());}
    [Fact] public async Task OnlyOnePublishPerEnvironment()
    {
        await using var api=await PrepareAsync();var a=await ReadyAsync(api);var b=await ReadyAsync(api);var responses=await Task.WhenAll(PublishAsync(api,a),PublishAsync(api,b));
        try {Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.Conflict);}finally {foreach(var r in responses) r.Dispose();}
        await using var db=api.Context();Assert.Equal(1,await db.Set<ReleaseRecord>().CountAsync(r=>r.Status=="Building"));Assert.Equal(0,await db.Set<GatewayConfigVersion>().CountAsync());
    }
    [Fact] public async Task StaleBaselineReturns409()
    {
        await using var api=await PrepareAsync();var id=await ReadyAsync(api);await using(var db=api.Context()) {var env=await db.Set<EnvironmentRecord>().SingleAsync();env.DesiredConfigVersion=3;await db.SaveChangesAsync();}
        using var response=await PublishAsync(api,id);Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);await using var check=api.Context();Assert.Equal("Ready",(await check.Set<ReleaseRecord>().SingleAsync()).Status);Assert.False(await check.Set<OutboxMessage>().AnyAsync());
    }
    [Fact] public async Task DatabaseCommitRedisFailureIsRetried()
    {
        await using var api=await PrepareAsync();var id=await ReadyAsync(api);using var response=await PublishAsync(api,id);response.EnsureSuccessStatusCode();await BuildAsync(api);
        await using(var db=api.Context()) {using var bad=new RedisSnapshotStore("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200,asyncTimeout=200,connectRetry=0","test:"+Guid.NewGuid());var dispatcher=new OutboxDispatcher(db,bad);Assert.False(await dispatcher.DispatchNextAsync());Assert.Equal(1,await db.Set<GatewayConfigVersion>().CountAsync());Assert.Null((await db.Set<OutboxMessage>().SingleAsync()).ProcessedAt);}
        await using(var db=api.Context()) {using var redis=new RedisSnapshotStore("redis:6379","test:"+Guid.NewGuid());var dispatcher=new OutboxDispatcher(db,redis);Assert.True(await dispatcher.DispatchNextAsync());var desired=await redis.GetDesiredAsync(api.Environment.Id);Assert.NotNull(desired);Assert.Equal(id,desired.Envelope.ReleaseId);Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(desired.Payload)),desired.Envelope.PayloadHash);Assert.Equal(1,await db.Set<GatewayConfigVersion>().CountAsync());Assert.NotNull((await db.Set<OutboxMessage>().AsNoTracking().SingleAsync()).ProcessedAt);Assert.Equal(2,await db.Set<ReleaseTarget>().CountAsync());}
    }
    [Fact] public async Task WorkerCrashAfterRedisWriteReusesSameSnapshot()
    {
        await using var api=await PrepareAsync();var id=await ReadyAsync(api);using var publish=await PublishAsync(api,id);publish.EnsureSuccessStatusCode();await BuildAsync(api);using var redis=new RedisSnapshotStore("redis:6379","test:"+Guid.NewGuid());DesiredConfigResponse original;
        await using(var db=api.Context()) {var dispatcher=new OutboxDispatcher(db,redis,()=>throw new InvalidOperationException("simulated worker termination after durable cache write"));await Assert.ThrowsAsync<InvalidOperationException>(()=>dispatcher.DispatchNextAsync());original=(await redis.GetDesiredAsync(api.Environment.Id))!;await db.Set<OutboxMessage>().ExecuteUpdateAsync(s=>s.SetProperty(m=>m.LeaseUntil,DateTimeOffset.UtcNow.AddMinutes(-1)));}
        await using(var db=api.Context()) {var dispatcher=new OutboxDispatcher(db,redis);Assert.True(await dispatcher.DispatchNextAsync());var after=(await redis.GetDesiredAsync(api.Environment.Id))!;Assert.Equal(original.Envelope,after.Envelope);Assert.Equal(original.Payload,after.Payload);Assert.Equal(1,await db.Set<GatewayConfigVersion>().CountAsync());Assert.Equal(2,(await db.Set<OutboxMessage>().AsNoTracking().SingleAsync()).Attempts);Assert.False(await dispatcher.DispatchNextAsync());}
    }
    [Fact] public async Task OlderOutboxCannotOverwriteNewDesired()
    {
        await using var api=await PrepareAsync();var id=await ReadyAsync(api);using var publish=await PublishAsync(api,id);publish.EnsureSuccessStatusCode();await BuildAsync(api);using var redis=new RedisSnapshotStore("redis:6379","test:"+Guid.NewGuid());
        await using var db=api.Context();var snapshot=await db.Set<GatewayConfigSnapshot>().SingleAsync();var config=await db.Set<GatewayConfigVersion>().SingleAsync();var newer=new SnapshotEnvelope(Guid.NewGuid(),9007199254740993L,config.VersionNo,config.SnapshotHash!,snapshot.SizeBytes);await redis.PutAsync(api.Environment.Id,newer,snapshot.PayloadBytes);var dispatcher=new OutboxDispatcher(db,redis);Assert.True(await dispatcher.DispatchNextAsync());Assert.Equal(newer,(await redis.GetDesiredAsync(api.Environment.Id))!.Envelope);
        // Adjacent int64 sequences above Lua's exact floating point range remain strictly ordered.
        await redis.PutAsync(api.Environment.Id,newer with {DeploymentSequence=9007199254740992L,ReleaseId=Guid.NewGuid()},snapshot.PayloadBytes);Assert.Equal(newer,(await redis.GetDesiredAsync(api.Environment.Id))!.Envelope);
    }
    [Fact] public async Task PublishPermissionRevokedWhileQueuedStopsDispatch()
    {
        await using var api=await PrepareAsync();var id=await ReadyAsync(api);var publisher=await api.NewReviewerAsync("ReleaseOperator");
        await using(var db=api.Context()) {var role=await db.Set<Role>().SingleAsync(r=>r.Code=="ReleaseOperator");var permission=await db.Set<Permission>().SingleAsync(p=>p.Code=="release.publish");db.Add(new RolePermission {RoleId=role.Id,PermissionId=permission.Id});await db.SaveChangesAsync();}
        using var publish=await ApiFixture.CommandAsync(publisher.Client,$"/api/v1/releases/{id}/publish");publish.EnsureSuccessStatusCode();
        await using(var db=api.Context()) {await db.Set<UserRole>().Where(r=>r.UserId==publisher.User.Id).ExecuteDeleteAsync();}
        await BuildAsync(api);await using var check=api.Context();var r=await check.Set<ReleaseRecord>().SingleAsync();Assert.Equal(publisher.User.Id,r.PublishRequestedBy);Assert.Equal("Failed",r.Status);Assert.Equal("permission_denied",r.FailureCode);Assert.Null((await check.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion);Assert.False(await check.Set<GatewayConfigVersion>().AnyAsync());Assert.False(await check.Set<OutboxMessage>().AnyAsync());
    }
    [Fact] public async Task DesiredCacheRebuildsFromCommittedDatabase()
    {
        await using var api=await PrepareAsync();var id=await ReadyAsync(api);using var publish=await PublishAsync(api,id);publish.EnsureSuccessStatusCode();await BuildAsync(api);using var emptyCache=new RedisSnapshotStore("redis:6379","test:"+Guid.NewGuid());Assert.Null(await emptyCache.GetDesiredAsync(api.Environment.Id));await using var db=api.Context();await new OutboxDispatcher(db,emptyCache).ReconcileDesiredAsync();var desired=(await emptyCache.GetDesiredAsync(api.Environment.Id))!;Assert.Equal(id,desired.Envelope.ReleaseId);Assert.Equal((await db.Set<GatewayConfigSnapshot>().SingleAsync()).PayloadBytes,desired.Payload);
    }
    [Fact] public async Task MissingOrOfflineNodesCannotReduceFrozenCohort()
    {
        await using var api=await PrepareAsync();var id=await ReadyAsync(api);await using(var db=api.Context()) {var node=await db.Set<GatewayNode>().FirstAsync();node.LastHeartbeatAt=DateTimeOffset.UtcNow.AddMinutes(-20);await db.SaveChangesAsync();}using var result=await PublishAsync(api,id);Assert.Equal(HttpStatusCode.Conflict,result.StatusCode);await using var check=api.Context();Assert.Equal("Ready",(await check.Set<ReleaseRecord>().SingleAsync()).Status);Assert.Null((await check.Set<EnvironmentRecord>().SingleAsync()).DesiredConfigVersion);
    }
    [Fact] public async Task VersionChangedSinceApprovalDoesNotOverwriteDraft()
    {
        await using var api=await PrepareAsync();var id=await ReadyAsync(api);await using(var db=api.Context()) {var version=await db.Set<ApiVersion>().SingleAsync();version.ChangeType="breaking";version.Revision++;await db.SaveChangesAsync();}using var result=await PublishAsync(api,id);Assert.Equal(HttpStatusCode.Conflict,result.StatusCode);await using var check=api.Context();var v=await check.Set<ApiVersion>().SingleAsync();Assert.Equal("breaking",v.ChangeType);Assert.Null(v.SealedAt);Assert.False(await check.Set<GatewayConfigVersion>().AnyAsync());
    }
}

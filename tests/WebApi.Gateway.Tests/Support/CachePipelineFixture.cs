using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
using WebApi.Contracts.Runtime;
using WebApi.Gateway.Configuration;
using WebApi.Gateway.Policies;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Releases;
using WebApi.Integration.Tests.Support;
using Xunit;
using SigningKey = WebApi.Gateway.Tests.JwtTokenVerifierTests.KeyFixture;
namespace WebApi.Gateway.Tests.Support;
internal sealed class CachePipelineFixture : IAsyncDisposable
{
    internal GatewayFixture F {get;}
    internal int Calls;
    internal ConcurrentQueue<string> Errors=[];
    internal string Prefix="cache-pipeline-"+Guid.NewGuid().ToString("N");
    internal List<ObservedStore> Stores=[];
    internal CountingLimiter Limiter=new();
    internal RetryPipelineTests.DelayClock Clock=new();
    internal Func<HttpContext,int,Task>? Respond;
    internal CachePipelineFixture()
    {
        F=new GatewayFixture { ConfigureGateway=b=>{
            var file=Path.Combine(F!.Directory,"cache.secret");
            if(!File.Exists(file)){File.WriteAllText(file,Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));if(!OperatingSystem.IsWindows())File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(F.Directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            b.Configuration["GatewayPolicies:Cache:HmacSecretFile"]=file;b.Configuration["GatewayPolicies:Cache:KeyPrefix"]=Prefix;b.Configuration["GatewayPolicies:Cache:RedisConnection"]="redis:6379";
            b.Logging.AddProvider(new ErrorCollector(Errors));
            b.Services.AddSingleton<IRateLimitStore>(Limiter);b.Services.AddSingleton<TimeProvider>(Clock);
            b.Services.AddSingleton<IResponseCacheStore>(sp=>{var store=new ObservedStore(new RedisResponseCacheStore(sp.GetRequiredService<GatewayCacheSettings>()));lock(Stores)Stores.Add(store);return store;});
        },ConfigureBackendA=a=>a.Use(async(ctx,next)=>{
            if(ctx.Request.Path=="/health"){await next();return;}
            var n=Interlocked.Increment(ref Calls);
            ctx.Response.Headers.CacheControl="public,max-age=60";ctx.Response.Headers["X-WebApi-Trace-Id"]="upstream-private-trace";ctx.Response.Headers["X-WebApi-Fake"]="upstream-fake";
            if(Respond is not null)await Respond(ctx,n);else await ctx.Response.WriteAsync("business-data-"+n);
        }) };
    }
    internal async Task Init(bool circuit=false,bool rate=false,SigningKey? jwt=null,bool forward=false,int maxEntry=65536)
    {
        await F.InitializeAsync();await using var db=F.Control.Context();
        var roleId=await db.Set<UserRole>().Where(r=>r.UserId==F.Control.User.Id).Select(r=>r.RoleId).SingleAsync();
        var permission=new Permission{Code="gateway.config.read",Module="gateway",Name="历史配置读取"};db.Add(permission);db.Add(new RolePermission{RoleId=roleId,PermissionId=permission.Id});
        void Add(string type,string config){var p=new Policy{OrganizationId=F.Control.Organization.Id,ProjectId=F.Control.Project.Id,Name=type,Type=type,Config=config};db.Add(p);db.Add(new RoutePolicyBinding{RouteId=F.RouteId,PolicyId=p.Id});}
        Add("cache",JsonSerializer.Serialize(CacheEligibilityTests.Config with{TtlSeconds=60,MaxEntryBytes=maxEntry,VaryHeaders=[]},new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if(circuit)Add("circuit_breaker","{\"samplingWindowMs\":30000,\"minimumRequests\":2,\"failureRatio\":0.5,\"openDurationMs\":1000,\"halfOpenMaxRequests\":1,\"halfOpenSuccesses\":1,\"failureStatusCodes\":[503],\"countTimeouts\":true,\"countConnectionFailures\":true}");
        if(rate)Add("rate_limit","{\"algorithm\":\"TokenBucket\",\"keyBy\":\"ApplicationRoute\",\"refillTokens\":1,\"windowMs\":600000,\"burst\":3,\"redisFailureMode\":\"Reject\"}");
        if(jwt is not null){db.RemoveRange(await db.Set<RoutePolicyBinding>().Where(b=>b.RouteId==F.RouteId).ToArrayAsync());Add("authentication",PolicyConfigurationValidator.Normalize("authentication",JsonSerializer.Serialize(jwt.Configuration() with {ApplicationMappings=[new("private-app-canary-8362",F.Control.Application.Id)],ForwardBearer=forward},new JsonSerializerOptions(JsonSerializerDefaults.Web))));}
        (await db.Set<ApiRoute>().SingleAsync(r=>r.Id==F.RouteId)).Revision++;await db.SaveChangesAsync();
        await F.ApplyBothAsync(await F.PublishAsync());
    }
    internal Task<HttpResponseMessage> Get(int node=0,string path="/orders",string? token=null,string? credential=null,CancellationToken ct=default,HttpCompletionOption option=HttpCompletionOption.ResponseContentRead)
    {
        var r=new HttpRequestMessage(HttpMethod.Get,path);r.Headers.Host="api.example.test";
        if(token is not null)r.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);else r.Headers.Add("X-API-Key",credential??F.Credential);
        return F.Clients[node].SendAsync(r,option,ct);
    }
    internal static string Token(SigningKey key,string subject="user-1",string nonce="first")
    {var c=key.Claims();var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();c["iat"]=now-60;c["exp"]=now+1800;c["sub"]=subject;c["jti"]=nonce;return key.Token(c);}
    internal async Task WaitWrites(int n)
    {for(var i=0;i<100;i++){if(Stores.Sum(s=>Volatile.Read(ref s.Writes))>=n)return;await Task.Delay(20);}Assert.Fail("Expected cache writes did not complete.");}
    internal async Task<DesiredConfigResponse> Rollback(DesiredConfigResponse from,long target)
    {
        using var created=await ApiFixture.CommandAsync(F.Control.Client,$"/api/v1/releases/{from.Envelope.ReleaseId}/rollback",new{targetConfigVersion=target});created.EnsureSuccessStatusCode();var id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var submit=await ApiFixture.CommandAsync(F.Control.Client,$"/api/v1/releases/{id}/submit");submit.EnsureSuccessStatusCode();
        foreach(var role in new[]{"ApiApprover","SecurityReviewer"}){var reviewer=await F.Control.NewReviewerAsync(role);using var approved=await ApiFixture.CommandAsync(reviewer.Client,$"/api/v1/releases/{id}/approve",new{comment="回滚审核"});approved.EnsureSuccessStatusCode();}
        using var publish=await ApiFixture.CommandAsync(F.Control.Client,$"/api/v1/releases/{id}/publish");publish.EnsureSuccessStatusCode();using(var scope=F.Control.Services())await scope.ServiceProvider.GetRequiredService<PublishCoordinator>().BuildNextAsync();
        await using var db=F.Control.Context();var release=await db.Set<ReleaseRecord>().SingleAsync(r=>r.Id==id);var config=await db.Set<GatewayConfigVersion>().SingleAsync(v=>v.VersionNo==target);var snapshot=await db.Set<GatewayConfigSnapshot>().SingleAsync(s=>s.ConfigVersionId==config.Id);
        return new(new(id,release.DeploymentSequence!.Value,target,config.SnapshotHash!,snapshot.SizeBytes),snapshot.PayloadBytes);
    }
    public async ValueTask DisposeAsync()
    {await F.DisposeAsync();using var mux=await ConnectionMultiplexer.ConnectAsync("redis:6379");var keys=mux.GetServer(mux.GetEndPoints().Single()).Keys(pattern:Prefix+":*").ToArray();if(keys.Length>0)await mux.GetDatabase().KeyDeleteAsync(keys);}
    private sealed class ErrorCollector(ConcurrentQueue<string> errors):ILoggerProvider
    {public ILogger CreateLogger(string categoryName)=>new ErrorLog(errors);public void Dispose(){}private sealed class ErrorLog(ConcurrentQueue<string> errors):ILogger{public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;public bool IsEnabled(LogLevel level)=>level>=LogLevel.Error;public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter){if(exception is not null)errors.Enqueue(exception.GetType().Name+" "+exception.StackTrace);}}}
    internal sealed class CountingLimiter:IRateLimitStore
    {internal ConcurrentQueue<RateLimitRequest> Requests=[];internal bool Reject;public ValueTask<RateLimitDecision> TakeAsync(RateLimitRequest r,CancellationToken ct){Requests.Enqueue(r);return ValueTask.FromResult(new RateLimitDecision(Reject?RateLimitDecisionKind.Exceeded:RateLimitDecisionKind.Allowed));}}
    internal sealed class ObservedStore(RedisResponseCacheStore inner):IResponseCacheStore,IDisposable
    {internal int Writes;internal ConcurrentQueue<CacheReadKind> Reads=[];public async ValueTask<CacheReadResult> GetAsync(CacheLookupKey key,CancellationToken ct){var r=await inner.GetAsync(key,ct);Reads.Enqueue(r.Kind);return r;}public async ValueTask<CacheWriteResult> PutAsync(CacheLookupKey key,CacheEntry entry,CancellationToken ct){var r=await inner.PutAsync(key,entry,ct);Interlocked.Increment(ref Writes);return r;}public void Dispose()=>inner.Dispose();}
}

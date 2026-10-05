using System.Net.Sockets;
using StackExchange.Redis;
using WebApi.Contracts.Policies;
namespace WebApi.Gateway.Policies;
public sealed class RedisTokenBucketStore : IRateLimitStore,IDisposable
{
    private readonly ITokenBucketExecutor executor;private readonly string prefix;private readonly TimeSpan budget;private readonly IDisposable? owned;
    public RedisTokenBucketStore(TrafficPolicySettings settings)
    {var redis=new RedisExecutor(settings.RedisConnection);executor=redis;owned=redis;prefix=settings.KeyPrefix;budget=TimeSpan.FromMilliseconds(settings.DecisionTimeoutMs);}
    public RedisTokenBucketStore(ITokenBucketExecutor executor,string prefix,TimeSpan budget)
    {this.executor=executor;this.prefix=prefix;this.budget=budget;}
    public async ValueTask<RateLimitDecision> TakeAsync(RateLimitRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var app=request.Configuration.KeyBy=="ApplicationRoute"?request.ApplicationId:null;
        if(request.EnvironmentId==Guid.Empty||request.RuntimePolicyId==Guid.Empty||request.RouteId==Guid.Empty||request.Configuration.KeyBy=="ApplicationRoute"&&(app is null||app==Guid.Empty)) return new(RateLimitDecisionKind.StoreUnavailable);
        var key=$"{prefix}:{{{request.EnvironmentId:N}}}:policy:{request.RuntimePolicyId:N}:route:{request.RouteId:N}"+(app is Guid id?$":app:{id:N}":"");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(budget);
        try {
            var result=await executor.TakeAsync(key,request.Configuration,timeout.Token).WaitAsync(timeout.Token);
            if(result.Length!=2||result[0] is <0 or >1||result[1]<0) return new(RateLimitDecisionKind.StoreUnavailable);
            return result[0]==1?new(RateLimitDecisionKind.Allowed):new(RateLimitDecisionKind.Exceeded,(int)Math.Clamp((result[1]+999)/1000,1,600));
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) {return new(RateLimitDecisionKind.StoreUnavailable);}
        catch(Exception ex) when(ex is RedisException or TimeoutException or SocketException or InvalidOperationException) {return new(RateLimitDecisionKind.StoreUnavailable);}
    }
    public void Dispose()=>owned?.Dispose();
    private sealed class RedisExecutor(string connection) : ITokenBucketExecutor,IDisposable
    {
        private readonly Lazy<Task<ConnectionMultiplexer>> mux=new(()=>ConnectionMultiplexer.ConnectAsync(connection),LazyThreadSafetyMode.ExecutionAndPublication);
        public async Task<long[]> TakeAsync(string key,RateLimitConfiguration config,CancellationToken ct)
        {var connected=await mux.Value.WaitAsync(ct);ct.ThrowIfCancellationRequested();var result=await connected.GetDatabase().ScriptEvaluateAsync(TokenBucketScript.Text,[key],[config.RefillTokens,config.WindowMs,config.Burst]);return ((RedisResult[])result!).Select(v=>(long)v).ToArray();}
        public void Dispose() {if(mux.IsValueCreated) _=mux.Value.ContinueWith(t=>{if(t.IsCompletedSuccessfully) t.Result.Dispose();else _=t.Exception;},TaskScheduler.Default);}
    }
}

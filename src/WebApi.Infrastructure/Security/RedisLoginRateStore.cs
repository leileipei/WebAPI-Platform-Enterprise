using StackExchange.Redis;
using WebApi.Contracts.Security;
using WebApi.Domain.Security;
namespace WebApi.Infrastructure.Security;
public sealed class RedisLoginRateStore : ILoginRateStore,IDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;
    private readonly string prefix;
    public RedisLoginRateStore(string connectionString,string prefix)
    {
        if(prefix.Length is <1 or >128||prefix.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_' and not ':'))throw new ArgumentException("Invalid login protection namespace.",nameof(prefix));
        this.prefix=prefix;
        var options=ConfigurationOptions.Parse(connectionString);options.AbortOnConnectFail=false;options.ConnectRetry=0;options.ConnectTimeout=1000;options.AsyncTimeout=1000;
        connection=new(()=>ConnectionMultiplexer.ConnectAsync(options));
    }
    public RedisKey[] BudgetKeys(LoginRateRequest request)
    {
        static bool Valid(string s)=>s.Length==64&&s.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
        if(!Valid(request.Identity.Ip)||!Valid(request.Identity.Account))throw new ArgumentException("Invalid login identity fingerprint.");
        var policy=LoginProtectionRules.Fingerprint(request.Limits);
        return [$"{prefix}:{{login}}:{policy}:ip:{request.Identity.Ip}",$"{prefix}:{{login}}:{policy}:account:{request.Identity.Account}"];
    }
    public async ValueTask<LoginRateDecision> TryAcquireAsync(LoginRateRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var keys=BudgetKeys(request);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(1000);
        try
        {
            var mux=await connection.Value.WaitAsync(timeout.Token);var l=request.Limits;
            var raw=await mux.GetDatabase().ScriptEvaluateAsync(LoginBudgetScript.Value,keys,[l.IpMaxAttempts,l.IpWindowSeconds,l.AccountMaxAttempts,l.AccountWindowSeconds]).WaitAsync(timeout.Token);
            var result=(RedisResult[])raw!;
            if(result.Length==2 && (long)result[0]==1 && (long)result[1]==0)return new(LoginRateDecisionKind.Allowed);
            if(result.Length==2 && (long)result[0]==0 && (long)result[1] is >=1 and <=3600)return new(LoginRateDecisionKind.Limited,(int)(long)result[1]);
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
        catch(Exception e) when(e is RedisException or TimeoutException or InvalidCastException or OverflowException) { }
        ct.ThrowIfCancellationRequested();return new(LoginRateDecisionKind.Unavailable);
    }
    public void Dispose()
    {
        if(connection.IsValueCreated)_=connection.Value.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();},TaskScheduler.Default);
    }
}

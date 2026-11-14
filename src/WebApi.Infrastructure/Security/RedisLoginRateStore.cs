using StackExchange.Redis;
using WebApi.Contracts.Security;
using WebApi.Domain.Security;
namespace WebApi.Infrastructure.Security;
public sealed class RedisLoginRateStore : ILoginRateStore,IDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;
    private readonly string prefix;
    private readonly IAuthenticationDiagnosticSink? diagnostics;
    public RedisLoginRateStore(string connectionString,string prefix,IAuthenticationDiagnosticSink? diagnostics=null)
    {
        if(prefix.Length is <1 or >128||prefix.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_' and not ':'))throw new ArgumentException("Invalid login protection namespace.",nameof(prefix));
        this.prefix=prefix;
        this.diagnostics=diagnostics;
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
        using var total=AuthenticationDiagnostics.Start(diagnostics,AuthenticationDiagnosticPhase.RateTotal,1000);
        try
        {
            ct.ThrowIfCancellationRequested();var keys=BudgetKeys(request);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(1000);
            total?.BudgetStarted();
            var luaStarted=false;var decodeStarted=false;
            try
            {
                ConnectionMultiplexer mux;
                using(var phase=AuthenticationDiagnostics.Start(diagnostics,AuthenticationDiagnosticPhase.RateConnect,1000,connection.IsValueCreated))
                {
                    try { mux=await connection.Value.WaitAsync(timeout.Token); }
                    catch(Exception error) { phase?.Fail(error,ct,timeout.Token);throw; }
                }
                var l=request.Limits;RedisResult raw;luaStarted=true;
                using(var phase=AuthenticationDiagnostics.Start(diagnostics,AuthenticationDiagnosticPhase.RateLua,1000))
                {
                    try { raw=await mux.GetDatabase().ScriptEvaluateAsync(LoginBudgetScript.Value,keys,[l.IpMaxAttempts,l.IpWindowSeconds,l.AccountMaxAttempts,l.AccountWindowSeconds]).WaitAsync(timeout.Token); }
                    catch(Exception error) { phase?.Fail(error,ct,timeout.Token);throw; }
                }
                decodeStarted=true;
                using(var phase=AuthenticationDiagnostics.Start(diagnostics,AuthenticationDiagnosticPhase.RateDecode))
                {
                    try
                    {
                        var result=(RedisResult[])raw!;
                        if(result.Length==2 && (long)result[0]==1 && (long)result[1]==0)return new(LoginRateDecisionKind.Allowed);
                        if(result.Length==2 && (long)result[0]==0 && (long)result[1] is >=1 and <=3600)return new(LoginRateDecisionKind.Limited,(int)(long)result[1]);
                        phase?.Mark(AuthenticationDiagnosticError.ResultShape);total?.Mark(AuthenticationDiagnosticError.ResultShape);
                    }
                    catch(Exception error) { phase?.Fail(error,ct,timeout.Token);throw; }
                }
            }
            catch(OperationCanceledException error) when(!ct.IsCancellationRequested) { total?.Fail(error,ct,timeout.Token); }
            catch(Exception error) when(error is RedisException or TimeoutException or InvalidCastException or OverflowException) { total?.Fail(error,ct,timeout.Token); }
            finally
            {
                if(!luaStarted)AuthenticationDiagnostics.NotReached(diagnostics,AuthenticationDiagnosticPhase.RateLua);
                if(!decodeStarted)AuthenticationDiagnostics.NotReached(diagnostics,AuthenticationDiagnosticPhase.RateDecode);
            }
            ct.ThrowIfCancellationRequested();return new(LoginRateDecisionKind.Unavailable);
        }
        catch(Exception error) { total?.Fail(error,ct);throw; }
    }
    public void Dispose()
    {
        if(connection.IsValueCreated)_=connection.Value.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();},TaskScheduler.Default);
    }
}

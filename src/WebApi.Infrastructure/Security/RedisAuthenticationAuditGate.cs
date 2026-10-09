using System.Globalization;
using StackExchange.Redis;
namespace WebApi.Infrastructure.Security;
public sealed record AuthenticationAuditLease(string BucketKey,string Token,DateTimeOffset FirstRejectedAt);
public interface IAuthenticationAuditGate
{
    ValueTask<AuthenticationAuditLease?> ClaimAsync(string ipFingerprint,CancellationToken ct);
    ValueTask ConfirmAsync(AuthenticationAuditLease lease,CancellationToken ct);
    ValueTask ReleaseAsync(AuthenticationAuditLease lease,CancellationToken ct);
}
public sealed class RedisAuthenticationAuditGate : IAuthenticationAuditGate,IDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> connection;
    private readonly string prefix;
    public RedisAuthenticationAuditGate(string connectionString,string prefix)
    {
        if(prefix.Length is <1 or >128||prefix.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_' and not ':'))throw new ArgumentException("Invalid authentication audit namespace.");
        this.prefix=prefix;var options=ConfigurationOptions.Parse(connectionString);options.AbortOnConnectFail=false;options.ConnectTimeout=1000;options.AsyncTimeout=1000;options.ConnectRetry=0;connection=new(()=>ConnectionMultiplexer.ConnectAsync(options));
    }
    private const string ClaimScript="""
        local t=redis.call('TIME') local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
        local bucket=math.floor(now/60000) local ending=(bucket+1)*60000
        local old=tonumber(redis.call('HGET',KEYS[1],'bucket'))
        if old~=bucket then redis.call('DEL',KEYS[1]) redis.call('HSET',KEYS[1],'bucket',bucket,'first',now) end
        local status=redis.call('HGET',KEYS[1],'status')
        local untilAt=tonumber(redis.call('HGET',KEYS[1],'until')) or 0
        if status=='done' or (status=='held' and untilAt>now) then return {} end
        redis.call('HSET',KEYS[1],'status','held','token',ARGV[1],'until',math.min(now+5000,ending))
        redis.call('PEXPIREAT',KEYS[1],ending)
        return {bucket,tonumber(redis.call('HGET',KEYS[1],'first'))}
        """;
    private const string FinishScript="""
        local t=redis.call('TIME') local now=tonumber(t[1])*1000+math.floor(tonumber(t[2])/1000)
        if redis.call('HGET',KEYS[1],'bucket')~=ARGV[1] or redis.call('HGET',KEYS[1],'token')~=ARGV[2] or tonumber(redis.call('HGET',KEYS[1],'until') or '0')<=now then return 0 end
        if ARGV[3]=='confirm' then redis.call('HSET',KEYS[1],'status','done') else redis.call('HDEL',KEYS[1],'status') end
        redis.call('HDEL',KEYS[1],'token','until') return 1
        """;
    public async ValueTask<AuthenticationAuditLease?> ClaimAsync(string ipFingerprint,CancellationToken ct)
    {
        if(ipFingerprint.Length!=64||ipFingerprint.Any(c=>c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))throw new ArgumentException("Invalid authentication IP fingerprint.");
        var key=$"{prefix}:{{login}}:audit:{ipFingerprint}";var token=Guid.NewGuid().ToString("N");
        var result=(RedisResult[]?)await Evaluate(ClaimScript,key,[token],ct)??throw new InvalidOperationException("Invalid authentication audit lease result.");
        if(result.Length==0)return null;
        if(result.Length!=2)throw new InvalidOperationException("Invalid authentication audit lease result.");
        return new(key+"|"+((long)result[0]).ToString(CultureInfo.InvariantCulture),token,DateTimeOffset.FromUnixTimeMilliseconds((long)result[1]));
    }
    public ValueTask ConfirmAsync(AuthenticationAuditLease lease,CancellationToken ct)=>Finish(lease,"confirm",ct);
    public ValueTask ReleaseAsync(AuthenticationAuditLease lease,CancellationToken ct)=>Finish(lease,"release",ct);
    private async ValueTask Finish(AuthenticationAuditLease lease,string operation,CancellationToken ct)
    {
        var parts=lease.BucketKey.Split('|');if(parts.Length!=2||!parts[0].StartsWith(prefix+":{login}:audit:",StringComparison.Ordinal)||!long.TryParse(parts[1],out _))throw new ArgumentException("Invalid authentication audit lease.");
        await Evaluate(FinishScript,parts[0],[parts[1],lease.Token,operation],ct);
    }
    private async Task<RedisResult> Evaluate(string script,string key,RedisValue[] args,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(1000);
        var mux=await connection.Value.WaitAsync(timeout.Token);return await mux.GetDatabase().ScriptEvaluateAsync(script,[key],args).WaitAsync(timeout.Token);
    }
    public void Dispose(){if(connection.IsValueCreated)_=connection.Value.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();},TaskScheduler.Default);}
}
